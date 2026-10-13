<script setup lang="ts">
import type { Node } from '@antv/x6'
import type { LineUpGraphRuntimeProjection, LineUpRuntimeTone } from '../monitor/monitorProjection'
import type { LineUpGraphPreviewSequenceInput, TiaGraphViewState, TiaLayoutNode, TiaParallelRail } from './tiaGraphLayout'
import type { TiaGraphSelection } from './tiaGraphSelection'
import type { TiaOverlayPlacement } from './tiaOverlayPlacement'
import type { LineUpConditionRuntimeValue } from '@/api/modules/LineUp/monitor'
import type { PlcGraphStepTagRow } from '@/api/modules/PLC/graphStepTag'
import { getStepTagRowsByStep } from '@/api/modules/PLC/graphStepTag'
import { Graph } from '@antv/x6'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, useSlots, watch } from 'vue'
import { buildTiaGraphLayout, buildTiaParallelRails } from './tiaGraphLayout'
import { selectTiaGraphElement } from './tiaGraphSelection'
import { nextTiaGraphZoom, resolveTiaGraphToolbarState, resolveTiaGraphWheelPan } from './tiaGraphToolbar'
import { fitTiaGraphViewport, resolveTiaGraphCanvasSize } from './tiaGraphViewport'
import TiaLogicNetworkPanel from './TiaLogicNetworkPanel.vue'
import { dragTiaOverlay, placeTiaOverlay } from './tiaOverlayPlacement'
import { useI18n } from 'vue-i18n'
import messages from './lang'

const props = withDefaults(defineProps<{
  sequence: LineUpGraphPreviewSequenceInput | null
  viewState?: TiaGraphViewState | null
  runtime?: LineUpGraphRuntimeProjection | null
  runtimeValues?: LineUpConditionRuntimeValue[]
  reservedRight?: number
  /** 传入后点击 Step 时按项目查 PLC 台账（ai_plc_graphsteptag）展示工步变量 */
  stepTagProjectId?: number | string
  /** 工步变量按 GRAPH FB 类型过滤（只显示当前 graph 对应实例 DB） */
  stepTagFbName?: string
  /** BOM 图片缩略图（静态预览上下文）：子工站 id，透传给工步/转换弹框面板 */
  bomSubStationId?: string
  /** 选中节点关联的 PLC 变量编码，透传给弹框面板的 BOM 图片页签 */
  bomReferences?: string[]
  /** 查看流程图：按节点读已保存的"成品图+红点"快照（透传 TiaLogicNetworkPanel） */
  savedNodeImages?: { lineId: string | number, blockName: string, nodeSourceKey: string } | null
}>(), {
  runtimeValues: () => [],
  reservedRight: 0,
  stepTagProjectId: '',
  stepTagFbName: '',
  bomSubStationId: '',
  bomReferences: () => [],
  savedNodeImages: null,
})

const emit = defineEmits<{
  nodeClick: [sourceKey: string]
  selectionChange: [selection: TiaGraphSelection | null]
  viewStateChange: [state: TiaGraphViewState]
}>()
const slots = useSlots()

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}

const emptyTipText = computed(() => t('lineupShared.emptyCanvasTip'))

const canvasHost = ref<HTMLDivElement | null>(null)
const container = ref<HTMLDivElement | null>(null)
const selectedElement = ref<TiaGraphSelection | null>(null)
const stepTags = ref<PlcGraphStepTagRow[]>([])
const stepTagsLoading = ref(false)
let stepTagsToken = 0
const overlayPlacement = ref<TiaOverlayPlacement | null>(null)
const overlayDragging = ref(false)
const parallelRails = ref<TiaParallelRail[]>([])
const viewportTransform = ref({ zoom: 1, translateX: 0, translateY: 0 })
let graph: Graph | null = null
let resizeObserver: ResizeObserver | null = null
let fitFrame = 0
let overlayFrame = 0
let locatePulseTimer = 0
let selectedNodeKey = ''
let restoringView = false
let overlayWasDragged = false
let overlayDragState: {
  pointerId: number
  startX: number
  startY: number
  origin: TiaOverlayPlacement
} | null = null

const overlayStyle = computed(() => {
  const placement = overlayPlacement.value
  if (!placement) {
    return undefined
  }
  return {
    left: `${placement.left}px`,
    top: `${placement.top}px`,
    width: `${placement.maxWidth}px`,
    height: `${placement.maxHeight}px`,
    maxWidth: `${placement.maxWidth}px`,
    maxHeight: `${placement.maxHeight}px`,
  }
})

const parallelRailTransform = computed(() => {
  const value = viewportTransform.value
  return `matrix(${value.zoom} 0 0 ${value.zoom} ${value.translateX} ${value.translateY})`
})
const hasRuntimeDetail = computed(() => Boolean(slots['runtime-detail']))
const toolbarState = computed(() => resolveTiaGraphToolbarState(props.sequence, props.runtime ?? null))

interface CanvasNodeData {
  sourceKey: string
  nodeKind: 'Step' | 'Transition'
  baseColor: string
  baseStrokeWidth: number
  logicalVisualRightExtent: number
}

interface CanvasEdgeData {
  sourceKey: string
  baseColor: string
  baseStrokeWidth: number
  dashed: boolean
}

