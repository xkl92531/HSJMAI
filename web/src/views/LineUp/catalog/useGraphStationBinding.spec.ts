import type { LineUpGraphCatalogLookup } from '@/api/modules/LineUp/catalog'
import type { GraphStationBoundBlock } from '@/api/modules/PLC/graphStationBinding'
import { describe, expect, it } from 'vitest'
import { bindingStatusOf, registrationNotice, useGraphStationBinding, type GraphStationBindingGateway } from './useGraphStationBinding'

function block(blockId: string, projectId: string, bindings: GraphStationBoundBlock['bindings']): GraphStationBoundBlock {
  return {
    blockId,
    blockName: 'FB' + blockId + '_Graph',
    blockType: 'FB',
    blockNumber: Number(blockId),
    plcProjectId: projectId,
    bindings,
  }
}

const lookup: LineUpGraphCatalogLookup = {
  projects: [],
  lines: [{ id: '10', code: 'CX1', name: '产线1' }],
  processes: [{ id: '20', lineId: '10', code: 'P20', name: '上料' }],
  stations: [
    { id: '30', lineId: '10', processId: '20', code: 'OP010', name: '上料工站' },
    { id: '31', lineId: '10', processId: '20', code: 'OP020', name: '压合工站' },
  ],
  subStations: [
    { id: '40', lineId: '10', processId: '20', stationId: '30', code: 'OP010-1', name: '子工位1', plcId: '50', plcName: 'PLC1' },
    { id: '41', lineId: '10', processId: '20', stationId: '31', code: 'OP020-1', name: '子工位2', plcId: '51', plcName: 'PLC2' },
  ],
  devices: [],
}

const projects = [
  { id: '100', name: 'THEONE-PLC1', plcCode: 'PLC1', productionLineId: '10', plcDeviceId: '50', plcDeviceName: 'PLC1' },
  { id: '101', name: 'THEONE-PLC2', plcCode: 'PLC2', productionLineId: '10', plcDeviceId: '51', plcDeviceName: 'PLC2' },
  { id: '102', name: 'OTHER-PLC9', plcCode: 'PLC9', productionLineId: '10', plcDeviceId: null, plcDeviceName: 'PLC9' },
]

function fixture() {
  const calls: Array<{ name: string, value?: unknown }> = []
  let blocks: GraphStationBoundBlock[] = [
    block('900', '100', []),
    block('901', '100', [{ bindingId: '700', stationCode: 'OP010', reason: '手动绑定', confidence: 1, isShared: false, boundAt: null }]),
    block('902', '101', []),
    block('903', '101', [{ bindingId: '701', stationCode: 'OP020', reason: 'DerivedFromStationColoring+BlockCall', confidence: 0.98, isShared: false, boundAt: null }]),
    block('904', '102', [{ bindingId: '702', stationCode: 'OP010', reason: '手动绑定', confidence: 1, isShared: false, boundAt: null }]),
  ]
  const gateway: GraphStationBindingGateway = {
    async getLookups() {
      calls.push({ name: 'lookups' })
      return lookup
    },
    async getProjects() {
      calls.push({ name: 'projects' })
      return projects
    },
    async getBlocks() {
      calls.push({ name: 'blocks' })
      return [...blocks]
    },
    async bind(request) {
      calls.push({ name: 'bind', value: request })
      blocks = blocks.map(item => item.blockId === request.blockIds[0]
        ? { ...item, bindings: [{ bindingId: '800', stationCode: request.stationCode, reason: '手动绑定', confidence: 1, isShared: false, boundAt: null }] }
        : item)
      return { boundCount: request.blockIds.length, movedCount: 0, updatedBlockCount: request.blockIds.length, skippedCount: 0, skipped: [] }
    },
    async unbind(request) {
      calls.push({ name: 'unbind', value: request })
      blocks = blocks.map(item => ({ ...item, bindings: item.bindings.filter(b => !request.bindingIds.includes(b.bindingId)) }))
      return { unboundCount: request.bindingIds.length, clearedBlockCount: request.bindingIds.length }
    },
    async acceptSuggestions(request) {
      calls.push({ name: 'accept', value: request })
      blocks = blocks.map(item => ({
        ...item,
        bindings: item.bindings.map(b => request.bindingIds.includes(b.bindingId) ? { ...b, reason: '手动绑定', status: 'Manual', confidence: 1 } : b),
      }))
      return { boundCount: request.bindingIds.length, movedCount: 0, updatedBlockCount: 0, skippedCount: 0, skipped: [], registrationMessage: '已排队 GRAPH 步位重新注册：THEONE-PLC2' }
    },
    async rejectSuggestions(request) {
      calls.push({ name: 'reject', value: request })
      blocks = blocks.map(item => ({
        ...item,
        bindings: item.bindings.filter(b => !request.bindingIds.includes(b.bindingId)),
        rejectedStations: item.bindings.filter(b => request.bindingIds.includes(b.bindingId)).map(b => b.stationCode),
      }))
      return { unboundCount: request.bindingIds.length, rejectedCount: request.bindingIds.length, restoredBlockCount: 0, clearedBlockCount: 1, registrationMessage: '' }
    },
    async rerunRegistration(projectId) {
      calls.push({ name: 'rerun', value: projectId })
      return { isSuccess: true }
    },
    async listMonitorBindings(query) {
      calls.push({ name: 'monitorList', value: query })
      return [
        { bindingId: '600', graphSourceName: 'FB900_Graph', plcProjectId: '100', monitoringEnabled: false, bindingStatus: 'Resolved' },
        { bindingId: '601', graphSourceName: 'FB901_Graph', plcProjectId: '100', monitoringEnabled: true, bindingStatus: 'Resolved' },
      ]
    },
    async enableMonitoring(bindingId) {
      calls.push({ name: 'enableMonitoring', value: bindingId })
    },
  }
  return { calls, gateway, setBlocks: (value: GraphStationBoundBlock[]) => (blocks = value) }
}

