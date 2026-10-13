import type {
  LineUpGraphCatalogAsset,
  LineUpGraphCatalogPage,
  LineUpGraphPublishPreview,
} from '@/api/modules/LineUp/catalog'
import type {
  LineUpMonitorBindingSummary,
  LineUpMonitorNavigationNode,
} from '@/api/modules/LineUp/monitor'
import { describe, expect, it, vi } from 'vitest'
import { createLineUpMonitorWorkspace } from './useLineUpMonitorWorkspace'

describe('createLineUpMonitorWorkspace', () => {
  it('restores an exact route leaf, binding and Sequence', async () => {
    const fixture = createFixture(true)
    const workspace = createLineUpMonitorWorkspace(fixture.monitor, fixture.catalog, fixture.live)

    const restored = await workspace.initializeRoute({
      subStationId: '3001',
      bindingId: '5001',
      sequenceSourceKey: 'sequence:2',
    })

    expect(restored).toBe(true)
    expect(fixture.monitor.requestedSubStations).toEqual(['3001'])
    expect(workspace.selectedBinding.value?.bindingId).toBe('5001')
    expect(workspace.selectedSequenceKey.value).toBe('sequence:2')
  })

  it('drops the page overlay as soon as the graph is loaded, while the live connection is still pending', async () => {
    const fixture = createFixture(true)
    let openGate!: () => void
    fixture.live.connectGate = new Promise<void>((resolve) => {
      openGate = resolve
    })
    const workspace = createLineUpMonitorWorkspace(fixture.monitor, fixture.catalog, fixture.live)
    await workspace.refreshNavigation()

    const selecting = workspace.selectSubStation('3001')
    await vi.waitFor(() => expect(fixture.live.calls).toContain('connect:5001:sequence:1'))

    expect(workspace.preview.value).not.toBeNull()
    expect(workspace.contentLoading.value).toBe(false)

    openGate()
    await selecting
    expect(workspace.contentLoading.value).toBe(false)
  })

  it('clears invalid route selection and never loads a parent tree node', async () => {
    const fixture = createFixture(true)
    const workspace = createLineUpMonitorWorkspace(fixture.monitor, fixture.catalog, fixture.live)

    expect(await workspace.initializeRoute({ subStationId: '10' })).toBe(false)
    expect(workspace.selectedSubStationId.value).toBeNull()
    expect(workspace.preview.value).toBeNull()
    expect(fixture.monitor.requestedSubStations).toEqual([])
  })

  it('ignores late A responses after selecting B and disconnects before the new load', async () => {
    const fixture = createFixture(false)
    const workspace = createLineUpMonitorWorkspace(fixture.monitor, fixture.catalog, fixture.live)
    await workspace.refreshNavigation()
    const first = workspace.selectSubStation('3001')
    const second = workspace.selectSubStation('3002')

    fixture.resolveSelection('3001', '5001')
    await first
    expect(workspace.bindings.value).toEqual([])
    fixture.resolveSelection('3002', '5002')
    await second

    expect(workspace.selectedSubStationId.value).toBe('3002')
    expect(workspace.selectedBinding.value?.bindingId).toBe('5002')
    expect(fixture.live.calls.slice(0, 2)).toEqual(['disconnect', 'disconnect'])
  })

  it('maps a legacy line-device-station route only when assigned work position is unique', async () => {
    const fixture = createFixture(true)
    fixture.monitor.legacyBindings = [binding('5001', '3001')]
    const workspace = createLineUpMonitorWorkspace(fixture.monitor, fixture.catalog, fixture.live)

    expect(await workspace.initializeRoute({ lineId: '10', deviceId: '40', stationId: '30' })).toBe(true)
    expect(workspace.selectedSubStationId.value).toBe('3001')

    fixture.monitor.legacyBindings = [binding('5001', '3001'), binding('5002', '3002')]
    expect(await workspace.initializeRoute({ lineId: '10', deviceId: '40', stationId: '30' })).toBe(false)
  })

  it('locates a sub-station by code with default binding and first sequence', async () => {
    const fixture = createFixture(true)
    fixture.monitor.navNodes = codeNavigation()
    const workspace = createLineUpMonitorWorkspace(fixture.monitor, fixture.catalog, fixture.live)

    const restored = await workspace.initializeRoute({ subStationCode: 'op030', lineCode: 'cx1' })

    expect(restored).toBe(true)
    expect(workspace.selectedSubStationId.value).toBe('3001')
    expect(fixture.monitor.requestedSubStations).toEqual(['3001'])
    // 未传 bindingId / sequenceSourceKey 时默认取第一个已发布绑定和第一个序列
    expect(workspace.selectedBinding.value?.bindingId).toBe('5001')
    expect(workspace.selectedSequenceKey.value).toBe('sequence:1')
  })

  it('disambiguates duplicate sub-station codes across lines with lineCode', async () => {
    const fixture = createFixture(true)
    fixture.monitor.navNodes = codeNavigation()
    const workspace = createLineUpMonitorWorkspace(fixture.monitor, fixture.catalog, fixture.live)

    await workspace.initializeRoute({ subStationCode: 'OP030', lineCode: 'CX2' })
    expect(workspace.selectedSubStationId.value).toBe('3002')

    // 不带 lineCode 时取导航顺序第一个命中
    await workspace.initializeRoute({ subStationCode: 'OP030' })
    expect(workspace.selectedSubStationId.value).toBe('3001')
  })

  it('clears selection when sub-station code matches nothing', async () => {
    const fixture = createFixture(true)
    fixture.monitor.navNodes = codeNavigation()
    const workspace = createLineUpMonitorWorkspace(fixture.monitor, fixture.catalog, fixture.live)

    expect(await workspace.initializeRoute({ subStationCode: 'OP999', lineCode: 'CX1' })).toBe(false)
    expect(workspace.selectedSubStationId.value).toBeNull()
    expect(fixture.monitor.requestedSubStations).toEqual([])
  })

  // 2026-09-21：不再先断后连。connect() 内部本来就会退掉旧订阅，
  // 外面再断一次纯属白等一轮往返（而且以前连带把 WebSocket 也拆了重建）。
  it('switches GRAPH or Sequence without a redundant disconnect round trip', async () => {
    const fixture = createFixture(true)
    const workspace = createLineUpMonitorWorkspace(fixture.monitor, fixture.catalog, fixture.live)
    await workspace.initializeRoute({ subStationId: '3001', bindingId: '5001' })
    fixture.live.calls.length = 0

    await workspace.selectSequence('sequence:2')

    expect(fixture.live.calls).not.toContain('disconnect')
    expect(fixture.live.calls.at(-1)).toContain('connect:5001:sequence:2')
  })

  // 预热必须抢在 previewPublish / getDefinition 前面发出去，那一次 PLC 读才能与它们并行跑掉；
  // 拖到 Subscribe 再读就是用户干等着的冷读，也就是进页面那股顿挫。
  it('warms the whole station before it starts fetching the graph', async () => {
    const fixture = createFixture(true)
    const workspace = createLineUpMonitorWorkspace(fixture.monitor, fixture.catalog, fixture.live)

    await workspace.initializeRoute({ subStationId: '3001', bindingId: '5001' })

    const warmIndex = fixture.live.calls.findIndex(call => call.startsWith('warm:'))
    const connectIndex = fixture.live.calls.findIndex(call => call.startsWith('connect:'))
    expect(warmIndex).toBeGreaterThanOrEqual(0)
    expect(fixture.live.calls[warmIndex]).toContain('5001')
    expect(warmIndex).toBeLessThan(connectIndex)
  })

  it('closes the connection for real only when the page goes away', async () => {
    const fixture = createFixture(true)
    const workspace = createLineUpMonitorWorkspace(fixture.monitor, fixture.catalog, fixture.live)
    await workspace.initializeRoute({ subStationId: '3001', bindingId: '5001' })

    await workspace.dispose()

    expect(fixture.live.calls.at(-1)).toBe('dispose')
  })

  // 2026-10-09 检查清单 #9：旧选择的迟到失败不能断开当前已连接的监控。
  // 已在 A、点了 B 再切回 A（或切到 C）后，B 的 previewPublish 才失败——断开副作用必须受
  // 当前选择身份守卫，否则 A/C 的实时连接被清掉、选中项还在、页面也没有错误。
  it('a late previewPublish failure does not tear down the newer selection connection', async () => {
    const fixture = createFixture(true)
    fixture.monitor.bindingsOverride = subStationId => subStationId === '3001'
      ? [binding('5001', '3001'), binding('5002', '3001')]
      : [binding('5001', subStationId)]
    fixture.catalog.getAssetsOverride = subStationId => page([
      asset('5001', subStationId),
      asset('5002', subStationId),
    ])
    let reject5002!: (reason?: unknown) => void
    const late5002 = new Promise<LineUpGraphPublishPreview>((_, reject) => {
      reject5002 = reject
    })
    fixture.catalog.previewPublishImpl = (bindingId) =>
      bindingId === '5002' ? late5002 : Promise.resolve(defaultPreview(bindingId))
    const workspace = createLineUpMonitorWorkspace(fixture.monitor, fixture.catalog, fixture.live)
    await workspace.initializeRoute({ subStationId: '3001', bindingId: '5001' })

    const selecting5002 = workspace.selectBinding('5002')
    const backTo5001 = workspace.selectBinding('5001')
    await backTo5001
    const disconnectsBefore = fixture.live.calls.filter(call => call === 'disconnect').length
    expect(workspace.selectedBinding.value?.bindingId).toBe('5001')
    expect(fixture.live.calls.some(call => call.startsWith('connect:5001'))).toBe(true)

    reject5002(new Error('late preview failure'))
    await selecting5002

    expect(fixture.live.calls.filter(call => call === 'disconnect').length).toBe(disconnectsBefore)
    expect(workspace.selectedBinding.value?.bindingId).toBe('5001')
    expect(workspace.safeErrorCode.value).toBe('')
  })

  // 2026-10-09 检查清单 #11：清空选择要撤掉预热（服务端预热集合独立于订阅），
  // 否则页面已无选中工位、旧工位仍持续轮询到下一次成功预热或连接关闭。
  it('clears the warm set when selection is cleared', async () => {
    const fixture = createFixture(true)
    const workspace = createLineUpMonitorWorkspace(fixture.monitor, fixture.catalog, fixture.live)
    await workspace.initializeRoute({ subStationId: '3001', bindingId: '5001' })
    expect(fixture.live.calls.some(call => call === 'warm:5001')).toBe(true)

    await workspace.clearSelection()

    expect(workspace.selectedSubStationId.value).toBeNull()
    expect(fixture.live.calls.at(-2)).toBe('warm:')
    expect(fixture.live.calls.at(-1)).toBe('disconnect')
  })
})