const runtimePalette: Record<LineUpRuntimeTone, { fill: string, stroke: string, header?: string, number?: string, label?: string }> = {
  idle: { fill: '#e5e8eb', stroke: '#66717d' },
  /** 活动步 = 深蓝底 + 白字，标识 PLC 当前停留工步 */
  active: { fill: '#1e3a8a', stroke: '#3b82f6', header: '#1e40af', number: '#bfdbfe', label: '#f0f6ff' },
  /** 步超时 = 活动步深蓝的淡化版（2026-09-11 B：超时非异常/告警，仅比健康色稍淡；TIA 模板默认 T#10S 普遍非现场组态） */
  timeout: { fill: '#4a69ad', stroke: '#7191d6', header: '#5472b4', number: '#cddaf2', label: '#eef3fb' },
  waiting: { fill: '#d6e4ff', stroke: '#2f54eb' },
  completed: { fill: '#d9f7be', stroke: '#52c41a' },
  warning: { fill: '#fff1b8', stroke: '#d48806' },
  error: { fill: '#ffccc7', stroke: '#cf1322' },
  unknown: { fill: '#f0f2f5', stroke: '#8c8c8c' },
}

function applySelectedNodeStyle() {
  for (const node of graph?.getNodes() ?? []) {
    const data = node.getData<CanvasNodeData>()
    const selected = data.sourceKey === selectedNodeKey
    const runtime = props.runtime?.nodes[data.sourceKey]
    const palette = runtime ? runtimePalette[runtime.tone] : null
    if (data.nodeKind === 'Step') {
      node.attr('body/fill', palette?.fill ?? (data.baseColor === '#cf1322' ? '#fff2f0' : '#e5e8eb'))
      node.attr('body/stroke', palette?.stroke ?? data.baseColor)
      node.attr('body/strokeWidth', data.baseStrokeWidth)
      node.attr('body/strokeDasharray', runtime?.tone === 'unknown' ? '5 3' : '')
      node.attr('header/fill', palette?.header ?? '#f1f3f4')
      node.attr('number/fill', palette?.number ?? '#007c77')
      node.attr('label/fill', palette?.label ?? '#141b24')
    }
    else {
      node.attr('bar/fill', palette?.stroke ?? data.baseColor)
    }
    node.attr('selectionOutline/stroke', selected ? '#f79009' : 'transparent')
  }
  for (const edge of graph?.getEdges() ?? []) {
    const data = edge.getData<CanvasEdgeData>()
    const runtime = props.runtime?.edges[data.sourceKey]
    edge.attr('line/stroke', runtime?.traversed ? '#389e0d' : data.baseColor)
    edge.attr('line/strokeWidth', runtime?.traversed ? 3.2 : data.baseStrokeWidth)
    edge.attr('line/strokeDasharray', data.dashed ? '7 5' : '')
  }
}

function syncViewportTransform() {
  if (!graph) {
    return
  }
  const translation = graph.translate()
  viewportTransform.value = {
    zoom: graph.zoom(),
    translateX: translation.tx,
    translateY: translation.ty,
  }
}

function captureViewState() {
  if (!graph || restoringView) {
    return
  }
  const translation = graph.translate()
  emit('viewStateChange', {
    zoom: graph.zoom(),
    translateX: translation.tx,
    translateY: translation.ty,
    selectedNodeKey,
  })
}

function canvasSize() {
  const ownBounds = container.value?.getBoundingClientRect()
  const parentBounds = container.value?.parentElement?.getBoundingClientRect()
  return resolveTiaGraphCanvasSize(ownBounds, parentBounds)
}

function resizeGraphToCanvas() {
  if (!graph) {
    return
  }
  const size = canvasSize()
  graph.resize(size.width, size.height)
}

function selectElement(sourceKey: string) {
  if (!props.sequence) {
    return null
  }
  return selectTiaGraphElement(
    { ...props.sequence, actions: props.sequence.actions ?? [] },
    sourceKey,
  )
}

function positionSelectedOverlay() {
  if (!graph || !canvasHost.value || !selectedElement.value) {
    overlayPlacement.value = null
    return
  }
  const hostBounds = canvasHost.value.getBoundingClientRect()
  if (overlayWasDragged && overlayPlacement.value) {
    overlayPlacement.value = dragTiaOverlay({
      placement: overlayPlacement.value,
      deltaX: 0,
      deltaY: 0,
      canvas: { x: 0, y: 0, width: hostBounds.width, height: hostBounds.height },
      reservedRight: props.reservedRight,
    })
    return
  }
  const selectedSourceKey = selectedElement.value.node.sourceKey
  const node = graph.getNodes().find((item) => {
    const data = item.getData<CanvasNodeData>()
    return data.sourceKey === selectedSourceKey
  })
  if (!node) {
    overlayPlacement.value = null
    return
  }
  const nodeData = node.getData<CanvasNodeData>()
  const graphZoom = graph.zoom()
  const visualRightExtension = Math.max(0, nodeData.logicalVisualRightExtent - node.getBBox().width) * graphZoom
  const nodeClientBounds = graph.localToClient(node.getBBox())
  overlayPlacement.value = placeTiaOverlay({
    canvas: { x: 0, y: 0, width: hostBounds.width, height: hostBounds.height },
    node: {
      x: nodeClientBounds.x - hostBounds.left,
      y: nodeClientBounds.y - hostBounds.top,
      width: nodeClientBounds.width + visualRightExtension,
      height: nodeClientBounds.height,
    },
    reservedRight: props.reservedRight,
    alignRight: hasRuntimeDetail.value,
  })
}

