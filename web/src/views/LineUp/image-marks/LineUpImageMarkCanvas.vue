<script setup lang="ts">
import type { ImagePoint, MarkerView } from './markerGeometry'
import type { LineUpImageMarkerWrite } from '@/api/modules/LineUp/imageMarks'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import {
  MAX_SCALE,
  MIN_SCALE,
  centeredView,
  clampImagePoint,
  fitView,
  separateBillboards,
  moveBillboard,
  moveLineEnd,
  panView,
  screenToImage,
  zoomViewAt,
} from './markerGeometry'
import { useI18n } from 'vue-i18n'
import messages from './components/lang'

export interface EditableImageMarker extends LineUpImageMarkerWrite {
  clientKey: string
}

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}

const props = defineProps<{
  imageUrl: string
  imageWidth: number
  imageHeight: number
  markers: EditableImageMarker[]
  bomLabels: Record<string, string>
  selectedMarkerKey: string | null
  /** 关联型 BOM（IsAssociated=TRUE）的 id 集合：标记挂到其中任一 BOM 时标签边框用粉红 */
  associatedBomIds?: Array<string | number>
  /** default=编辑态（90% 居中留标签位）；fill=成品查看态（铺满画布） */
  fitMode?: 'fill' | 'default'
}>()

const emit = defineEmits<{
  select: [clientKey: string]
  update: [marker: EditableImageMarker]
}>()

const svg = ref<SVGSVGElement | null>(null)
const view = ref<MarkerView>({ scale: 1, translateX: 24, translateY: 24 })
const gesture = ref<
  | { kind: 'pan', last: ImagePoint }
  | { kind: 'point' | 'billboard' | 'line-start' | 'line-end', markerKey: string }
  | null
>(null)

const transform = computed(() =>
  `translate(${view.value.translateX} ${view.value.translateY}) scale(${view.value.scale})`)

interface ConnectorLine {
  x1: number
  y1: number
  x2: number
  y2: number
}

/** 连线在渲染时按几何实时推导：标签左边缘中点 → 点位（老系统语义）。
 * 老库存储的连线坐标 99.9% 是复制粘贴残留（不指向自身点位），一律不信；
 * 拖拽标签或点位时连线自动跟随。仅当缺少标签或点位时才回退画存储连线。 */
function connector(marker: EditableImageMarker): ConnectorLine | null {
  if (marker.billboard && marker.point) {
    const box = layoutBoxOf(marker)
    return {
      x1: box?.x ?? marker.billboard.x ?? 0,
      y1: (box?.y ?? marker.billboard.y ?? 0) + (box?.height ?? marker.billboard.height ?? 20) / 2,
      x2: marker.point.x ?? 0,
      y2: marker.point.y ?? 0,
    }
  }
  const line = marker.line
  if (line) {
    return { x1: line.startX ?? 0, y1: line.startY ?? 0, x2: line.endX ?? 0, y2: line.endY ?? 0 }
  }
  return null
}

function estimateTextWidth(text: string, fontSize: number): number {
  let width = 0
  for (const ch of text) {
    width += ch.charCodeAt(0) > 255 ? fontSize : fontSize * 0.62
  }
  return width
}

/** 标签框宽度自适应：不小于存储宽度，也不小于标题/描述文本估宽 + 内边距。 */
function billboardBoxWidth(
  billboard: { width?: number | null },
  title: string,
  description?: string | null,
): number {
  const titleWidth = estimateTextWidth(title || '', 14)
  const descriptionWidth = description ? estimateTextWidth(description, 12) : 0
  return Math.max(billboard.width ?? 0, Math.ceil(Math.max(titleWidth, descriptionWidth)) + 16)
}

interface BillboardBoxView {
  x: number
  y: number
  width: number
  height: number
}

const BILLBOARD_GAP = 10

/** 标签显示布局：重叠标签垂直避让（不改存储数据），连线/文本跟随显示位置。
 * 图片上方（图外）的标签镜像坐标后避让——只会被向上推开，绝不压进图片；图内/图下标签向下避让。 */