function createFixture(immediate: boolean) {
  const monitor = new FakeMonitorGateway(immediate)
  const catalog = new FakeCatalogGateway(immediate)
  const live = new FakeLiveMonitor()
  return {
    monitor,
    catalog,
    live,
    resolveSelection(subStationId: string, bindingId: string) {
      monitor.requests.get(subStationId)?.resolve([binding(bindingId, subStationId)])
      catalog.requests.get(subStationId)?.resolve(page(asset(bindingId, subStationId)))
    },
  }
}

class FakeMonitorGateway {
  readonly requests = new Map<string, Deferred<LineUpMonitorBindingSummary[]>>()
  readonly requestedSubStations: string[] = []
  legacyBindings: LineUpMonitorBindingSummary[] = []
  navNodes: LineUpMonitorNavigationNode[] = navigation()
  bindingsOverride: ((subStationId: string) => LineUpMonitorBindingSummary[]) | null = null

  constructor(private readonly immediate: boolean) {}

  async getNavigation(): Promise<LineUpMonitorNavigationNode[]> {
    return this.navNodes
  }

  async getBindingsBySubStation(subStationId: string) {
    this.requestedSubStations.push(subStationId)
    if (this.bindingsOverride) {
      return this.bindingsOverride(subStationId)
    }
    if (this.immediate) {
      return [binding(subStationId === '3001' ? '5001' : '5002', subStationId)]
    }
    const request = deferred<LineUpMonitorBindingSummary[]>()
    this.requests.set(subStationId, request)
    return request.promise
  }