function stopOverlayDrag() {
  window.removeEventListener('pointermove', handleOverlayDragMove)
  window.removeEventListener('pointerup', handleOverlayDragEnd)
  window.removeEventListener('pointercancel', handleOverlayDragEnd)
  overlayDragState = null
  overlayDragging.value = false
}

function handleOverlayDragMove(event: PointerEvent) {
  if (!overlayDragState || event.pointerId !== overlayDragState.pointerId || !canvasHost.value) {
    return
  }
  const hostBounds = canvasHost.value.getBoundingClientRect()
  overlayPlacement.value = dragTiaOverlay({
    placement: overlayDragState.origin,
    deltaX: event.clientX - overlayDragState.startX,
    deltaY: event.clientY - overlayDragState.startY,
    canvas: { x: 0, y: 0, width: hostBounds.width, height: hostBounds.height },
    reservedRight: props.reservedRight,
  })
  overlayWasDragged = true
}

function handleOverlayDragEnd(event: PointerEvent) {
  if (!overlayDragState || event.pointerId !== overlayDragState.pointerId) {
    return
  }
  stopOverlayDrag()
}

function startOverlayDrag(event: PointerEvent) {
  const target = event.target
  if (
    event.button !== 0
    || !(target instanceof Element)
    || !target.closest('.panel-title')
    || !overlayPlacement.value
  ) {
    return
  }
  event.preventDefault()
  stopOverlayDrag()
  overlayDragState = {
    pointerId: event.pointerId,
    startX: event.clientX,
    startY: event.clientY,
    origin: { ...overlayPlacement.value },
  }
  overlayDragging.value = true
  window.addEventListener('pointermove', handleOverlayDragMove)
  window.addEventListener('pointerup', handleOverlayDragEnd)
  window.addEventListener('pointercancel', handleOverlayDragEnd)
}

function scheduleOverlayPosition() {
  window.cancelAnimationFrame(overlayFrame)
  overlayFrame = window.requestAnimationFrame(() => {
    overlayFrame = 0
    positionSelectedOverlay()
  })
}

function clearSelection(emitChange = false) {
  stopOverlayDrag()
  overlayWasDragged = false
  selectedNodeKey = ''
  selectedElement.value = null
  overlayPlacement.value = null
  window.cancelAnimationFrame(overlayFrame)
  overlayFrame = 0
  applySelectedNodeStyle()
  if (emitChange) {
    emit('selectionChange', null)
    captureViewState()
  }
}

function restoreSelectionFromViewState(restoreSelectedNode: boolean) {
  const requestedSourceKey = restoreSelectedNode ? props.viewState?.selectedNodeKey ?? '' : ''
  const restoredSelection = requestedSourceKey ? selectElement(requestedSourceKey) : null
  if (!restoredSelection) {
    clearSelection(false)
    if (requestedSourceKey) {
      emit('selectionChange', null)
    }
    return
  }
  selectedNodeKey = requestedSourceKey
  selectedElement.value = restoredSelection
  overlayPlacement.value = null
  emit('selectionChange', restoredSelection)
}

function handleNodeClick({ node }: { node: Node }) {
  stopOverlayDrag()
  overlayWasDragged = false
  selectedNodeKey = String(node.getData<CanvasNodeData>()?.sourceKey ?? node.id)
  selectedElement.value = selectElement(selectedNodeKey)
  overlayPlacement.value = null
  applySelectedNodeStyle()
  emit('nodeClick', selectedNodeKey)
  emit('selectionChange', selectedElement.value)
  captureViewState()
  scheduleOverlayPosition()
}

function handleBlankClick() {
  clearSelection(true)
}

function handleViewportChange() {
  syncViewportTransform()
  captureViewState()
  scheduleOverlayPosition()
}

function handleGraphResize() {
  scheduleOverlayPosition()
}

function handleCanvasWheel(event: WheelEvent) {
  if (!graph || !canvasHost.value) {
    return
  }
  const panDelta = resolveTiaGraphWheelPan(event, canvasHost.value.clientHeight)
  if (panDelta === null) {
    return
  }
  event.preventDefault()
  if (panDelta === 0) {
    return
  }
  const translation = graph.translate()
  graph.translate(translation.tx, translation.ty - panDelta)
}

function scheduleFit() {
  window.cancelAnimationFrame(fitFrame)
  fitFrame = window.requestAnimationFrame(() => {
    if (!graph) {
      return
    }
    const currentGraph = graph
    resizeGraphToCanvas()
    fitTiaGraphViewport({
      fit: options => currentGraph.zoomToFit(options),
      zoom: () => currentGraph.zoom(),
      anchorTop: () => {
        const firstNode = currentGraph.getNodes()[0]
        if (firstNode) {
          const previousTranslation = currentGraph.translate()
          currentGraph.positionCell(firstNode, 'top')
          const positionedTranslation = currentGraph.translate()
          currentGraph.translate(previousTranslation.tx, positionedTranslation.ty)
        }
      },
      shiftY: (delta) => {
        const translation = currentGraph.translate()
        currentGraph.translate(translation.tx, translation.ty + delta)
      },
      anchorLeft: () => {
        const previousTranslation = currentGraph.translate()
        currentGraph.positionContent('left')
        const positionedTranslation = currentGraph.translate()
        currentGraph.translate(positionedTranslation.tx, previousTranslation.ty)
      },
      shiftX: (delta) => {
        const translation = currentGraph.translate()
        currentGraph.translate(translation.tx + delta, translation.ty)
      },
    }, { alignLeft: hasRuntimeDetail.value })
  })
}

