export interface ImagePoint {
  x: number
  y: number
}

export interface MarkerView {
  scale: number
  translateX: number
  translateY: number
}

export interface MarkerBillboardRect {
  x: number
  y: number
  width: number
  height: number
}

export interface MarkerLineGeometry {
  startX: number
  startY: number
  endX: number
  endY: number
}

export interface SerializableMarker {
  id: string | null
  markType: string | null
  isSingleMark: boolean
  bomIds: string[]
  point?: unknown
  billboard?: unknown
  line?: unknown
}

export const MIN_SCALE = 0.2
export const MAX_SCALE = 5

export function imageToScreen(point: ImagePoint, view: MarkerView): ImagePoint {
  return {
    x: point.x * view.scale + view.translateX,
    y: point.y * view.scale + view.translateY,
  }
}

export function screenToImage(point: ImagePoint, view: MarkerView): ImagePoint {
  return {
    x: (point.x - view.translateX) / view.scale,
    y: (point.y - view.translateY) / view.scale,
  }
}

export function zoomViewAt(view: MarkerView, cursor: ImagePoint, requestedScale: number): MarkerView {
  const imagePoint = screenToImage(cursor, view)
  const scale = Math.min(MAX_SCALE, Math.max(MIN_SCALE, requestedScale))
  return {
    scale,
    translateX: cursor.x - imagePoint.x * scale,
    translateY: cursor.y - imagePoint.y * scale,
  }
}

export function panView(view: MarkerView, deltaX: number, deltaY: number): MarkerView {
  return {
    ...view,
    translateX: view.translateX + deltaX,
    translateY: view.translateY + deltaY,
  }
}

/** 按绝对缩放系数生成居中视图（如 0.9 = 工具栏显示 90%）。 */
export function centeredView(
  bounds: CanvasBounds,
  imageWidth: number,
  imageHeight: number,
  scale: number,
): MarkerView | null {
  if (imageWidth <= 0 || imageHeight <= 0 || !Number.isFinite(scale) || scale <= 0) {
    return null
  }
  const clamped = Math.min(MAX_SCALE, Math.max(MIN_SCALE, scale))
  return {
    scale: clamped,
    translateX: (bounds.width - imageWidth * clamped) / 2,
    translateY: (bounds.height - imageHeight * clamped) / 2,
  }
}

export interface BillboardBox {
  key: string
  x: number
  y: number
  width: number
  height: number
}

/** 标签避让布局：水平方向有重叠且垂直间距不足 gap 的标签框向下推开。
 * 返回新数组（不改入参），按 y 升序稳定处理，最多迭代 4 轮处理级联。 */
export function separateBillboards<T extends BillboardBox>(boxes: T[], gap = 10): T[] {
  const items = boxes.map(box => ({ ...box })).sort((a, b) => a.y - b.y || a.x - b.x)
  for (let pass = 0; pass < 4; pass += 1) {
    let moved = false
    const placed: T[] = []
    for (const item of items) {
      for (const other of placed) {
        const xOverlap = item.x < other.x + other.width && other.x < item.x + item.width
        if (!xOverlap) {
          continue
        }
        const minTop = other.y + other.height + gap
        if (item.y < minTop) {
          item.y = minTop
          moved = true
        }
      }
      placed.push(item)
    }
    if (!moved) {
      break
    }
  }
  return items
}

export interface CanvasBounds {
  width: number
  height: number
}

/** 计算铺满画布的视图：图片等比缩放至可用区域（允许放大超过 100%），并居中。
 * zoom 为缩放系数（1=恰好铺满可用区，0.9=铺满尺寸的 90%）。 */
export function fitView(
  bounds: CanvasBounds,
  imageWidth: number,
  imageHeight: number,
  padding = 16,
  zoom = 1,
): MarkerView | null {
  if (imageWidth <= 0 || imageHeight <= 0) {
    return null
  }
  const availableWidth = bounds.width - padding * 2
  const availableHeight = bounds.height - padding * 2
  if (availableWidth <= 0 || availableHeight <= 0) {
    return null
  }
  const requested = Math.min(availableWidth / imageWidth, availableHeight / imageHeight) * zoom
  const scale = Math.min(MAX_SCALE, Math.max(MIN_SCALE, requested))
  return {
    scale,
    translateX: (bounds.width - imageWidth * scale) / 2,
    translateY: (bounds.height - imageHeight * scale) / 2,
  }
}

export function clampImagePoint(point: ImagePoint, imageWidth: number, imageHeight: number): ImagePoint {
  return {
    x: Math.min(Math.max(0, imageWidth), Math.max(0, point.x)),
    y: Math.min(Math.max(0, imageHeight), Math.max(0, point.y)),
  }
}

export function moveBillboard(
  billboard: MarkerBillboardRect,
  imageWidth: number,
  imageHeight: number,
): MarkerBillboardRect {
  // 标签允许挂在图片边缘外（仅钳制尺寸不超过图片尺寸）；点位仍由 clampImagePoint 限制在图内
  const width = Math.min(Math.max(0, imageWidth), Math.max(0, billboard.width))
  const height = Math.min(Math.max(0, imageHeight), Math.max(0, billboard.height))
  return {
    x: billboard.x,
    y: billboard.y,
    width,
    height,
  }
}

export function moveLineEnd(
  line: MarkerLineGeometry,
  endpoint: 'start' | 'end',
  position: ImagePoint,
  imageWidth: number,
  imageHeight: number,
): MarkerLineGeometry {
  const next = clampImagePoint(position, imageWidth, imageHeight)
  return endpoint === 'start'
    ? { ...line, startX: next.x, startY: next.y }
    : { ...line, endX: next.x, endY: next.y }
}

function compareMarker(left: SerializableMarker, right: SerializableMarker): number {
  if (left.id === null) {
    return right.id === null ? 0 : -1
  }
  if (right.id === null) {
    return 1
  }
  return left.id.localeCompare(right.id, undefined, { numeric: true })
}

export function serializeMarkers(markers: SerializableMarker[]): string {
  const normalized = markers.map(marker => ({
    id: marker.id,
    markType: marker.markType,
    isSingleMark: marker.isSingleMark,
    ...(marker.point === undefined ? {} : { point: marker.point }),
    ...(marker.billboard === undefined ? {} : { billboard: marker.billboard }),
    ...(marker.line === undefined ? {} : { line: marker.line }),
    bomIds: [...new Set(marker.bomIds)].sort((left, right) =>
      left.localeCompare(right, undefined, { numeric: true })),
  })).sort(compareMarker)
  return JSON.stringify(normalized)
}