const billboardLayout = computed(() => {
  const inputs = props.markers
    .filter(marker => marker.billboard)
    .map((marker) => {
      const billboard = marker.billboard!
      return {
        key: marker.clientKey,
        x: billboard.x ?? 0,
        y: billboard.y ?? 0,
        width: billboardBoxWidth(billboard, billboard.text || bomLabelOf(marker), billboard.description),
        height: billboard.height ?? 20,
      }
    })
  const map = new Map<string, BillboardBoxView>()
  const above = inputs.filter(box => box.y + box.height <= 0).map(box => ({ ...box, y: -box.y - box.height }))
  const below = inputs.filter(box => box.y + box.height > 0)
  for (const item of separateBillboards(above, BILLBOARD_GAP)) {
    map.set(item.key, { ...item, y: -item.y - item.height })
  }
  for (const item of separateBillboards(below, BILLBOARD_GAP)) {
    map.set(item.key, item)
  }
  return map
})

function layoutBoxOf(marker: EditableImageMarker): BillboardBoxView | null {
  return billboardLayout.value.get(marker.clientKey) ?? null
}

// 关联点位清单条目（BomIds 非空）的点位标签边框专用色：粉红；其余元素一律默认色
const BOM_LINKED_BORDER = '#ff6b9d'
const BOM_LINKED_FILL = '#ff5722'

function isBomLinkedMarker(marker: EditableImageMarker): boolean {
  const ids = props.associatedBomIds
  if (!ids || ids.length === 0) return false
  return (marker.bomIds ?? []).some(id => ids.includes(String(id)) || ids.includes(Number(id)))
}

function bomLabelOf(marker: EditableImageMarker): string {
  const labels = marker.bomIds.map(id => props.bomLabels[id]).filter(Boolean)
  return labels.length > 0 ? labels.join(' / ') : t('lineupImageMarks_form.bomMarkFallback')
}

function billboardTitleDy(billboard: { height?: number | null }): number {
  // 老系统标签是单行紧凑小框（约 120x20），按高度垂直居中；空描述不渲染第二行
  return Math.max(14, (billboard.height ?? 20) / 2 + 5)
}

function localPoint(event: PointerEvent): ImagePoint {
  const bounds = svg.value?.getBoundingClientRect()
  return { x: event.clientX - (bounds?.left ?? 0), y: event.clientY - (bounds?.top ?? 0) }
}

function startPan(event: PointerEvent) {
  if (event.button !== 0 && event.button !== 1) {
    return
  }
  svg.value?.setPointerCapture(event.pointerId)
  gesture.value = { kind: 'pan', last: localPoint(event) }
}

function startMarkerDrag(
  event: PointerEvent,
  markerKey: string,
  kind: 'point' | 'billboard' | 'line-start' | 'line-end',
) {
  event.stopPropagation()
  svg.value?.setPointerCapture(event.pointerId)
  gesture.value = { kind, markerKey }
  emit('select', markerKey)
}

function handlePointerMove(event: PointerEvent) {
  const activeGesture = gesture.value
  if (!activeGesture) {
    return
  }
  const local = localPoint(event)
  if (activeGesture.kind === 'pan') {
    view.value = panView(
      view.value,
      local.x - activeGesture.last.x,
      local.y - activeGesture.last.y,
    )
    gesture.value = { kind: 'pan', last: local }
    return
  }

  const marker = props.markers.find(item => item.clientKey === activeGesture.markerKey)
  if (!marker) {
    return
  }
  const point = clampImagePoint(
    screenToImage(local, view.value),
    props.imageWidth,
    props.imageHeight,
  )
  // 不能用 structuredClone：props.markers 里的条目是 Vue 响应式代理，structuredClone 对代理抛
  // DataCloneError，导致拖拽第一次 pointermove 就中断（点位拖不动、保存仍是旧坐标）。
  // 标记数据全是 JSON 安全类型，JSON 往返即可深克隆。
  const next = JSON.parse(JSON.stringify(marker)) as EditableImageMarker
  if (activeGesture.kind === 'point' && next.point) {
    next.point.x = point.x
    next.point.y = point.y
  }
  else if (activeGesture.kind === 'billboard' && next.billboard) {
    const rect = moveBillboard({
      x: point.x,
      y: point.y,
      width: next.billboard.width ?? 180,
      height: next.billboard.height ?? 64,
    }, props.imageWidth, props.imageHeight)
    next.billboard.x = rect.x
    next.billboard.y = rect.y
    next.billboard.width = rect.width
    next.billboard.height = rect.height
  }
  else if ((activeGesture.kind === 'line-start' || activeGesture.kind === 'line-end') && next.line) {
    const line = moveLineEnd({
      startX: next.line.startX ?? 0,
      startY: next.line.startY ?? 0,
      endX: next.line.endX ?? 0,
      endY: next.line.endY ?? 0,
    }, activeGesture.kind === 'line-start' ? 'start' : 'end', point, props.imageWidth, props.imageHeight)
    Object.assign(next.line, line)
  }
  emit('update', next)
}

