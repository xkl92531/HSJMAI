import { readFileSync } from 'node:fs'
import { describe, expect, it, vi } from 'vitest'
import { nextTick, ref } from 'vue'
import { formatTiaActiveDuration, useTiaLogicPanelTab } from './tiaLogicPanelState'

const actionTableSource = readFileSync(new URL('./TiaActionTable.vue', import.meta.url), 'utf8')
const panelSource = readFileSync(new URL('./TiaLogicNetworkPanel.vue', import.meta.url), 'utf8')
const ladNetworkSource = readFileSync(new URL('./TiaLadNetwork.vue', import.meta.url), 'utf8')
const runtimeDetailSource = readFileSync(new URL('../monitor/components/LineUpRuntimeDetail.vue', import.meta.url), 'utf8')
const faultLocationSource = readFileSync(new URL('../monitor/components/LineUpFaultLocation.vue', import.meta.url), 'utf8')

describe('tIA readonly viewer component contracts', () => {
  it('renders the four action columns with stable SourceKey rows and preserved line breaks', () => {
    expect(actionTableSource).toContain('Interlock')
    expect(actionTableSource).toContain('Event')
    expect(actionTableSource).toContain('Qualifier')
    expect(actionTableSource).toContain('Action')
    expect(actionTableSource).toContain(':key="item.sourceKey"')
    expect(actionTableSource).toContain('white-space: pre-wrap')
    expect(actionTableSource).toContain('—')
  })

  it('exposes readonly selection, runtimeValues, and activeTab props with the step tabs and transition view', () => {
    expect(panelSource).toContain('selection: TiaGraphSelection')
    expect(panelSource).toContain('runtimeValues?: LineUpConditionRuntimeValue[]')
    expect(panelSource).toContain('activeTab?: TiaLogicPanelTab')
    // 4f3d884 起页签文案走 i18n（lang.js 的 lineupShared.*），源码断言键名而非硬编码中文
    expect(panelSource).toContain('lineupShared.tabActions')
    expect(panelSource).toContain('lineupShared.tabInterlock')
    expect(panelSource).toContain('lineupShared.tabSupervision')
    expect(panelSource).toContain('TransitionCondition')
  })

  it('keeps the preview step tabs before supervision and the runtime detail tabs after it in one strip', () => {
    // 39fc742 起的页签布局：静态步页签（预览上下文，工步详情=PLC 台账）排在监督之前；
    // 运行详情五页签（运行详情/实时条件/异常定位/事件/BOM 图片）接在监督之后的同一条页签带
    // （页签文案走 i18n，按 lineupShared.* 键名断言顺序）
    expect(panelSource).toContain(`const hasRuntimeDetail = computed(() => Boolean(slots['runtime-detail']))`)
    const supervision = panelSource.indexOf('lineupShared.tabSupervision')
    let staticPrevious = -1
    for (const key of ['lineupShared.tabStepDetail', 'lineupShared.tabActions', 'lineupShared.tabInterlock']) {
      const current = panelSource.indexOf(key)
      expect(current).toBeGreaterThan(staticPrevious)
      expect(current).toBeLessThan(supervision)
      staticPrevious = current
    }
    let previous = supervision
    for (const key of ['lineupShared.tabRuntime', 'lineupShared.tabConditions', 'lineupShared.tabFaults', 'lineupShared.tabEvents', 'lineupShared.tabBomImages']) {
      const current = panelSource.indexOf(key, supervision)
      expect(current).toBeGreaterThan(previous)
      previous = current
    }
    expect(panelSource).toContain('<slot name="runtime-detail" :active-tab="runtimeDetailTab" />')
    expect(runtimeDetailSource).toContain('embedded?: boolean')
    expect(runtimeDetailSource).toContain('activeTab?: LineUpRuntimeDetailTab')
    expect(runtimeDetailSource).toMatch(/\.runtime-detail\.is-embedded[\s\S]*\.el-tabs__header[\s\S]*display: none/)
  })

  it('shows runtime exceptions directly and keeps BOM image lookup in its own tab', () => {
    const faultsStart = runtimeDetailSource.indexOf('name="faults"')
    const eventsStart = runtimeDetailSource.indexOf('name="events"', faultsStart)
    const bomStart = runtimeDetailSource.indexOf('name="bom"', eventsStart)

    expect(faultsStart).toBeGreaterThan(-1)
    expect(eventsStart).toBeGreaterThan(faultsStart)
    expect(bomStart).toBeGreaterThan(eventsStart)
    expect(runtimeDetailSource.slice(faultsStart, eventsStart)).not.toContain('<LineUpFaultLocation')
    expect(runtimeDetailSource.slice(bomStart)).toContain('<LineUpFaultLocation')
    expect(runtimeDetailSource.slice(faultsStart, eventsStart)).toContain('item.safeMessage')
    expect(runtimeDetailSource.slice(faultsStart, eventsStart)).toContain('item.severity')
    expect(runtimeDetailSource.slice(faultsStart, eventsStart)).toContain('item.occurredAtUtc')
  })

  it('automatically locates BOM images from the selected GRAPH node without manual PLC tag input', () => {
    expect(runtimeDetailSource).toContain(':sub-station-id="subStationId"')
    expect(runtimeDetailSource).toContain(':selection="selection"')
    expect(runtimeDetailSource).toContain(':conditions="conditions"')
    expect(faultLocationSource).toContain('selection?: TiaGraphSelection | null')
    expect(faultLocationSource).toContain('subStationId?: string')
    expect(faultLocationSource).toContain('plcReferencesForMonitorSelection(props.selection, props.conditions)')
    expect(faultLocationSource).toContain('imageMarkApi.getStation(subStationId)')
    expect(faultLocationSource).toContain('describeMonitorBomLocation')
    expect(faultLocationSource).not.toContain('<el-input')
    expect(faultLocationSource).not.toContain('输入完整 PLC 标签')
  })

  it('distinguishes an old revision without complete TIA detail from a real empty v2 network', () => {
    expect(panelSource).toContain('selection.detailUnavailable')
    expect(panelSource).toContain('lineupShared.detailUnavailableTip')
    expect(panelSource).toContain('lineupShared.graphUndefined')
    expect(actionTableSource).toContain('detailUnavailable?: boolean')
    expect(actionTableSource).toContain('lineupShared.detailUnavailableTip')
    expect(actionTableSource).toContain('lineupShared.noActionsDefined')
  })

  it('renders every source value as text and never enables HTML injection', () => {
    for (const source of [actionTableSource, panelSource, ladNetworkSource]) {
      expect(source).not.toContain('v-html')
    }
    expect(ladNetworkSource).toContain('{{ symbol.label }}')
    expect(ladNetworkSource).toContain(':key="symbol.key"')
    expect(ladNetworkSource).toContain(':key="wire.key"')
  })

  it('renders attached Access operands and explicit unattached fallbacks without Access function blocks', () => {
    expect(ladNetworkSource).toContain('symbol.operands')
    expect(ladNetworkSource).toContain('operand.label')
    expect(ladNetworkSource).toContain('projection.accessFallbacks')
    expect(ladNetworkSource).toContain('lineupShared.ladAccessFallback')
    expect(ladNetworkSource).not.toContain(`symbol.partName === 'Access'`)
  })

  it('shows the exact runtime value, quality, code, and source timestamp without coloring unknown as true or false', () => {
    for (const source of [ladNetworkSource, runtimeDetailSource]) {
      expect(source).toContain('qualityCode')
      expect(source).toContain('sourceTimestamp')
      expect(source).toContain('formatRuntimeValue')
    }
    expect(runtimeDetailSource).toContain('formatMonitorDuration(stepState.activeDurationMs)')
    expect(runtimeDetailSource).toContain('item.partSourceKey')
    expect(runtimeDetailSource).not.toContain('<strong>{{ item.sourceKey }}</strong>')
    expect(ladNetworkSource).toContain('symbol.runtime.state === \'unknown\'')
    expect(ladNetworkSource).toContain('operandRuntimeDetail(operand)')
    expect(ladNetworkSource).toContain('operandRuntimeDetail(access)')
    // 值状态行走 i18n（ladValueStatusMeta）；行内值短截断防越列（formatMetaValue），完整值在 title
    expect(ladNetworkSource).toContain('lineupShared.ladValueStatusMeta')
    expect(ladNetworkSource).toContain('formatMetaValue(operand.runtime.value)')
    expect(ladNetworkSource).toContain('formatRuntimeValue(access.runtime.value)')
  })
})

