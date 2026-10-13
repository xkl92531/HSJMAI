import type {
  LineUpMonitorAdminGateway,
  LineUpMonitorBindingSummary,
  LineUpMonitorNavigationNode,
} from '@/api/modules/LineUp/monitor'
import { describe, expect, it } from 'vitest'
import { createLineUpBindingWorkspace } from './useLineUpBindingWorkspace'

describe('createLineUpBindingWorkspace', () => {
  it('loads only the latest exact work-position selection and clears stale rows immediately', async () => {
    const gateway = new FakeGateway()
    const workspace = createLineUpBindingWorkspace(gateway)
    const first = workspace.selectSubStation('3001')
    const second = workspace.selectSubStation('3002')

    expect(workspace.bindings.value).toEqual([])
    gateway.bindingRequests.get('3001')?.resolve([binding('5001', '3001')])
    await first
    expect(workspace.bindings.value).toEqual([])
    gateway.bindingRequests.get('3002')?.resolve([binding('5002', '3002')])
    await second

    expect(gateway.requestedSubStations).toEqual(['3001', '3002'])
    expect(workspace.bindings.value.map(item => item.bindingId)).toEqual(['5002'])
  })

  it('prepare refreshes binding and navigation, validate and enable remain explicit gated actions', async () => {
    const gateway = new FakeGateway(true)
    const workspace = createLineUpBindingWorkspace(gateway)
    await workspace.refreshNavigation()
    await workspace.selectSubStation('3001')

    await expect(workspace.validate('5001')).rejects.toThrow('LineUpBindingPrepareRequired')
    await workspace.prepare('5001')
    expect(gateway.calls).toContain('prepare:5001')
    expect(gateway.navigationCalls).toBe(2)
    expect(gateway.calls).not.toContain('monitor:5001:true')

    await workspace.validate('5001')
    expect(gateway.calls).toContain('validate:5001')
    expect(gateway.calls).not.toContain('monitor:5001:true')
    await workspace.setMonitoring('5001', true)
    expect(gateway.calls).toContain('monitor:5001:true')
  })

  it('allows explicit disable for an enabled stale binding but exposes no alias or publish operation', async () => {
    const gateway = new FakeGateway(true)
    const workspace = createLineUpBindingWorkspace(gateway)
    await workspace.selectSubStation('3001')
    gateway.canEnable = false

    await workspace.setMonitoring('5001', false)

    expect(gateway.calls).toContain('monitor:5001:false')
    expect('publish' in workspace).toBe(false)
    expect('saveAlias' in workspace).toBe(false)
  })
})

class FakeGateway implements LineUpMonitorAdminGateway {
  readonly bindingRequests = new Map<string, Deferred<LineUpMonitorBindingSummary[]>>()
  readonly requestedSubStations: string[] = []
  readonly calls: string[] = []
  navigationCalls = 0
  canEnable = true

  constructor(private readonly immediate = false) {}

  async getNavigation(): Promise<LineUpMonitorNavigationNode[]> {
    this.navigationCalls++
    return [navigationNode()]
  }

  async getBindingsBySubStation(subStationId: string) {
    this.requestedSubStations.push(subStationId)
    if (this.immediate) {
      return [binding('5001', subStationId, true)]
    }
    const request = deferred<LineUpMonitorBindingSummary[]>()
    this.bindingRequests.set(subStationId, request)
    return request.promise
  }

  async prepareBinding(bindingId: string) {
    this.calls.push(`prepare:${bindingId}`)
    return { bindingId, bindingStatus: 'NeedsReview', definitionRevision: 'D1', tagCount: 4, canValidate: true, issues: [] }
  }

  async validateBinding(bindingId: string) {
    this.calls.push(`validate:${bindingId}`)
    return { bindingId, bindingStatus: 'Resolved', goodCount: 4, totalCount: 4, canEnable: this.canEnable, issues: [] }
  }

  async setMonitoring(bindingId: string, enabled: boolean) {
    this.calls.push(`monitor:${bindingId}:${enabled}`)
    return { bindingId, bindingStatus: 'Resolved', monitoringEnabled: enabled, globalMonitorEnabled: true }
  }

  async getBindings() { return [] }
  async getDefinition(): Promise<never> { throw new Error('unused') }
  async getSnapshot(): Promise<never> { throw new Error('unused') }
  async getEvents(): Promise<never> { throw new Error('unused') }
}

function binding(bindingId: string, subStationId: string, enabled = false): LineUpMonitorBindingSummary {
  return {
    bindingId,
    graphAssetId: '4001',
    publishedRevisionId: '6001',
    lineId: '10',
    deviceId: '40',
    processId: '20',
    stationId: '30',
    subStationId,
    plcProjectId: '4612671841813794816',
    graphSourceName: 'FB260_CacheGraph',
    bindingStatus: enabled ? 'Resolved' : 'NeedsReview',
    monitoringEnabled: enabled,
    runtimeBindingReady: enabled,
    sequences: [{ sequenceSourceKey: 'sequence:1', sequenceDisplayName: '上料滑台1', stepCount: 83, transitionCount: 92, runtimeStatus: '' }],
  }
}

function navigationNode(): LineUpMonitorNavigationNode {
  return {
    nodeId: 'substation:3001',
    nodeType: 'SubStation',
    parentNodeId: 'station:30',
    entityId: '3001',
    code: 'OP020-01',
    name: '主工位',
    sortOrder: 1,
    descendantCount: 0,
    issueCount: 0,
    runtimeStatus: 'Ready',
    publishedBindingCount: 1,
  }
}

interface Deferred<T> {
  promise: Promise<T>
  resolve: (value: T) => void
}

function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((accept) => {
    resolve = accept
  })
  return { promise, resolve }
}