function stopGesture(event: PointerEvent) {
  if (svg.value?.hasPointerCapture(event.pointerId)) {
    svg.value.releasePointerCapture(event.pointerId)
  }
  gesture.value = null
}

function handleWheel(event: WheelEvent) {
  const requested = view.value.scale * (event.deltaY > 0 ? 0.88 : 1.12)
  view.value = zoomViewAt(view.value, localPoint(event as unknown as PointerEvent), requested)
}

/** 默认视图：编辑态=绝对 90% 缩放居中（留标签位）；成品查看态=铺满画布。 */
const DEFAULT_VIEW_SCALE = 0.9

function applyDefaultView() {
  const bounds = svg.value?.getBoundingClientRect()
  if (!bounds) {
    return
  }
  const box = { width: bounds.width, height: bounds.height }
  const next = props.fitMode === 'fill'
    ? fitView(box, props.imageWidth, props.imageHeight)
    : centeredView(box, props.imageWidth, props.imageHeight, DEFAULT_VIEW_SCALE)
  if (next) {
    view.value = next
  }
}

/** 适应画布：内容整体可见（图片 ∪ 全部标签显示框——被避让挤出视口的图外标签也纳入）。 */
function fit() {
  const bounds = svg.value?.getBoundingClientRect()
  if (!bounds) {
    return
  }
  let minX = 0
  let minY = 0
  let maxX = props.imageWidth
  let maxY = props.imageHeight
  for (const box of billboardLayout.value.values()) {
    minX = Math.min(minX, box.x)
    minY = Math.min(minY, box.y)
    maxX = Math.max(maxX, box.x + box.width)
    maxY = Math.max(maxY, box.y + box.height)
  }
  const padding = 16
  const width = Math.max(1, maxX - minX)
  const height = Math.max(1, maxY - minY)
  const scale = Math.min(
    MAX_SCALE,
    Math.max(
      MIN_SCALE,
      Math.min((bounds.width - padding * 2) / width, (bounds.height - padding * 2) / height),
    ),
  )
  view.value = {
    scale,
    translateX: (bounds.width - width * scale) / 2 - minX * scale,
    translateY: (bounds.height - height * scale) / 2 - minY * scale,
  }
}

let resizeObserver: ResizeObserver | null = null
onMounted(() => {
  resizeObserver = new ResizeObserver(() => applyDefaultView())
  if (svg.value) {
    resizeObserver.observe(svg.value)
  }
  applyDefaultView()
})
onBeforeUnmount(() => resizeObserver?.disconnect())
// 切换图片（含尺寸变化）或查看/编辑模式切换时应用对应默认视图
watch(
  () => [props.imageUrl, props.imageWidth, props.imageHeight, props.fitMode] as const,
  () => void nextTick(applyDefaultView),
)

function loadImage(src: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const img = new Image()
    img.onload = () => resolve(img)
    img.onerror = reject
    img.src = src
  })
}

/** 导出成品图：与编辑器实时渲染完全同款——原始照片 + 连线(#ffd34d) + 标签框(维护的 billboard.text，
 * 不用 BOM 编码生成标签) + 点位空圈(#ff5950)。生成预览再在其上动态强调当前工步匹配的点位。 */
