import { readFileSync } from 'node:fs'
import { compileScript, compileTemplate, parse } from '@vue/compiler-sfc'
import { describe, expect, it } from 'vitest'

const canvasSource = readFileSync(new URL('./LineUpGraphCanvas.vue', import.meta.url), 'utf8')
const layoutSource = readFileSync(new URL('./tiaGraphLayout.ts', import.meta.url), 'utf8')
const previewPanelSource = readFileSync(new URL('../workspace/components/GraphPreviewPanel.vue', import.meta.url), 'utf8')
const descriptor = parse(canvasSource, { filename: 'LineUpGraphCanvas.vue' }).descriptor
const script = descriptor.scriptSetup?.content ?? ''
const template = descriptor.template?.content ?? ''
const previewDescriptor = parse(previewPanelSource, { filename: 'GraphPreviewPanel.vue' }).descriptor
const previewScript = previewDescriptor.scriptSetup?.content ?? ''
const previewTemplate = previewDescriptor.template?.content ?? ''

function occurrences(source: string, pattern: RegExp) {
  return [...source.matchAll(pattern)].length
}

describe('lineUpGraphCanvas TIA overlay integration contract', () => {
  it('imports and calls the exact selector, placement function, and readonly panel', () => {
    expect(script).toMatch(/import \{ selectTiaGraphElement \} from '\.\/tiaGraphSelection'/)
    expect(script).toMatch(/import \{ dragTiaOverlay, placeTiaOverlay \} from '\.\/tiaOverlayPlacement'/)
    expect(script).toMatch(/import TiaLogicNetworkPanel from '\.\/TiaLogicNetworkPanel\.vue'/)
    expect(layoutSource).toMatch(/actions\?: LineUpImportAction\[\]/)
    expect(script).toMatch(/selectTiaGraphElement\(\s*\{ \.\.\.props\.sequence, actions: props\.sequence\.actions \?\? \[\] \},\s*sourceKey,?\s*\)/)
    expect(script).toMatch(/placeTiaOverlay\(\{[\s\S]*reservedRight: props\.reservedRight,[\s\S]*\}\)/)
    expect(template).toContain('<TiaLogicNetworkPanel')
  })

  it('registers blank and viewport listeners and removes every owned resource', () => {
    expect(script).toMatch(/graph\.on\('node:click', handleNodeClick\)/)
    expect(script).toMatch(/graph\.on\('blank:click', handleBlankClick\)/)
    expect(script).toMatch(/graph\.on\('scale', handleViewportChange\)/)
    expect(script).toMatch(/graph\.on\('translate', handleViewportChange\)/)
    expect(script).toMatch(/graph\.on\('resize', handleGraphResize\)/)
    expect(script).toMatch(/new ResizeObserver\(\(\) => \{[\s\S]*scheduleOverlayPosition\(\)/)

    expect(script).toMatch(/currentGraph\.off\('node:click', handleNodeClick\)/)
    expect(script).toMatch(/currentGraph\.off\('blank:click', handleBlankClick\)/)
    expect(script).toMatch(/currentGraph\.off\('scale', handleViewportChange\)/)
    expect(script).toMatch(/currentGraph\.off\('translate', handleViewportChange\)/)
    expect(script).toMatch(/currentGraph\.off\('resize', handleGraphResize\)/)
    expect(script).toMatch(/resizeObserver\?\.disconnect\(\)/)
    expect(script).toMatch(/window\.cancelAnimationFrame\(fitFrame\)/)
    expect(script).toMatch(/window\.cancelAnimationFrame\(overlayFrame\)/)
    expect(script).toMatch(/currentGraph\.dispose\(\)/)
  })

  it('resizes the graph without resetting the viewport and still fits an initial unsaved view', () => {
    const observerStart = script.indexOf('resizeObserver = new ResizeObserver')
    const observerEnd = script.indexOf('resizeObserver.observe', observerStart)
    const observerBody = script.slice(observerStart, observerEnd)

    expect(observerStart).toBeGreaterThanOrEqual(0)
    expect(observerEnd).toBeGreaterThan(observerStart)
    expect(observerBody).toContain('resizeGraphToCanvas()')
    expect(observerBody).toContain('scheduleOverlayPosition()')
    expect(observerBody).not.toContain('scheduleFit()')

    const renderStart = script.indexOf('function renderGraph')
    const renderEnd = script.indexOf('onMounted(', renderStart)
    const renderBody = script.slice(renderStart, renderEnd)
    expect(renderBody).toMatch(/if \(props\.viewState\) \{[\s\S]*\}\s*else \{\s*scheduleFit\(\)\s*\}/)
  })

  it('converts the selected X6 node rectangle into host-local placement coordinates', () => {
    expect(script).toMatch(/const nodeClientBounds = graph\.localToClient\(node\.getBBox\(\)\)/)
    expect(script).toMatch(/const hostBounds = canvasHost\.value\.getBoundingClientRect\(\)/)
    expect(script).toMatch(/x: nodeClientBounds\.x - hostBounds\.left/)
    expect(script).toMatch(/y: nodeClientBounds\.y - hostBounds\.top/)
    expect(script).toMatch(/left: `\$\{placement\.left\}px`/)
    expect(script).toMatch(/top: `\$\{placement\.top\}px`/)
    expect(script).toMatch(/maxWidth: `\$\{placement\.maxWidth\}px`/)
    expect(script).toMatch(/maxHeight: `\$\{placement\.maxHeight\}px`/)
    expect(template).toContain(':style="overlayStyle"')
  })

  it('extends a transition overlay anchor past its compacted labels at the graph zoom', () => {
    expect(script).toMatch(/interface CanvasNodeData \{[\s\S]*logicalVisualRightExtent: number/)
    // 标题/描述移到转换条下方后不再向右延伸占位（转换条视觉右边界 = 自身宽度）
    expect(script).toMatch(/function transitionVisualRightExtent\(item: TiaLayoutNode\) \{[\s\S]*return item\.width/)
    expect(script).toMatch(/function compactLabel\(value: string, maximumLength: number\)/)
    expect(script).toMatch(/function stepNode[\s\S]*logicalVisualRightExtent: item\.width/)
    expect(script).toMatch(/function transitionNode[\s\S]*logicalVisualRightExtent: transitionVisualRightExtent\(item\)/)
    expect(script).toMatch(/const graphZoom = graph\.zoom\(\)/)
    expect(script).toMatch(/const visualRightExtension = Math\.max\(0, nodeData\.logicalVisualRightExtent - node\.getBBox\(\)\.width\) \* graphZoom/)
    expect(script).toMatch(/width: nodeClientBounds\.width \+ visualRightExtension/)
  })

  it('mounts exactly one interactive readonly overlay and emits exact selection changes', () => {
    expect(occurrences(template, /class="tia-graph-canvas__overlay"/g)).toBe(1)
    expect(template).toMatch(/v-if="selectedElement && overlayPlacement"/)
    expect(template).toContain('@pointerdown.stop')
    expect(template).toContain('@click.stop')
    expect(template).toContain(':active-duration-ms="runtime?.nodes[selectedElement.node.sourceKey]?.activeDurationText ?? null"')
    expect(template).not.toContain('runtime-values="snapshot.conditions"')

    expect(script).toMatch(/selectionChange: \[selection: TiaGraphSelection \| null\]/)
    expect(script).toMatch(/emit\('nodeClick', selectedNodeKey\)/)
    expect(script).toMatch(/emit\('selectionChange', selectedElement\.value\)/)
    expect(script).toMatch(/function handleBlankClick\(\) \{\s*clearSelection\(true\)\s*\}/)
    expect(script).toMatch(/if \(emitChange\) \{\s*emit\('selectionChange', null\)/)
    expect(script).not.toContain('emitChange && hadSelection')
    expect(script).toMatch(/watch\(\(\) => props\.sequence,[\s\S]*clearSelection\(true\)[\s\S]*renderGraph\(false\)/)
    expect(script).toMatch(/watch\(\(\) => props\.reservedRight, scheduleOverlayPosition\)/)
  })

  it('keeps runtime detail in a draggable overlay instead of a fixed right pane', () => {
    expect(template).not.toContain('tia-graph-workspace__detail')
    expect(template).not.toContain('aria-label="当前工步运行详情"')
    expect(template).toContain('@pointerdown.stop="startOverlayDrag"')
    expect(template).toContain('<slot name="runtime-detail" v-bind="runtimeDetailProps" />')
    expect(script).toMatch(/target\.closest\('\.panel-title'\)/)
    expect(script).toMatch(/window\.addEventListener\('pointermove', handleOverlayDragMove\)/)
    expect(script).toMatch(/overlayPlacement\.value = dragTiaOverlay\(\{/)
    expect(script).toMatch(/window\.removeEventListener\('pointermove', handleOverlayDragMove\)/)
  })

  it('keeps selection outline separate from runtime fill and keeps X6 readonly', () => {
    expect(script).toMatch(/interacting: false/)
    expect(script).not.toMatch(/interacting: true/)
    expect(script).toMatch(/node\.attr\('body\/fill', palette\?\.fill \?\?/)
    expect(script).toMatch(/node\.attr\('bar\/fill', palette\?\.stroke \?\?/)
    expect(script).toMatch(/node\.attr\('selectionOutline\/stroke', selected \? '#f79009' : 'transparent'\)/)
    expect(script).not.toMatch(/node\.attr\('(body\/fill|bar\/fill)',[^\n]*selected/)
  })

  it('renders shared Parallel double rails outside X6 cell ownership without duplicating edges', () => {
    expect(script).toMatch(/import \{ buildTiaGraphLayout, buildTiaParallelRails \} from '\.\/tiaGraphLayout'/)
    expect(script).toContain('parallelRails.value = buildTiaParallelRails(layout)')
    expect(template).toContain('class="tia-graph-canvas__parallel-rails"')
    expect(template).toContain('v-for="rail in parallelRails"')
    expect(template).toContain(':y1="rail.secondaryY"')
    expect(template).not.toContain('graph.addEdges(parallelRails')
    expect(script).toContain('graph.addEdges(layout.edges.map')
  })

  it('keeps the readonly toolbar for static review but replaces it with a compact runtime legend in monitor mode', () => {
    // 2026-09-14：静态预览与设备运行监控分离——定位类按钮只存在于监控图例，静态工具栏不再出现
    // （4f3d884 起工具栏/图例文案走 i18n，按 lineupShared.* 键名断言）
    for (const key of ['lineupShared.selectBtn', 'lineupShared.zoomOut', 'lineupShared.zoomIn', 'lineupShared.fitCanvas']) {
      expect(template).toContain(key)
    }
    // 2026-09-11 B：超时独立淡色图例（不再与异常共色）
    for (const key of ['lineupShared.legendActive', 'lineupShared.legendTimeout', 'lineupShared.legendWaiting', 'lineupShared.legendCompleted', 'lineupShared.legendWarning', 'lineupShared.legendError', 'lineupShared.legendIdleUnknown']) {
      expect(template).toContain(key)
    }
    expect(template).toContain('v-if="!hasRuntimeDetail"')
    expect(template).toContain('class="tia-graph-canvas__legend"')
    expect(template).toContain('lineupShared.wheelHint')
    expect(template).toContain('toolbarState.onlineLabel')
    expect(template).toContain(`:disabled="!toolbarState.canLocateActiveStep"`)
    expect(script).toContain(`nextTiaGraphZoom(graph.zoom(), direction)`)
    expect(script).toContain(`graph.zoom(requested, { absolute: true, minScale: 0.2, maxScale: 2 })`)
    expect(script).toContain('graph.centerCell(node)')
    expect(script).toContain(`view.container.classList.add('is-locate-pulse')`)

    const locateStart = script.indexOf('function locateCurrentStep')
    const locateEnd = script.indexOf('\nfunction ', locateStart + 1)
    const locateBody = script.slice(locateStart, locateEnd)
    expect(locateBody).not.toContain(`emit('selectionChange'`)
    expect(locateBody).not.toContain('selectedNodeKey =')
    // 「连线」不进负向集——模板含合法的「GRAPH 连线类型图例」aria-label，非编辑功能
    expect(template).not.toMatch(/(编辑|删除|新增)/)
  })

  it('follows the first true step on every value refresh with a default-on auto locate toggle in the runtime legend', () => {
    // 默认开启，实时可关；重开立即定位
    expect(script).toContain('const followActiveStep = ref(true)')
    expect(script).toContain('function maybeFollowActiveStep(')
    expect(script).toContain('function toggleFollowActiveStep(')
    expect(script).toMatch(/if \(followActiveStep\.value\) \{[\s\S]*maybeFollowActiveStep\(\)/)
    // 2026-09-11 需求②：每次取值刷新都居中第一个 true 工步（同一步停留期间持续吸附、脉冲只在切换时触发）
    expect(script).toMatch(/watch\(\(\) => props\.runtime, \(\) => \{[\s\S]*applySelectedNodeStyle\(\)[\s\S]*maybeFollowActiveStep\(\)[\s\S]*\{ deep: true \}\)/)
    expect(script).toMatch(/watch\(\(\) => props\.sequence[\s\S]*lastFollowedStepKey = ''/)
    expect(script).toMatch(/const changed = sourceKey !== lastFollowedStepKey[\s\S]*locateCurrentStep\(changed\)/)
    // 监控模式图例内的定位按钮 + 自动定位开关（静态预览工具栏不动）
    expect(template).toContain('class="tia-graph-canvas__locate"')
    expect(template).toContain(`:disabled="!toolbarState.canLocateActiveStep"`)
    expect(template).toContain('@click="locateCurrentStep"')
    expect(template).toContain('class="tia-graph-canvas__follow"')
    expect(template).toContain(`:class="{ 'is-on': followActiveStep }"`)
    expect(template).toContain(':aria-pressed="followActiveStep"')
    expect(template).toContain('@click="toggleFollowActiveStep"')
    // 图例容器 pointer-events:none，按钮必须单独恢复可点击
    expect(canvasSource).toMatch(/\.tia-graph-canvas__legend button \{[\s\S]*pointer-events: auto/)
  })

  it('routes plain wheel input to vertical graph movement while leaving modified wheel input to X6 zoom', () => {
    expect(script).toContain('resolveTiaGraphWheelPan')
    expect(script).toMatch(/function handleCanvasWheel\(event: WheelEvent\)/)
    expect(script).toContain('event.preventDefault()')
    expect(script).toMatch(/graph\.translate\(translation\.tx, translation\.ty - panDelta\)/)
    expect(template).toContain('@wheel="handleCanvasWheel"')
    expect(script).toMatch(/mousewheel:\s*\{[\s\S]*modifiers: \['ctrl', 'meta'\]/)
  })

  it('forwards the canvas selection event through the GraphPreviewPanel component boundary', () => {
    expect(previewScript).toMatch(/import type \{ TiaGraphSelection \} from '\.\.\/\.\.\/components\/tiaGraphSelection'/)
    expect(previewScript).toMatch(/'selectionChange': \[selection: TiaGraphSelection \| null\]/)
    expect(previewTemplate).toContain('@selection-change="emit(\'selectionChange\', $event)"')
    expect(previewTemplate).toContain('@node-click="emit(\'nodeClick\', $event)"')

    expect(() => compileScript(previewDescriptor, { id: 'task4-preview-panel' })).not.toThrow()
    const compiledTemplate = compileTemplate({
      source: previewDescriptor.template?.content ?? '',
      filename: 'GraphPreviewPanel.vue',
      id: 'task4-preview-panel',
      scoped: previewDescriptor.styles.some(item => item.scoped),
    })
    expect(compiledTemplate.errors).toEqual([])
  })

  it('passes exact runtime condition values through the preview wrapper into the selected overlay', () => {
    expect(script).toContain('runtimeValues?: LineUpConditionRuntimeValue[]')
    expect(template).toContain(':runtime-values="runtimeValues"')
    expect(previewScript).toContain('runtimeValues?: LineUpConditionRuntimeValue[]')
    expect(previewTemplate).toContain(':runtime-values="runtimeValues"')
    expect(previewTemplate).toContain('#runtime-detail="runtimeDetailProps"')
    expect(previewTemplate).toContain('<slot name="runtime-detail" v-bind="runtimeDetailProps" />')
    expect(template).toContain('#runtime-detail="runtimeDetailProps"')
    expect(template).toContain('<slot name="runtime-detail" v-bind="runtimeDetailProps" />')
  })

  it('rebuilds a saved exact selection after graph nodes and positions it after viewport restore', () => {
    expect(script).toMatch(/function restoreSelectionFromViewState\(restoreSelectedNode: boolean\)/)
    expect(script).toMatch(/const requestedSourceKey = restoreSelectedNode \? props\.viewState\?\.selectedNodeKey \?\? '' : ''/)
    expect(script).toMatch(/const restoredSelection = requestedSourceKey \? selectElement\(requestedSourceKey\) : null/)
    expect(script).toMatch(/if \(!restoredSelection\) \{[\s\S]*clearSelection\(false\)[\s\S]*emit\('selectionChange', null\)/)
    expect(script).toMatch(/selectedNodeKey = requestedSourceKey[\s\S]*selectedElement\.value = restoredSelection[\s\S]*emit\('selectionChange', restoredSelection\)/)

    const renderStart = script.indexOf('function renderGraph')
    const renderEnd = script.indexOf('onMounted(', renderStart)
    const renderBody = script.slice(renderStart, renderEnd)
    const addNodes = renderBody.indexOf('graph.addNodes')
    const restoreSelection = renderBody.indexOf('restoreSelectionFromViewState(restoreSelectedNode)')
    const scale = renderBody.indexOf('graph.scale(props.viewState.zoom)')
    const translate = renderBody.indexOf('graph.translate(props.viewState.translateX, props.viewState.translateY)')
    const scheduleOverlay = renderBody.lastIndexOf('scheduleOverlayPosition()')

    expect(addNodes).toBeGreaterThanOrEqual(0)
    expect(restoreSelection).toBeGreaterThan(addNodes)
    expect(scale).toBeGreaterThan(restoreSelection)
    expect(translate).toBeGreaterThan(scale)
    expect(scheduleOverlay).toBeGreaterThan(translate)
    expect(script).toMatch(/watch\(\(\) => props\.sequence,[\s\S]*renderGraph\(false\)/)
  })
})