function zoomCanvas(direction: 'in' | 'out') {
  if (!graph) {
    return
  }
  const requested = nextTiaGraphZoom(graph.zoom(), direction)
  graph.zoom(requested, { absolute: true, minScale: 0.2, maxScale: 2 })
}

function fitCanvas() {
  scheduleFit()
}

function locateCurrentStep(pulse = true) {
  if (!graph || !toolbarState.value.canLocateActiveStep) {
    return
  }
  const sourceKey = toolbarState.value.activeStepSourceKey
  const node = graph.getNodes().find(item => item.getData<CanvasNodeData>().sourceKey === sourceKey)
  if (!node) {
    return
  }
  graph.centerCell(node)
  if (!pulse) {
    return
  }
  const view = graph.findViewByCell(node)
  if (!view) {
    return
  }
  window.clearTimeout(locatePulseTimer)
  view.container.classList.remove('is-locate-pulse')
  void view.container.getBoundingClientRect()
  view.container.classList.add('is-locate-pulse')
  locatePulseTimer = window.setTimeout(() => {
    view.container.classList.remove('is-locate-pulse')
    locatePulseTimer = 0
  }, 1200)
}

/** 自动定位（默认开，2026-09-11 需求②）：每次取值刷新都把画布居中到第一个 true 的工步
 * （含 Waiting/Warning/超时中仍为 true 的步）；同一步停留期间持续吸附视口、
 * 脉冲高亮只在目标步切换时触发一次。需自由拖动/缩放时点「自动定位」关闭。 */
const followActiveStep = ref(true)
let lastFollowedStepKey = ''

function maybeFollowActiveStep() {
  if (!followActiveStep.value) {
    return
  }
  const sourceKey = toolbarState.value.activeStepSourceKey
  if (!sourceKey) {
    return
  }
  const changed = sourceKey !== lastFollowedStepKey
  lastFollowedStepKey = sourceKey
  locateCurrentStep(changed)
}

function toggleFollowActiveStep() {
  followActiveStep.value = !followActiveStep.value
  // 实时开关：重新开启时立即定位到当前活动步（不等到下一次取值刷新）
  if (followActiveStep.value) {
    maybeFollowActiveStep()
  }
}

function compactLabel(value: string, maximumLength: number) {
  return value.length > maximumLength ? `${value.slice(0, maximumLength - 1)}…` : value
}

function transitionVisualRightExtent(item: TiaLayoutNode) {
  // 标题/描述已移到转换条下方靠右，不再向右侧延伸占位
  return item.width
}

function stepNode(item: TiaLayoutNode) {
  const accent = item.isInitial ? '#008f83' : item.isFinal ? '#5f6b7a' : '#66717d'
  return {
    id: item.id,
    x: item.x,
    y: item.y,
    width: item.width,
    height: item.height,
    zIndex: 2,
    data: {
      sourceKey: item.id,
      nodeKind: 'Step',
      baseColor: item.isMissing ? '#cf1322' : accent,
      baseStrokeWidth: item.isInitial ? 3 : 2,
      logicalVisualRightExtent: item.width,
    } satisfies CanvasNodeData,
    markup: [
      { tagName: 'rect', selector: 'body' },
      { tagName: 'rect', selector: 'header' },
      { tagName: 'path', selector: 'divider' },
      { tagName: 'rect', selector: 'selectionOutline' },
      { tagName: 'text', selector: 'number' },
      { tagName: 'text', selector: 'label' },
    ],
    attrs: {
      body: {
        width: item.width,
        height: item.height,
        rx: 0,
        ry: 0,
        fill: item.isMissing ? '#fff2f0' : '#e5e8eb',
        stroke: item.isMissing ? '#cf1322' : accent,
        strokeWidth: item.isInitial ? 3 : 2,
      },
      // 明确上下两格：上格=编号行（浅底+清晰分隔线），下格=描述行
      header: {
        width: item.width,
        height: 22,
        fill: '#e6ebf0',
        stroke: 'none',
      },
      divider: {
        d: `M 0 22 L ${item.width} 22`,
        stroke: '#98a2b3',
        strokeWidth: 1.5,
      },
      selectionOutline: {
        x: -3,
        y: -3,
        width: item.width + 6,
        height: item.height + 6,
        fill: 'none',
        stroke: 'transparent',
        strokeWidth: 3,
        pointerEvents: 'none',
      },
      // 上下两格：编号独占上格（水平+垂直居中），描述独占下格（居中）
      // ⚠️ X6 文本以节点中心为锚点，x/y 是相对中心的偏移（不是左上角绝对坐标）：
      //    实际渲染位 = (x + width/2, y + height/2)，故按目标位减去半宽高补偿
      number: {
        x: 0,
        y: item.height / 2 * -1 + 16.5,
        text: `S${item.number}`,
        fill: '#007c77',
        fontFamily: 'Consolas, "Microsoft YaHei", sans-serif',
        fontSize: 16,
        fontWeight: 700,
        textAnchor: 'middle',
      },
      label: {
        x: 0,
        y: item.height / 2 * -1 + 47.5,
        text: compactLabel(item.label, 14),
        fill: '#141b24',
        fontFamily: '"Microsoft YaHei", sans-serif',
        fontSize: 12,
        textAnchor: 'middle',
      },
    },
  }
}