async function exportRenderedImage(): Promise<File | null> {
  try {
    const image = await loadImage(props.imageUrl)
    // 标签显示布局（与 billboardLayout computed 相同：重叠垂直避让 + 图上标签镜像）
    const inputs = props.markers
      .filter(marker => marker.billboard)
      .map((marker) => {
        const billboard = marker.billboard!
        return {
          key: marker.clientKey,
          x: billboard.x ?? 0,
          y: billboard.y ?? 0,
          width: billboardBoxWidth(billboard, billboard.text || '', billboard.description),
          height: billboard.height ?? 20,
        }
      })
    const boxes = new Map<string, BillboardBoxView>()
    const above = inputs.filter(box => box.y + box.height <= 0).map(box => ({ ...box, y: -box.y - box.height }))
    const below = inputs.filter(box => box.y + box.height > 0)
    for (const item of separateBillboards(above, BILLBOARD_GAP)) {
      boxes.set(item.key, { x: item.x, y: -item.y - item.height, width: item.width, height: item.height })
    }
    for (const item of separateBillboards(below, BILLBOARD_GAP)) {
      boxes.set(item.key, item)
    }

    const pad = 12
    let minX = 0
    let minY = 0
    let maxX = props.imageWidth
    let maxY = props.imageHeight
    for (const box of boxes.values()) {
      minX = Math.min(minX, box.x)
      minY = Math.min(minY, box.y)
      maxX = Math.max(maxX, box.x + box.width)
      maxY = Math.max(maxY, box.y + box.height)
    }
    for (const marker of props.markers) {
      if (marker.point) {
        minX = Math.min(minX, (marker.point.x ?? 0) - 14)
        minY = Math.min(minY, (marker.point.y ?? 0) - 14)
        maxX = Math.max(maxX, (marker.point.x ?? 0) + 14)
        maxY = Math.max(maxY, (marker.point.y ?? 0) + 14)
      }
      if (marker.line) {
        minX = Math.min(minX, marker.line.startX ?? 0, marker.line.endX ?? 0)
        minY = Math.min(minY, marker.line.startY ?? 0, marker.line.endY ?? 0)
        maxX = Math.max(maxX, marker.line.startX ?? 0, marker.line.endX ?? 0)
        maxY = Math.max(maxY, marker.line.startY ?? 0, marker.line.endY ?? 0)
      }
    }
    // 照片在成品图内居中放置：消费侧（生成预览/快照生成器）可直接用
    // ((成品图宽 - 照片宽) / 2, (成品图高 - 照片高) / 2) 精确算出照片原点，无需像素扫描。
    // 画布尺寸需同时满足：容纳全部内容+边距、容纳照片+边距、且居中偏移后内容两侧都留边距
    //（否则图外标签外凸过多时会被推出画布裁掉）
    const canvasW = Math.max(
      Math.ceil(maxX - minX) + pad * 2,
      props.imageWidth + pad * 2,
      props.imageWidth + pad * 2 - 2 * minX,
      pad * 2 + 2 * maxX - props.imageWidth,
    )
    const canvasH = Math.max(
      Math.ceil(maxY - minY) + pad * 2,
      props.imageHeight + pad * 2,
      props.imageHeight + pad * 2 - 2 * minY,
      pad * 2 + 2 * maxY - props.imageHeight,
    )
    const canvas = document.createElement('canvas')
    canvas.width = canvasW
    canvas.height = canvasH
    const ctx = canvas.getContext('2d')
    if (!ctx) {
      return null
    }
    // 照片外边距带用浅色（贴合编辑器画布观感，深色太突兀）
    ctx.fillStyle = '#f5f7fa'
    ctx.fillRect(0, 0, canvas.width, canvas.height)
    const ox = Math.round((canvasW - props.imageWidth) / 2)
    const oy = Math.round((canvasH - props.imageHeight) / 2)
    ctx.drawImage(image, ox, oy)
    // 与模板同序：先连线，再标签框，最后红点
    for (const marker of props.markers) {
      const box = boxes.get(marker.clientKey)
      if (marker.billboard && marker.point && box) {
        ctx.beginPath()
        ctx.moveTo(box.x + ox, (box.y + box.height / 2) + oy)
        ctx.lineTo((marker.point.x ?? 0) + ox, (marker.point.y ?? 0) + oy)
        ctx.strokeStyle = '#ffd34d'
        ctx.lineWidth = 3
        ctx.stroke()
      }
      else if (marker.line) {
        ctx.beginPath()
        ctx.moveTo((marker.line.startX ?? 0) + ox, (marker.line.startY ?? 0) + oy)
        ctx.lineTo((marker.line.endX ?? 0) + ox, (marker.line.endY ?? 0) + oy)
        ctx.strokeStyle = '#ffd34d'
        ctx.lineWidth = 3
        ctx.stroke()
      }
    }
    for (const marker of props.markers) {
      const box = boxes.get(marker.clientKey)
      if (marker.billboard && box) {
        ctx.beginPath()
        if (typeof ctx.roundRect === 'function') {
          ctx.roundRect(box.x + ox, box.y + oy, box.width, box.height, 5)
        }
        else {
          ctx.rect(box.x + ox, box.y + oy, box.width, box.height)
        }
        ctx.fillStyle = marker.billboard.backgroundColor || '#0c2539'
        ctx.fill()
        ctx.strokeStyle = marker.billboard.borderColor || '#24d4c3'
        ctx.lineWidth = 2
        ctx.stroke()
        const title = marker.billboard.text || ''
        const dy = billboardTitleDy(marker.billboard)
        if (title) {
          ctx.font = '14px sans-serif'
          ctx.fillStyle = '#e7f7ff'
          ctx.textBaseline = 'alphabetic'
          ctx.fillText(title, (box.x + 8) + ox, (box.y + dy) + oy)
        }
        if (marker.billboard.description) {
          ctx.font = '12px sans-serif'
          ctx.fillStyle = '#9ec2d8'
          ctx.fillText(marker.billboard.description, (box.x + 8) + ox, (box.y + dy + 16) + oy)
        }
      }
    }
    // 点位空圈（编辑器 point-handle 同款：外圈描边 + 中心白点），画在最后与编辑层级一致
    for (const marker of props.markers) {
      if (!marker.point) {
        continue
      }
      const x = (marker.point.x ?? 0) + ox
      const y = (marker.point.y ?? 0) + oy
      ctx.beginPath()
      ctx.arc(x, y, 12, 0, Math.PI * 2)
      ctx.fillStyle = 'rgba(255, 89, 80, 0.2)'
      ctx.fill()
      ctx.strokeStyle = '#ff5950'
      ctx.lineWidth = 3
      ctx.stroke()
      ctx.beginPath()
      ctx.arc(x, y, 4, 0, Math.PI * 2)
      ctx.fillStyle = '#fff'
      ctx.fill()
    }
    const blob = await new Promise<Blob | null>(resolve => canvas.toBlob(resolve, 'image/png'))
    if (!blob) {
      return null
    }
    return new File([blob], 'rendered.png', { type: 'image/png' })
  }
  catch (error) {
    console.error('[exportRenderedImage] 合成成品图失败:', error)
    return null
  }
}