function findNode(nodes: ReturnType<typeof useGraphStationBinding>['tree']['value'], key: string): any {
  for (const node of nodes) {
    if (node.key === key) {
      return node
    }
    const child = findNode(node.children, key)
    if (child) {
      return child
    }
  }
  return null
}

describe('useGraphStationBinding', () => {
  it('builds line → process → station → sub-station tree with bound GRAPH block leaves', async () => {
    const value = fixture()
    const workspace = useGraphStationBinding(value.gateway)
    await workspace.initialize()

    const station = findNode(workspace.tree.value, 'station:30')
    expect(station).not.toBeNull()
    expect(station.stationCode).toBe('OP010')
    // 子工站节点 + 项目不匹配任何子工站 PLC 的绑定块（FB904/PLC9）直接挂工站下
    expect(station.children.map((c: any) => c.kind)).toEqual(['substation', 'block'])
    expect(station.children[1].blockName).toBe('FB904_Graph')
    expect(station.children[1].bindingId).toBe('702')

    const sub = station.children[0]
    expect(sub.label).toBe('OP010-1 子工位1')
    expect(sub.plcCode).toBe('PLC1')
    expect(sub.projectId).toBe('100')
    expect(sub.stationCode).toBe('OP010')
    // 已绑定块挂为子工站叶子：块项目匹配子工站 PLC（FB901/PLC1）
    expect(sub.children.map((c: any) => c.kind)).toEqual(['block'])
    expect(sub.children[0].blockName).toBe('FB901_Graph')
    expect(sub.children[0].bindingId).toBe('700')
    expect(sub.children[0].plcCode).toBe('PLC1')
    // 徽标计数：子工站按项目计（FB901）+ 工站下 PLC 不匹配的绑定（FB904）
    expect(sub.count).toBe(1)
    expect(station.count).toBe(2)
  })

  it('filters unbound blocks by selected device project and station line scope', async () => {
    const value = fixture()
    const workspace = useGraphStationBinding(value.gateway)
    await workspace.initialize()
    // 默认选中首条产线：右侧 = 该产线全部未绑定块（FB900 / FB902）
    expect(workspace.selectedKey.value).toBe('line:10')
    expect(workspace.unboundBlocks.value.map(b => b.blockName).sort()).toEqual(['FB900_Graph', 'FB902_Graph'])

    // 选中 OP010 的 PLC1 子工站节点：右侧仅剩该项目的未绑定块
    workspace.selectNode('station:30:sub:40')
    expect(workspace.scope.value.stationCode).toBe('OP010')
    expect(workspace.scope.value.projectId).toBe('100')
    expect(workspace.unboundBlocks.value.map(b => b.blockName)).toEqual(['FB900_Graph'])

    // 关键词过滤
    workspace.keyword.value = '902'
    expect(workspace.unboundBlocks.value).toHaveLength(0)
    workspace.keyword.value = ''
  })

  it('binds selected blocks to the scoped station and refreshes blocks', async () => {
    const value = fixture()
    const workspace = useGraphStationBinding(value.gateway)
    await workspace.initialize()
    workspace.selectNode('station:30:sub:40')
    workspace.setSelection(['900'])

    expect(workspace.canBind.value).toBe(true)
    const result = await workspace.bindSelected()

    expect(result.boundCount).toBe(1)
    expect(value.calls.filter(c => c.name === 'bind').pop()?.value).toEqual({ stationCode: 'OP010', blockIds: ['900'] })
    // 绑定后刷新：FB900 已绑定 → 回到右侧列表消失
    expect(workspace.unboundBlocks.value.map(b => b.blockName)).toEqual([])
    expect(workspace.selectedBlockIds.value).toEqual([])
  })

  it('unbinds by binding id and the block returns to the unbound list', async () => {
    const value = fixture()
    const workspace = useGraphStationBinding(value.gateway)
    await workspace.initialize()
    workspace.selectNode('station:30:sub:40')
    expect(workspace.unboundBlocks.value.map(b => b.blockName)).toEqual(['FB900_Graph'])

    const result = await workspace.unbind(['700'])
    expect(result.unboundCount).toBe(1)
    expect(value.calls.filter(c => c.name === 'unbind').pop()?.value).toEqual({ bindingIds: ['700'] })
    // FB901 解绑后回到未绑定区
    expect(workspace.unboundBlocks.value.map(b => b.blockName).sort()).toEqual(['FB900_Graph', 'FB901_Graph'])
  })

  it('locates the sub-station node from initial projectId, or falls back to focus filtering', async () => {
    const value = fixture()
    const workspace = useGraphStationBinding(value.gateway)
    await workspace.initialize({ projectId: '100' })
    expect(workspace.selectedKey.value).toBe('station:30:sub:40')
    // 定位成功后设备过滤仍保持（树按该 PLC 过滤）
    expect(workspace.focusProjectId.value).toBe('100')

    const value2 = fixture()
    const workspace2 = useGraphStationBinding(value2.gateway)
    await workspace2.initialize({ projectId: '999' })
    // 无法定位设备节点时：树按 999 聚焦裁剪后只剩 placeholder（无产线可兜底选中），焦点项目继续兜底过滤右侧块
    expect(workspace2.selectedKey.value).toBe('')
    expect(workspace2.focusProjectId.value).toBe('999')
    expect(workspace2.scope.value.projectId).toBe('999')
  })

  it('filters sub-stations to the focused project PLC device in the tree', async () => {
    // 聚焦项目 100（PLC1）时：OP010 工站保留 PLC1 子工站，OP020 工站的 PLC2 子工站被过滤
    const value = fixture()
    const workspace = useGraphStationBinding(value.gateway)
    await workspace.initialize({ projectId: '100' })
    const station30 = workspace.tree.value[0]?.children?.[0]?.children?.find(n => n.key === 'station:30')
    const station31 = workspace.tree.value[0]?.children?.[0]?.children?.find(n => n.key === 'station:31')
    expect(station30?.children?.map(n => n.kind)).toEqual(['substation'])
    expect(station30?.children?.[0]?.plcCode).toBe('PLC1')
    expect(station31?.children ?? []).toEqual([])
    // 聚焦 PLC1 时：PLC9 项目（102）绑到 OP010 的块 904 不再兜底挂工站（上方 toEqual 已覆盖：工站层只剩 substation）

    // 无聚焦（initialize 不带 projectId）：两台 PLC 的子工站都显示；PLC9 的绑定块兜底挂工站（不丢）
    const value2 = fixture()
    const workspace2 = useGraphStationBinding(value2.gateway)
    await workspace2.initialize()
    const st30 = workspace2.tree.value[0]?.children?.[0]?.children?.find(n => n.key === 'station:30')
    const st31 = workspace2.tree.value[0]?.children?.[0]?.children?.find(n => n.key === 'station:31')
    expect(st30?.children?.map(n => n.kind)).toEqual(['substation', 'block'])
    expect(st30?.children?.[0]?.plcCode).toBe('PLC1')
    expect(st30?.children?.[1]?.blockName).toBe('FB904_Graph')
    expect(st31?.children?.map(n => n.kind)).toEqual(['substation'])
  })

  it('prunes processes/stations of other PLC devices when focused', async () => {
    // 聚焦 PLC2（项目 101）：OP010 工站（PLC1 子工站 + PLC9 兜底块）整条裁掉，只剩 OP020 链路
    const value = fixture()
    const workspace = useGraphStationBinding(value.gateway)
    await workspace.initialize({ projectId: '101' })
    const line = workspace.tree.value[0]
    expect(line?.kind).toBe('line')
    const proc = line?.children?.[0]
    expect(proc?.children?.map(n => n.key)).toEqual(['station:31'])
    expect(proc?.children?.[0]?.children?.map(n => n.kind)).toEqual(['substation'])

    // 聚焦无任何匹配的项目（PLC9=102）：整棵树只剩 placeholder 提示
    const value2 = fixture()
    const workspace2 = useGraphStationBinding(value2.gateway)
    await workspace2.initialize({ projectId: '102' })
    // 102 的块 904 绑 OP010 → OP010 保留（兜底块），PLC1/PLC2 子工站裁掉
    const line2 = workspace2.tree.value[0]
    expect(line2?.children?.[0]?.children?.map(n => n.key)).toEqual(['station:30'])
    expect(line2?.children?.[0]?.children?.[0]?.children?.map(n => n.kind)).toEqual(['block'])
  })

  it('tells manual bindings from automatic suggestions', () => {
    expect(bindingStatusOf({ status: 'Manual', reason: '自动建议:名称含工位号' })).toBe('Manual')
    expect(bindingStatusOf({ status: 'Suggested', reason: '手动绑定' })).toBe('Suggested')
    // 旧后端没有 status 字段时按 reason 认：只有「手动绑定」是人工的，其余都是自动建议
    expect(bindingStatusOf({ reason: '手动绑定' })).toBe('Manual')
    expect(bindingStatusOf({ reason: 'DerivedFromStationColoring+BlockCall' })).toBe('Suggested')
    expect(bindingStatusOf({ reason: null })).toBe('Suggested')
  })

  it('marks block leaves in the tree as manual or automatic', async () => {
    const value = fixture()
    const workspace = useGraphStationBinding(value.gateway)
    await workspace.initialize()
    const manual = findNode(workspace.tree.value, 'binding:700')
    expect(manual.bindingStatus).toBe('Manual')
    const suggested = findNode(workspace.tree.value, 'binding:701')
    expect(suggested.bindingStatus).toBe('Suggested')
    expect(suggested.confidence).toBe(0.98)
    expect(suggested.reason).toBe('DerivedFromStationColoring+BlockCall')
  })

  it('accepts a suggestion as a manual binding and reloads blocks', async () => {
    const value = fixture()
    const workspace = useGraphStationBinding(value.gateway)
    await workspace.initialize()
    const result = await workspace.acceptSuggestions(['701'])
    expect(value.calls.filter(c => c.name === 'accept').pop()?.value).toEqual({ bindingIds: ['701'] })
    expect(registrationNotice(result)).toBe('已排队 GRAPH 步位重新注册：THEONE-PLC2')
    expect(findNode(workspace.tree.value, 'binding:701').bindingStatus).toBe('Manual')
  })

  it('rejects a suggestion and the block returns to the unbound list', async () => {
    const value = fixture()
    const workspace = useGraphStationBinding(value.gateway)
    await workspace.initialize()
    workspace.selectNode('station:31:sub:41')
    expect(workspace.unboundBlocks.value.map(b => b.blockName)).toEqual(['FB902_Graph'])
    const result = await workspace.rejectSuggestions(['701'])
    expect(value.calls.filter(c => c.name === 'reject').pop()?.value).toEqual({ bindingIds: ['701'] })
    expect(result.rejectedCount).toBe(1)
    expect(findNode(workspace.tree.value, 'binding:701')).toBeNull()
    expect(workspace.unboundBlocks.value.map(b => b.blockName).sort()).toEqual(['FB902_Graph', 'FB903_Graph'])
  })

  it('refuses to accept or reject without binding ids', async () => {
    const value = fixture()
    const workspace = useGraphStationBinding(value.gateway)
    await workspace.initialize()
    await expect(workspace.acceptSuggestions([])).rejects.toThrow('BindingTargetMissing')
    await expect(workspace.rejectSuggestions([])).rejects.toThrow('BindingTargetMissing')
    expect(value.calls.some(c => c.name === 'accept' || c.name === 'reject')).toBe(false)
  })

  it('reads the re-registration notice from bind results', () => {
    expect(registrationNotice({ registrationMessage: '  已排队 GRAPH 步位重新注册：PLC1  ' })).toBe('已排队 GRAPH 步位重新注册：PLC1')
    expect(registrationNotice({ registrationMessage: '' })).toBe('')
    expect(registrationNotice({ registrationMessage: null })).toBe('')
    expect(registrationNotice({})).toBe('')
    expect(registrationNotice(null)).toBe('')
  })

  it('reruns graph step registration against the scoped project', async () => {
    const value = fixture()
    const workspace = useGraphStationBinding(value.gateway)
    await workspace.initialize()
    workspace.selectNode('station:31:sub:41')
    await workspace.rerunRegistration()
    expect(value.calls.filter(c => c.name === 'rerun').pop()?.value).toBe('101')
  })
})
