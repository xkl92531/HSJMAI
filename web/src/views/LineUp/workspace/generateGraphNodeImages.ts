import type { LineUpImportSequence } from '@/api/modules/LineUp/types'
import type { EntityId } from '@/api/modules/LineUp/types'
import graphNodeImageApi from '@/api/modules/LineUp/graphNodeImages'
import imageMarkApi from '@/api/modules/LineUp/imageMarks'
import { selectTiaGraphElement, symbolNamesForSelection, withAdjacentTransitionNetworks } from '../components/tiaGraphSelection'

interface GenerateOptions {
  batchId: EntityId
  lineId: EntityId
  deviceId: EntityId
  blockName: string
  sequences: LineUpImportSequence[]
  /** 顺控 → 子工站 id（层级候选里选定的） */
  subStationBySequence: Map<string, EntityId>
}

function referenceKeys(references: string[]) {
  const keys = new Set<string>()
  for (const code of references) {
    const value = code.trim().replaceAll('"', '').toLowerCase()
    if (value) {
      keys.add(value)
    }
  }
  return keys
}

function loadImage(url: string) {
  return new Promise<HTMLImageElement>((resolve, reject) => {
    const el = new Image()
    el.onload = () => resolve(el)
    el.onerror = reject
    el.src = url
  })
}

/** 成品图 + 红点（照片原点 + 标记坐标），返回 PNG base64（不含 data: 前缀） */
function renderNodeSnapshot(img: HTMLImageElement, offset: { x: number, y: number }, points: Array<{ x: number, y: number }>) {
  const canvas = document.createElement('canvas')
  canvas.width = img.naturalWidth
  canvas.height = img.naturalHeight
  const ctx = canvas.getContext('2d')
  if (!ctx) {
    return null
  }
  ctx.drawImage(img, 0, 0)
  // 实心红点 + 白描边：与成品图里已烤好的空心圈明显区分（空心圈样式叠加在空圈上会完全看不出来）
  for (const point of points) {
    const x = offset.x + point.x
    const y = offset.y + point.y
    ctx.beginPath()
    ctx.arc(x, y, 10, 0, Math.PI * 2)
    ctx.fillStyle = '#ff5950'
    ctx.fill()
    ctx.lineWidth = 3
    ctx.strokeStyle = '#ffffff'
    ctx.stroke()
  }
  const dataUrl = canvas.toDataURL('image/png')
  return dataUrl.slice(dataUrl.indexOf(',') + 1)
}

/** 逐节点生成"成品图+红点"快照并上传（幂等：同批次同节点整组替换）。失败节点跳过不中断。 */
export async function generateGraphNodeImages(options: GenerateOptions): Promise<{ uploaded: number, failed: number }> {
  let uploaded = 0
  let failed = 0
  const stationCache = new Map<EntityId, Awaited<ReturnType<typeof imageMarkApi.getStation>>>()
  const imageCache = new Map<string, { img: HTMLImageElement, offset: { x: number, y: number } }>()

  for (const sequence of options.sequences) {
    const subStationId = options.subStationBySequence.get(sequence.sourceKey)
    if (!subStationId) {
      continue
    }
    if (!stationCache.has(subStationId)) {
      try {
        stationCache.set(subStationId, await imageMarkApi.getStation(subStationId))
      }
      catch {
        continue
      }
    }
    const station = stationCache.get(subStationId)!

    for (const node of sequence.nodes) {
      try {
        const selection = selectTiaGraphElement(sequence, node.sourceKey)
        if (!selection) {
          continue
        }
        const refs = referenceKeys(symbolNamesForSelection(withAdjacentTransitionNetworks(sequence, selection)))
        if (refs.size === 0) {
          continue
        }
        const bomIds = new Set<string>()
        for (const bom of station.boms) {
          const tagCode = (bom.tagCode ?? '').trim().toLowerCase()
          if (tagCode && refs.has(tagCode)) {
            bomIds.add(String(bom.id))
          }
        }
        if (bomIds.size === 0) {
          continue
        }
        const markers = station.markers.filter(marker =>
          marker.bomIds.some(id => bomIds.has(String(id))) && marker.point?.x != null && marker.point?.y != null)
        if (markers.length === 0) {
          continue
        }

        const byImage = new Map<string, Array<{ x: number, y: number }>>()
        for (const marker of markers) {
          const list = byImage.get(String(marker.imageId)) ?? []
          list.push({ x: marker.point!.x as number, y: marker.point!.y as number })
          byImage.set(String(marker.imageId), list)
        }

        const payloads: Array<{ sourceImageId: EntityId, pixelWidth: number, pixelHeight: number, pngBase64: string }> = []
        for (const image of station.images) {
          const points = byImage.get(String(image.id))
          if (!points || !points.length) {
            continue
          }
          const cacheKey = String(image.id)
          if (!imageCache.has(cacheKey)) {
            const contentUrl = image.currentContentUrl || image.contentUrl
            if (!contentUrl) {
              continue
            }
            const blob = await imageMarkApi.getContent(contentUrl)
            const objectUrl = URL.createObjectURL(blob)
            try {
              const img = await loadImage(objectUrl)
              // 成品图导出时照片居中放置：原点 = (成品图尺寸 - 照片尺寸) / 2，精确无需扫描
              imageCache.set(cacheKey, { img, offset: { x: (img.naturalWidth - (image.pixelWidth || img.naturalWidth)) / 2, y: (img.naturalHeight - (image.pixelHeight || img.naturalHeight)) / 2 } })
            }
            finally {
              URL.revokeObjectURL(objectUrl)
            }
          }
          const cached = imageCache.get(cacheKey)!
          const base64 = renderNodeSnapshot(cached.img, cached.offset, points)
          if (!base64) {
            continue
          }
          payloads.push({
            sourceImageId: image.id,
            pixelWidth: cached.img.naturalWidth,
            pixelHeight: cached.img.naturalHeight,
            pngBase64: base64,
          })
        }

        if (payloads.length === 0) {
          continue
        }
        await graphNodeImageApi.save({
          batchId: options.batchId,
          lineId: options.lineId,
          deviceId: options.deviceId,
          blockName: options.blockName,
          sequenceSourceKey: sequence.sourceKey,
          nodeSourceKey: node.sourceKey,
          nodeKind: selection.kind,
          subStationId,
          images: payloads,
        })
        uploaded++
      }
      catch {
        failed++
      }
    }
  }

  return { uploaded, failed }
}