defineExpose({ fit, exportRenderedImage })
</script>

<template>
  <div class="marker-canvas-shell">
    <div class="canvas-tools">
      <el-button size="small" @click="fit">
        {{ $t('lineupImageMarks_form.fitCanvas') }}
      </el-button>
      <span>{{ Math.round(view.scale * 100) }}%</span>
      <span>{{ $t('lineupImageMarks_form.canvasToolsTip') }}</span>
    </div>
    <svg
      ref="svg"
      class="marker-canvas"
      @wheel.prevent="handleWheel"
      @pointerdown="startPan"
      @pointermove="handlePointerMove"
      @pointerup="stopGesture"
      @pointercancel="stopGesture"
    >
      <defs>
        <pattern id="lineup-grid" width="20" height="20" patternUnits="userSpaceOnUse">
          <path class="canvas-grid-line" d="M 20 0 L 0 0 0 20" fill="none" stroke-width="1" />
        </pattern>
      </defs>
      <rect width="100%" height="100%" fill="url(#lineup-grid)" />
      <g :transform="transform">
        <image :href="imageUrl" x="0" y="0" :width="imageWidth" :height="imageHeight" />
        <g
          v-for="marker in markers"
          :key="marker.clientKey"
          class="marker-group"
          :class="{ selected: marker.clientKey === selectedMarkerKey }"
          @click.stop="emit('select', marker.clientKey)"
        >
          <line
            v-if="connector(marker)"
            :x1="connector(marker)?.x1 ?? 0"
            :y1="connector(marker)?.y1 ?? 0"
            :x2="connector(marker)?.x2 ?? 0"
            :y2="connector(marker)?.y2 ?? 0"
            class="marker-line"
          />
          <template v-if="marker.billboard">
            <rect
              :x="layoutBoxOf(marker)?.x ?? 0"
              :y="layoutBoxOf(marker)?.y ?? 0"
              :width="layoutBoxOf(marker)?.width ?? 120"
              :height="layoutBoxOf(marker)?.height ?? 20"
              rx="5"
              class="marker-billboard"
              :style="{
                fill: isBomLinkedMarker(marker)
                  ? BOM_LINKED_FILL
                  : (marker.billboard.backgroundColor || '#0c2539'),
                stroke: isBomLinkedMarker(marker)
                  ? BOM_LINKED_BORDER
                  : (marker.billboard.borderColor || '#24d4c3'),
              }"
              @pointerdown="startMarkerDrag($event, marker.clientKey, 'billboard')"
            />
            <text
              :x="(layoutBoxOf(marker)?.x ?? 0) + 8"
              :y="(layoutBoxOf(marker)?.y ?? 0) + billboardTitleDy(marker.billboard)"
              class="billboard-title"
            >
              {{ marker.billboard.text || bomLabelOf(marker) }}
            </text>
            <text
              v-if="marker.billboard.description"
              :x="(layoutBoxOf(marker)?.x ?? 0) + 8"
              :y="(layoutBoxOf(marker)?.y ?? 0) + billboardTitleDy(marker.billboard) + 16"
              class="billboard-description"
            >
              {{ marker.billboard.description }}
            </text>
          </template>
          <g
            v-if="marker.point"
            class="point-handle"
            @pointerdown="startMarkerDrag($event, marker.clientKey, 'point')"
          >
            <circle :cx="marker.point.x ?? 0" :cy="marker.point.y ?? 0" r="12" />
            <circle :cx="marker.point.x ?? 0" :cy="marker.point.y ?? 0" r="4" />
          </g>
        </g>
      </g>
    </svg>
  </div>