describe('tIA logic panel tab state', () => {
  it('switches tabs internally when activeTab is uncontrolled and resets for a new SourceKey', async () => {
    const activeTab = ref()
    const selectionSourceKey = ref('step:1')
    const update = vi.fn()
    const state = useTiaLogicPanelTab(activeTab, selectionSourceKey, update)

    expect(state.effectiveTab.value).toBe('actions')
    state.selectTab('interlock')
    expect(state.effectiveTab.value).toBe('interlock')
    expect(update).toHaveBeenLastCalledWith('interlock')

    selectionSourceKey.value = 'step:2'
    await nextTick()
    expect(state.effectiveTab.value).toBe('actions')
  })

  it('keeps the prop authoritative when activeTab is controlled and still emits updates', () => {
    const activeTab = ref<'actions' | 'interlock' | 'supervision' | undefined>('supervision')
    const selectionSourceKey = ref('step:1')
    const update = vi.fn()
    const state = useTiaLogicPanelTab(activeTab, selectionSourceKey, update)

    state.selectTab('interlock')
    expect(state.effectiveTab.value).toBe('supervision')
    expect(update).toHaveBeenLastCalledWith('interlock')

    activeTab.value = 'interlock'
    expect(state.effectiveTab.value).toBe('interlock')
  })

  it('requests one actions reset when a controlled selection SourceKey changes', async () => {
    const activeTab = ref<'actions' | 'interlock' | 'supervision' | undefined>('supervision')
    const selectionSourceKey = ref('step:1')
    const update = vi.fn()
    const state = useTiaLogicPanelTab(activeTab, selectionSourceKey, update)

    expect(update).not.toHaveBeenCalled()
    selectionSourceKey.value = 'step:2'
    await nextTick()
    expect(state.effectiveTab.value).toBe('supervision')
    expect(update).toHaveBeenCalledTimes(1)
    expect(update).toHaveBeenLastCalledWith('actions')
    expect(panelSource).toContain('value => emit(\'update:activeTab\', value)')

    selectionSourceKey.value = 'step:2'
    await nextTick()
    expect(update).toHaveBeenCalledTimes(1)

    activeTab.value = 'actions'
    selectionSourceKey.value = 'step:3'
    await nextTick()
    expect(state.effectiveTab.value).toBe('actions')
    expect(update).toHaveBeenCalledTimes(1)
  })
})