function transitionNode(item: TiaLayoutNode) {
  const baseColor = item.isMissing ? '#cf1322' : '#111827'
  return {
    id: item.id,
    x: item.x,
    y: item.y,
    width: item.width,
    height: item.height,
    zIndex: 2,
    data: {
      sourceKey: item.id,
      nodeKind: 'Transition',
      baseColor,
      baseStrokeWidth: 0,
      logicalVisualRightExtent: transitionVisualRightExtent(item),
    } satisfies CanvasNodeData,
    markup: [
      { tagName: 'rect', selector: 'bar' },
      { tagName: 'rect', selector: 'selectionOutline' },
      { tagName: 'text', selector: 'number' },
      { tagName: 'text', selector: 'condition' },
    ],
    attrs: {
      bar: {
        width: item.width,
        height: item.height,
        fill: baseColor,
        stroke: 'none',
      },
      selectionOutline: {
        x: -4,
        y: -4,
        width: item.width + 8,
        height: item.height + 8,
        fill: 'none',
        stroke: 'transparent',
        strokeWidth: 3,
        pointerEvents: 'none',
      },
      // 编码放黑框内白字居中放大（X6 文本锚点=节点中心，坐标按半宽高补偿）；描述放条正下方、以条中心点为轴居中
      number: {
        x: 0,
        y: item.height / 2 - 7,
        text: `T${item.number}`,
        fill: '#ffffff',
        fontFamily: 'Consolas, "Microsoft YaHei", sans-serif',
        fontSize: 14,
        fontWeight: 700,
        textAnchor: 'middle',
      },
      condition: {
        x: 0,
        y: item.height / 2 + 12,
        text: compactLabel(item.conditionSummary, 36),
        fill: '#344054',
        fontFamily: 'Consolas, "Microsoft YaHei", sans-serif',
        fontSize: 9,
        textAnchor: 'middle',
        stroke: '#fff',
        strokeWidth: 3,
        paintOrder: 'stroke',
      },
    },
  }
}

/** 边基础配色：并行=青色（配双轨线，S7 同步分支画法）；选择/条件分支=蓝色；顺序=黑；跳转=虚线黑 */
function edgeBaseColor(kind: string) {
  if (kind.startsWith('Parallel')) {
    return '#005f73'
  }
  if (kind.startsWith('Selection')) {
    return '#2563eb'
  }
  return '#111827'
}

function edgeBaseStrokeWidth(kind: string) {
  if (kind.startsWith('Parallel')) {
    return 2.4
  }
  if (kind.startsWith('Selection')) {
    return 2.2
  }
  return 2
}

function renderGraph(restoreSelectedNode = true) {
  if (!graph) {
    return
  }
  clearSelection(false)
  graph.clearCells()
  if (!props.sequence) {
    parallelRails.value = []
    return
  }

  const layout = buildTiaGraphLayout(props.sequence)
  parallelRails.value = buildTiaParallelRails(layout)
  graph.addNodes(layout.nodes.map(item => item.kind === 'Step' ? stepNode(item) : transitionNode(item)))
  graph.addEdges(layout.edges.map(item => ({
    id: item.id,
    source: { cell: item.source, anchor: 'bottom' },
    target: { cell: item.target, anchor: 'top' },
    vertices: item.vertices,
    router: { name: 'orth', args: { padding: 0 } },
    connector: { name: 'rounded', args: { radius: 4 } },
    zIndex: 1,
    data: {
      sourceKey: item.id,
      baseColor: edgeBaseColor(item.kind),
      baseStrokeWidth: edgeBaseStrokeWidth(item.kind),
      dashed: item.dashed,
    } satisfies CanvasEdgeData,
    attrs: {
      line: {
        stroke: edgeBaseColor(item.kind),
        strokeWidth: edgeBaseStrokeWidth(item.kind),
        strokeDasharray: item.dashed ? '7 5' : '',
        sourceMarker: null,
        targetMarker: null,
      },
    },
    labels: item.label
      ? [{
          position: 0.54,
          attrs: {
            body: { fill: '#fff', stroke: '#98a2b3', strokeWidth: 1, rx: 2, ry: 2 },
            label: { text: item.label, fill: '#344054', fontSize: 10 },
          },
        }]
      : [],
  })))
  restoreSelectionFromViewState(restoreSelectedNode)
  applySelectedNodeStyle()
  if (props.viewState) {
    restoringView = true
    graph.scale(props.viewState.zoom)
    graph.translate(props.viewState.translateX, props.viewState.translateY)
    syncViewportTransform()
    queueMicrotask(() => {
      restoringView = false
    })
  }
  else {
    scheduleFit()
  }
  if (selectedElement.value) {
    scheduleOverlayPosition()
  }
}