  async getBindings() {
    return this.legacyBindings
  }
}

class FakeCatalogGateway {
  readonly requests = new Map<string, Deferred<LineUpGraphCatalogPage>>()
  getAssetsOverride: ((subStationId: string) => LineUpGraphCatalogPage) | null = null
  previewPublishImpl: ((bindingId: string) => Promise<LineUpGraphPublishPreview>) | null = null

  constructor(private readonly immediate: boolean) {}

  async getAssets(query: { subStationId?: string }) {
    const subStationId = query.subStationId ?? ''
    if (this.getAssetsOverride) {
      return this.getAssetsOverride(subStationId)
    }
    if (this.immediate) {
      return page(asset(subStationId === '3001' ? '5001' : '5002', subStationId))
    }
    const request = deferred<LineUpGraphCatalogPage>()
    this.requests.set(subStationId, request)
    return request.promise
  }

  async previewPublish(bindingId: string): Promise<LineUpGraphPublishPreview> {
    if (this.previewPublishImpl) {
      return this.previewPublishImpl(bindingId)
    }
    return defaultPreview(bindingId)
  }
}

class FakeLiveMonitor {
  readonly calls: string[] = []

  async disconnectAndClear() {
    this.calls.push('disconnect')
  }

  /** 设置后 connect 会挂起到它 resolve，用来观察连接进行中的页面状态。 */
  connectGate: Promise<void> | null = null

  async connect(identity: { bindingId: string, sequenceSourceKey: string }) {
    this.calls.push(`connect:${identity.bindingId}:${identity.sequenceSourceKey}`)
    await this.connectGate
  }