</template>

<style scoped lang="scss">
.marker-canvas-shell {
  display: grid;
  grid-template-rows: 42px minmax(0, 1fr);
  min-height: 520px;
  overflow: hidden;
  background: var(--el-fill-color-light);
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 10px;
}

.canvas-tools {
  display: flex;
  gap: 14px;
  align-items: center;
  padding: 0 12px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
  background: var(--el-bg-color);
  border-bottom: 1px solid var(--el-border-color-lighter);
}

.canvas-grid-line {
  stroke: var(--el-border-color-lighter);
}

.marker-canvas {
  width: 100%;
  height: 100%;
  min-height: 478px;
  touch-action: none;
  cursor: grab;
  user-select: none;
}

.marker-line {
  stroke: #ffd34d;
  stroke-width: 3;
  vector-effect: non-scaling-stroke;
}

.line-handle {
  cursor: crosshair;
  fill: #07131f;
  stroke: #ffd34d;
  stroke-width: 3;
  vector-effect: non-scaling-stroke;
}

.marker-billboard {
  cursor: move;
  stroke-width: 2;
  vector-effect: non-scaling-stroke;
}

.billboard-title,
.billboard-description {
  font-size: 14px;
  pointer-events: none;
  fill: #e7f7ff;
}

.billboard-description {
  font-size: 12px;
  fill: #9ec2d8;
}

.point-handle {
  cursor: move;

  circle:first-child {
    fill: rgb(255 89 80 / 20%);
    stroke: #ff5950;
    stroke-width: 3;
    vector-effect: non-scaling-stroke;
  }

  circle:last-child {
    fill: #fff;
  }
}

.marker-group.selected {
  .marker-line,
  .line-handle,
  .marker-billboard,
  .point-handle circle:first-child {
    filter: drop-shadow(0 0 5px #41f3ff);
  }
}
</style>
