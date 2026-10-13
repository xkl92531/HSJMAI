import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

const source = readFileSync(new URL('./index.vue', import.meta.url), 'utf8')
const detailSource = readFileSync(new URL('./components/LineUpRuntimeDetail.vue', import.meta.url), 'utf8')
const routesSource = readFileSync(new URL('../../../router/routes.ts', import.meta.url), 'utf8')
const guardsSource = readFileSync(new URL('../../../router/guards.ts', import.meta.url), 'utf8')

describe('lineUp monitor page contract', () => {
  it('is a read-only work-position tree workspace with a separate binding-settings link', () => {
    expect(source).toContain('<LineUpMonitorTree')
    expect(source).toContain('path: \'/lineup/bindings\'')
    expect(source).not.toContain('生成绑定')
    expect(source).not.toContain('在线验证')
    expect(source).not.toContain('启用监控')
    expect(source).not.toContain('停用监控')
    expect(source).not.toContain('runBindingAction')
  })

  it('integrates exact runtime detail into the GRAPH node panel instead of reserving a separate sidebar', () => {
    expect(source).toContain('<GraphPreviewPanel')
    expect(source).toContain('<LineUpRuntimeDetail')
    expect(source).toContain('<template #runtime-detail="{ activeTab }">')
    expect(source).toContain('@selection-change="selectGraphElement"')
    expect(source).toContain(':runtime-values="selectedConditions"')
    expect(source).toContain(':selection="selectedGraphElement"')
    expect(source).toContain(':conditions="selectedConditions"')
    expect(source).toContain(':sub-station-id="workspace.selectedSubStationId.value || \'\'"')
    expect(source).toContain(':snapshot="matchingSnapshot"')
    expect(source).toContain(':active-tab="activeTab"')
    expect(source).toContain('embedded')
    expect(source).not.toContain('<aside class="monitor-detail"')
    expect(source).not.toContain('detailCollapsed')
    expect(source).not.toContain('detailWidth')
    expect(source).not.toContain('startResize')
  })

  it('wires step BOM images from the selected work position and node symbols', () => {
    expect(source).toContain(':bom-sub-station-id="workspace.selectedSubStationId.value || \'\'"')
    expect(source).toContain(':bom-references="bomReferences"')
    expect(source).toContain('symbolNamesForSelection(withAdjacentTransitionNetworks(')
  })

  it('uses a tree canvas detail grid without global overflow and a narrow drawer tree', () => {
    expect(source).toContain('grid-template-columns: auto minmax(0, 1fr)')
    expect(source).toContain('overflow: hidden')
    expect(source).toContain('<el-drawer')
    expect(source).toContain('@media (width <= 900px)')
  })

  it('switches sequences via panel tabs with runtime status dots and graphs via the tree', () => {
    expect(source).toContain(':sequence-statuses="sequenceStatuses"')
    expect(source).toContain('@update:model-value="selectSequence"')
    expect(source).not.toContain(':show-sequence-tabs="false"')
    expect(source).toContain('@select-graph="selectGraph"')
    expect(source).toContain(':selected-binding-id="selectedBinding?.bindingId || \'\'"')
    expect(source).not.toContain('class="monitor-toolbar"')
    expect(source).not.toContain('class="monitor-selection"')
    expect(source).not.toContain('class="monitor-status"')
  })

  it('clears graph selection on work-position, GRAPH and Sequence changes', () => {
    expect(source).toMatch(/function clearGraphSelection\(\)[\s\S]*selectedGraphElement\.value = null/)
    expect(source).toMatch(/async function selectSubStation[\s\S]*clearGraphSelection\(\)/)
    expect(source).toMatch(/async function selectGraph[\s\S]*clearGraphSelection\(\)/)
    expect(source).toMatch(/async function selectSequence[\s\S]*clearGraphSelection\(\)/)
  })

  it('keeps exact event ownership in runtime detail', () => {
    expect(detailSource).toContain('selection?: TiaGraphSelection | null')
    expect(detailSource).toContain('conditions?: LineUpConditionRuntimeValue[]')
    expect(detailSource).toContain('eventsForMonitorSelection(props.events, props.selection)')
    expect(detailSource).not.toContain('snapshot.conditions')
  })
})

describe('lineUp monitor embed contract (external systems)', () => {
  it('registers a layout-free embed route reusing the monitor component', () => {
    expect(routesSource).toContain("path: '/lineup/monitor-embed'")
    expect(routesSource).toContain("name: 'LineUpMonitorEmbed'")
    expect(routesSource).toContain("import('@/views/LineUp/monitor/index.vue')")
    // 顶层 constantRoutes（不挂在 Layout 子路由下）才具备免菜单能力
    const routeBlock = routesSource.slice(
      routesSource.indexOf("path: '/lineup/monitor-embed'"),
      routesSource.indexOf('const systemRoutes'),
    )
    expect(routeBlock).not.toContain('layouts/index.vue')
  })

  it('injects login state from ?token= on the embed route only and strips it from the URL', () => {
    expect(guardsSource).toContain("to.path === '/lineup/monitor-embed'")
    expect(guardsSource).toContain("localStorage.setItem('token', to.query.token)")
    expect(guardsSource).toContain('token: undefined')
  })

  it('hides in-app navigation actions and expands to full viewport height in embed mode', () => {
    expect(source).toContain('isEmbeddedRoute')
    expect(source).toContain('<el-button v-if="!isEmbeddedRoute" @click="router.push(historyLink())">')
    expect(source).toContain('<el-button v-if="!isEmbeddedRoute" @click="router.push({ path: \'/lineup/bindings\'')
    expect(source).toContain('<el-button v-if="!isEmbeddedRoute" :disabled="!workspace.selectedSubStationId.value" @click="openImageMarks">')
    expect(source).toContain('.lineup-monitor-shell.is-embedded')
    // 嵌入页直达参数无法还原时不得跳回带布局的 /lineup/monitor
    expect(source).toContain('await router.replace({ path: route.path })')
    expect(source).not.toContain("router.replace({ path: '/lineup/monitor' })")
  })
})