onMounted(() => {
  if (!container.value) {
    return
  }
  const initialSize = canvasSize()
  graph = new Graph({
    container: container.value,
    width: initialSize.width,
    height: initialSize.height,
    background: { color: '#fbfcfd' },
    grid: {
      visible: true,
      type: 'doubleMesh',
      args: [
        { color: '#e6e9ed', thickness: 1 },
        { color: '#d5d9df', thickness: 1, factor: 5 },
      ],
    },
    interacting: false,
    panning: true,
    mousewheel: {
      enabled: true,
      modifiers: ['ctrl', 'meta'],
      minScale: 0.2,
      maxScale: 2,
    },
  })
  graph.on('node:click', handleNodeClick)
  graph.on('blank:click', handleBlankClick)
  graph.on('scale', handleViewportChange)
  graph.on('translate', handleViewportChange)
  graph.on('resize', handleGraphResize)
  resizeObserver = new ResizeObserver(() => {
    if (!container.value || !graph) {
      return
    }
    resizeGraphToCanvas()
    scheduleOverlayPosition()
  })
  resizeObserver.observe(container.value)
  if (container.value.parentElement) {
    resizeObserver.observe(container.value.parentElement)
  }
  renderGraph()
})

watch(() => props.sequence, async () => {
  clearSelection(true)
  // 切换顺控/graph 后活动步 key 重新判定，允许首个快照重新定位
  lastFollowedStepKey = ''
  await nextTick()
  renderGraph(false)
}, { deep: true })

watch(() => props.runtime, () => {
  applySelectedNodeStyle()
  maybeFollowActiveStep()
}, { deep: true })

watch(() => props.reservedRight, scheduleOverlayPosition)

watch(
  () => [selectedElement.value?.kind, selectedElement.value?.node.sourceKey, selectedElement.value?.node.number] as const,
  async ([kind, , number]) => {
    if (!props.stepTagProjectId || kind !== 'step' || !number) {
      stepTags.value = []
      return
    }
    const token = ++stepTagsToken
    stepTagsLoading.value = true
    try {
      const rows = await getStepTagRowsByStep(props.stepTagProjectId, number, props.stepTagFbName || undefined)
      if (token === stepTagsToken) {
        stepTags.value = rows
      }
    }
    catch {
      if (token === stepTagsToken) {
        stepTags.value = []
      }
    }
    finally {
      if (token === stepTagsToken) {
        stepTagsLoading.value = false
      }
    }
  },
)

onBeforeUnmount(() => {
  stopOverlayDrag()
  resizeObserver?.disconnect()
  window.cancelAnimationFrame(fitFrame)
  window.cancelAnimationFrame(overlayFrame)
  window.clearTimeout(locatePulseTimer)
  const currentGraph = graph
  if (currentGraph) {
    currentGraph.off('node:click', handleNodeClick)
    currentGraph.off('blank:click', handleBlankClick)
    currentGraph.off('scale', handleViewportChange)
    currentGraph.off('translate', handleViewportChange)
    currentGraph.off('resize', handleGraphResize)
    currentGraph.dispose()
  }
  graph = null
})
</script>