  async warm(bindingIds: readonly string[]) {
    this.calls.push(`warm:${bindingIds.join(',')}`)
  }

  async dispose() {
    this.calls.push('dispose')
  }
}

function navigation(): LineUpMonitorNavigationNode[] {
  return [
    nav('line:10', 'Line', null, '10'),
    nav('process:20', 'Process', 'line:10', '20'),
    nav('station:30', 'Station', 'process:20', '30'),
    nav('substation:3001', 'SubStation', 'station:30', '3001'),
    nav('substation:3002', 'SubStation', 'station:30', '3002'),
  ]
}

// 编码定位用例导航：两条产线（CX1/CX2）各有一个同编码子工位 OP030
function codeNavigation(): LineUpMonitorNavigationNode[] {
  return [
    nav('line:L1', 'Line', null, '10', 'CX1'),
    nav('process:P1', 'Process', 'line:L1', '20', 'OP010'),
    nav('station:S1', 'Station', 'process:P1', '30', 'OP010'),
    nav('substation:3001', 'SubStation', 'station:S1', '3001', 'OP030'),
    nav('line:L2', 'Line', null, '11', 'CX2'),
    nav('process:P2', 'Process', 'line:L2', '21', 'OP010'),
    nav('station:S2', 'Station', 'process:P2', '31', 'OP010'),
    nav('substation:3002', 'SubStation', 'station:S2', '3002', 'OP030'),
  ]
}

function nav(nodeId: string, nodeType: LineUpMonitorNavigationNode['nodeType'], parentNodeId: string | null, entityId: string, code = nodeId): LineUpMonitorNavigationNode {
  return { nodeId, nodeType, parentNodeId, entityId, code, name: nodeId, sortOrder: 0, descendantCount: 0, issueCount: 0, runtimeStatus: 'Ready', publishedBindingCount: 1 }
}

function binding(bindingId: string, subStationId: string): LineUpMonitorBindingSummary {
  return {
    bindingId,
    graphAssetId: `asset:${bindingId}`,
    publishedRevisionId: '6001',
    lineId: '10',
    deviceId: '40',
    processId: '20',
    stationId: '30',
    subStationId,
    plcProjectId: '4612671841813794816',
    graphSourceName: `FB_${bindingId}`,
    bindingStatus: 'Resolved',
    monitoringEnabled: true,
    runtimeBindingReady: true,
    sequences: [
      { sequenceSourceKey: 'sequence:1', sequenceDisplayName: '序列1', stepCount: 2, transitionCount: 1, runtimeStatus: '' },
      { sequenceSourceKey: 'sequence:2', sequenceDisplayName: '序列2', stepCount: 2, transitionCount: 1, runtimeStatus: '' },
    ],
  }
}

function asset(bindingId: string, subStationId: string): LineUpGraphCatalogAsset {
  return {
    assetId: `asset:${bindingId}`,
    bindingId,
    subStationId,
    publishedRevisionId: '6001',
    sourceBlockName: `FB_${bindingId}`,
    publishedRevisionNo: 1,
  } as LineUpGraphCatalogAsset
}

function page(item: LineUpGraphCatalogAsset | LineUpGraphCatalogAsset[]): LineUpGraphCatalogPage {
  const items = Array.isArray(item) ? item : [item]
  return { items, total: items.length, page: 1, pageSize: 100 }
}

function defaultPreview(bindingId: string): LineUpGraphPublishPreview {
  return {
    bindingId,
    targetRevisionId: '6001',
    targetRevisionNo: 1,
    publishedRevisionId: '6001',
    publishedRevisionNo: 1,
    diff: {
      isInitialPublish: false,
      added: counts(),
      removed: counts(),
      changed: counts(),
      items: [],
    },
    preview: {
      viewerSchemaVersion: 2,
      viewerCapabilities: ['GraphTopology'],
      viewerWarnings: [],
      blockName: 'FB260_CacheGraph',
      blockNumber: 260,
      programmingLanguage: 'GRAPH',
      sequences: [sequence('sequence:1'), sequence('sequence:2')],
    },
  }
}

function sequence(sourceKey: string) {
  return {
    viewerSchemaVersion: 2,
    viewerCapabilities: ['GraphTopology'],
    viewerWarnings: [],
    sourceKey,
    sourceName: sourceKey,
    displayName: sourceKey,
    nodes: [],
    actions: [],
    edges: [],
    logicNetworks: [],
  }
}

function counts() {
  return { sequenceCount: 0, nodeCount: 0, actionCount: 0, edgeCount: 0 }
}

interface Deferred<T> { promise: Promise<T>, resolve: (value: T) => void }
function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((accept) => {
    resolve = accept
  })
  return { promise, resolve }
}