describe('tIA supervision duration', () => {
  it('formats finite non-negative milliseconds and rejects missing or invalid values', () => {
    expect(formatTiaActiveDuration(0)).toBe('0.0 s')
    expect(formatTiaActiveDuration(1500)).toBe('1.5 s')
    expect(formatTiaActiveDuration(null)).toBe('—')
    expect(formatTiaActiveDuration(undefined)).toBe('—')
    expect(formatTiaActiveDuration(-1)).toBe('—')
    expect(formatTiaActiveDuration(Number.NaN)).toBe('—')
    expect(formatTiaActiveDuration(Number.POSITIVE_INFINITY)).toBe('—')
  })

  it('exposes the original activeDurationMs decimal text and renders it without Number arithmetic', () => {
    expect(panelSource).toContain('activeDurationMs?: string | null')
    expect(panelSource).toContain('lineupShared.activeDuration')
    expect(panelSource).toContain('formatMonitorDuration(activeDurationMs)')
  })
})

describe('tIA constrained panel layout', () => {
  it('uses the available overlay height and scrolls action or network content internally', () => {
    expect(panelSource).toContain('class="panel-body"')
    expect(panelSource).toMatch(/\.tia-logic-panel\s*\{[^}]*display: flex;[^}]*flex-direction: column;[^}]*min-height: 0;/)
    expect(panelSource).toMatch(/\.panel-body\s*\{[^}]*display: flex;[^}]*flex: 1;[^}]*flex-direction: column;[^}]*min-height: 0;/)
    expect(panelSource).not.toContain('max-height: 360px')

    expect(actionTableSource).toMatch(/\.tia-action-table\s*\{[^}]*display: flex;[^}]*flex: 1;[^}]*flex-direction: column;[^}]*min-height: 0;/)
    expect(actionTableSource).not.toContain('height: 100%')
    expect(actionTableSource).toMatch(/\.action-table-scroll\s*\{[^}]*flex: 1;[^}]*min-height: 0;[^}]*overflow: auto;/)
    expect(actionTableSource).not.toContain('max-height: 260px')
  })
})