<template>
  <div ref="canvasHost" class="tia-graph-canvas" @wheel="handleCanvasWheel">
    <div v-if="!sequence" class="tia-graph-canvas__empty">
      {{ emptyTipText }}
    </div>
    <div ref="container" class="tia-graph-canvas__surface" />
    <svg class="tia-graph-canvas__parallel-rails" aria-hidden="true">
      <g :transform="parallelRailTransform">
        <line
          v-for="rail in parallelRails"
          :key="rail.key"
          :x1="rail.x1"
          :x2="rail.x2"
          :y1="rail.secondaryY"
          :y2="rail.secondaryY"
        />
      </g>
    </svg>
    <div v-if="!hasRuntimeDetail" class="tia-graph-canvas__toolbar" :aria-label="$t('lineupShared.canvasToolbarAria')">
      <button type="button" class="is-active" aria-pressed="true" :title="$t('lineupShared.selectNodeTitle')">
        {{ $t('lineupShared.selectBtn') }}
      </button>
      <button type="button" :title="$t('lineupShared.zoomOut')" @click="zoomCanvas('out')">
        {{ $t('lineupShared.zoomOut') }}
      </button>
      <button type="button" :title="$t('lineupShared.zoomIn')" @click="zoomCanvas('in')">
        {{ $t('lineupShared.zoomIn') }}
      </button>
      <button type="button" :title="$t('lineupShared.fitCanvas')" @click="fitCanvas">
        {{ $t('lineupShared.fitCanvas') }}
      </button>
      <span class="tia-graph-canvas__online" :class="`is-${toolbarState.onlineTone}`">
        <i />{{ toolbarState.onlineLabel }}
      </span>
    </div>
    <div v-else class="tia-graph-canvas__legend" :aria-label="$t('lineupShared.legendAria')">
      <span><i class="is-active" />{{ $t('lineupShared.legendActive') }}</span>
      <span><i class="is-timeout" />{{ $t('lineupShared.legendTimeout') }}</span>
      <span><i class="is-waiting" />{{ $t('lineupShared.legendWaiting') }}</span>
      <span><i class="is-completed" />{{ $t('lineupShared.legendCompleted') }}</span>
      <span><i class="is-warning" />{{ $t('lineupShared.legendWarning') }}</span>
      <span><i class="is-error" />{{ $t('lineupShared.legendError') }}</span>
      <span><i class="is-idle" />{{ $t('lineupShared.legendIdleUnknown') }}</span>
      <button
        type="button"
        class="tia-graph-canvas__locate"
        :disabled="!toolbarState.canLocateActiveStep"
        :title="$t('lineupShared.locateTitle')"
        @click="locateCurrentStep"
      >
        {{ $t('lineupShared.locateBtn') }}
      </button>
      <button
        type="button"
        class="tia-graph-canvas__follow"
        :class="{ 'is-on': followActiveStep }"
        :aria-pressed="followActiveStep"
        :title="followActiveStep ? $t('lineupShared.followOnTitle') : $t('lineupShared.followOffTitle')"
        @click="toggleFollowActiveStep"
      >
        {{ followActiveStep ? $t('lineupShared.followOn') : $t('lineupShared.followOff') }}
      </button>
      <small>{{ $t('lineupShared.wheelHint') }}</small>
    </div>
    <div v-if="sequence" class="tia-graph-canvas__line-legend" :aria-label="$t('lineupShared.lineLegendAria')">
      <span><i class="is-seq" />{{ $t('lineupShared.lineLegendSeq') }}</span>
      <span><i class="is-sel" />{{ $t('lineupShared.lineLegendSel') }}</span>
      <span><i class="is-par" />{{ $t('lineupShared.lineLegendPar') }}</span>
      <span><i class="is-rail" />{{ $t('lineupShared.lineLegendRail') }}</span>
      <span><i class="is-jump" />{{ $t('lineupShared.lineLegendJump') }}</span>
    </div>
    <div
      v-if="selectedElement && overlayPlacement"
      class="tia-graph-canvas__overlay"
      :class="{ 'is-dragging': overlayDragging }"
      :style="overlayStyle"
      @pointerdown.stop="startOverlayDrag"
      @click.stop
      @wheel.stop
    >
      <TiaLogicNetworkPanel
        :selection="selectedElement"
        :runtime-values="runtimeValues"
        :active-duration-ms="runtime?.nodes[selectedElement.node.sourceKey]?.activeDurationText ?? null"
        :step-tags="stepTags"
        :has-step-tag-source="Boolean(stepTagProjectId)"
        :bom-sub-station-id="bomSubStationId"
        :bom-references="bomReferences"
        :saved-node="savedNodeImages"
        :plc-project-id="stepTagProjectId"
        :graph-fb-name="stepTagFbName"
      >
        <!-- 2026-09-07: 仅在宿主真的提供了 runtime-detail 插槽（运行监控上下文）时才向面板转发。
             此前无条件转发会让静态预览里的 TiaLogicNetworkPanel 误判 hasRuntimeDetail=true：
             ① 弹框出现 运行详情/实时条件/异常定位/事件 幻影页签；② 无台账数据源时默认页签
             落到空的 runtime-step（工步/转换弹框默认页签错位的根因）。 -->
        <template v-if="hasRuntimeDetail" #runtime-detail="runtimeDetailProps">
          <slot name="runtime-detail" v-bind="runtimeDetailProps" />
        </template>
      </TiaLogicNetworkPanel>
    </div>
  </div>
</template>

<style scoped>
.tia-graph-canvas {
  position: relative;
  width: 100%;
  min-width: 0;
  height: 100%;
  min-height: 280px;
  overflow: hidden;
  background: #fbfcfd;
  border: 1px solid var(--el-border-color-light);
}

.tia-graph-canvas__surface {
  position: absolute;
  inset: 0;
}

.tia-graph-canvas__parallel-rails {
  position: absolute;
  inset: 0;
  z-index: 2;
  width: 100%;
  height: 100%;
  overflow: hidden;
  pointer-events: none;
}

.tia-graph-canvas__parallel-rails line {
  fill: none;
  stroke: #005f73;
  stroke-width: 2.4;
  vector-effect: non-scaling-stroke;
}

.tia-graph-canvas__toolbar {
  position: absolute;
  top: 8px;
  left: 8px;
  z-index: 3;
  display: flex;
  gap: 4px;
  align-items: center;
  padding: 4px;
  font-size: 11px;
  background: rgb(255 255 255 / 94%);
  border: 1px solid #b8bec7;
  box-shadow: 0 2px 7px rgb(15 23 42 / 12%);
}

.tia-graph-canvas__legend {
  position: absolute;
  top: 8px;
  right: 8px;
  z-index: 3;
  display: flex;
  flex-wrap: wrap;
  gap: 7px 12px;
  align-items: center;
  max-width: calc(100% - 16px);
  padding: 7px 10px;
  font-size: 11px;
  color: #475467;
  pointer-events: none;
  background: rgb(255 255 255 / 94%);
  border: 1px solid #c7cdd5;
  border-radius: 4px;
  box-shadow: 0 2px 7px rgb(15 23 42 / 10%);
}

.tia-graph-canvas__legend span {
  display: inline-flex;
  gap: 5px;
  align-items: center;
  white-space: nowrap;
}

.tia-graph-canvas__legend i {
  width: 8px;
  height: 8px;
  border-radius: 50%;
}

.tia-graph-canvas__legend i.is-active {
  /* 与 runtimePalette.active 的深蓝底一致（活动步 = 工步变量为 true） */
  background: #1e3a8a;
}

.tia-graph-canvas__legend i.is-timeout {
  /* 与 runtimePalette.timeout 一致：活动步深蓝的淡化版（超时非异常/告警） */
  background: #4a69ad;
}

.tia-graph-canvas__legend i.is-waiting {
  background: #2f54eb;
}

.tia-graph-canvas__legend i.is-completed {
  background: #95de64;
}

.tia-graph-canvas__legend i.is-warning {
  background: #d48806;
}

.tia-graph-canvas__legend i.is-error {
  background: #cf1322;
}

.tia-graph-canvas__legend i.is-idle {
  background: #8c8c8c;
}

/* 图例容器 pointer-events:none（不挡画布拖动），定位/自动定位按钮单独恢复可点击 */
.tia-graph-canvas__legend button {
  pointer-events: auto;
  cursor: pointer;
  padding: 2px 9px;
  font-size: 11px;
  color: #344054;
  background: #fff;
  border: 1px solid #c7cdd5;
  border-radius: 10px;
  transition: border-color 0.15s ease, color 0.15s ease, background 0.15s ease;
}

.tia-graph-canvas__legend button:hover:not(:disabled) {
  border-color: #2f54eb;
}

.tia-graph-canvas__legend button:disabled {
  cursor: not-allowed;
  opacity: 0.45;
}

.tia-graph-canvas__legend button.is-on {
  color: #1e3a8a;
  background: #f0f5ff;
  border-color: #2f54eb;
}

.tia-graph-canvas__legend small {
  color: #7b8492;
  white-space: nowrap;
}

.tia-graph-canvas__toolbar button {
  min-height: 26px;
  padding: 3px 8px;
  font: inherit;
  color: #344054;
  cursor: pointer;
  background: #f6f7f9;
  border: 1px solid transparent;
}

.tia-graph-canvas__toolbar button:hover:not(:disabled),
.tia-graph-canvas__toolbar button.is-active {
  color: #005f73;
  background: #e7f5f6;
  border-color: #82b9c0;
}

.tia-graph-canvas__toolbar button:disabled {
  color: #a5abb3;
  cursor: not-allowed;
  background: #f3f4f6;
}

.tia-graph-canvas__online {
  display: inline-flex;
  gap: 5px;
  align-items: center;
  padding: 0 6px;
  color: #667085;
  white-space: nowrap;
}

.tia-graph-canvas__online i {
  width: 7px;
  height: 7px;
  background: #98a2b3;
  border-radius: 50%;
}

.tia-graph-canvas__online.is-online i {
  background: #2f9e44;
  box-shadow: 0 0 0 3px rgb(47 158 68 / 14%);
}

.tia-graph-canvas__online.is-degraded i,
.tia-graph-canvas__online.is-connecting i {
  background: #d48806;
}

.tia-graph-canvas__online.is-offline i {
  background: #cf1322;
}

.tia-graph-canvas :deep(.x6-node.is-locate-pulse) {
  animation: tia-locate-pulse 0.6s ease-in-out 2;
}

@keyframes tia-locate-pulse {
  50% {
    opacity: 0.62;
    filter: drop-shadow(0 0 8px #f79009);
  }
}

.tia-graph-canvas__line-legend {
  position: absolute;
  right: 8px;
  bottom: 8px;
  z-index: 3;
  display: flex;
  flex-wrap: wrap;
  gap: 6px 12px;
  align-items: center;
  max-width: calc(100% - 16px);
  padding: 6px 10px;
  font-size: 11px;
  color: #475467;
  pointer-events: none;
  background: rgb(255 255 255 / 94%);
  border: 1px solid #c7cdd5;
  border-radius: 4px;
  box-shadow: 0 2px 7px rgb(15 23 42 / 10%);
}

.tia-graph-canvas__line-legend span {
  display: inline-flex;
  gap: 5px;
  align-items: center;
  white-space: nowrap;
}

.tia-graph-canvas__line-legend i {
  display: inline-block;
  width: 20px;
  height: 0;
}

.tia-graph-canvas__line-legend i.is-seq {
  border-top: 2px solid #111827;
}

.tia-graph-canvas__line-legend i.is-sel {
  border-top: 2px solid #2563eb;
}

.tia-graph-canvas__line-legend i.is-par {
  border-top: 3px solid #005f73;
}

.tia-graph-canvas__line-legend i.is-rail {
  height: 6px;
  border-top: 2px solid #005f73;
  border-bottom: 2px solid #005f73;
}

.tia-graph-canvas__line-legend i.is-jump {
  border-top: 2px dashed #111827;
}

.tia-graph-canvas__overlay {
  position: absolute;
  z-index: 4;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
  pointer-events: auto;
}

.tia-graph-canvas__overlay :deep(.panel-title) {
  touch-action: none;
  cursor: move;
  user-select: none;
}

.tia-graph-canvas__overlay.is-dragging :deep(.panel-title) {
  cursor: grabbing;
}

.tia-graph-canvas__empty {
  position: absolute;
  inset: 0;
  z-index: 3;
  display: grid;
  place-items: center;
  color: var(--el-text-color-secondary);
  pointer-events: none;
}
</style>
