import type {
  LineUpMonitorDelta,
  LineUpMonitorGateway,
  LineUpMonitorRealtimeConnection,
  LineUpMonitorSnapshot,
} from '@/api/modules/LineUp/monitor'
import { describe, expect, it } from 'vitest'
import { useLineUpMonitor } from './useLineUpMonitor'

class FakeConnection implements LineUpMonitorRealtimeConnection {
  handlers = new Map<string, Array<(payload: any) => void>>()
  reconnecting?: () => void
  reconnected?: () => void
  closed?: () => void
  mismatchOnNextSubscribe: LineUpMonitorSnapshot | null = null
  calls: string[] = []
  on(name: string, handler: (payload: any) => void) {
    this.handlers.set(name, [...(this.handlers.get(name) ?? []), handler])
  }

  onreconnecting(handler: () => void) { this.reconnecting = handler }
  onreconnected(handler: () => void) { this.reconnected = handler }
  onclose(handler: () => void) { this.closed = handler }
  async start() { this.calls.push('start') }
  async stop() { this.calls.push('stop') }
  async invoke(method: string, ...args: unknown[]) {
    this.calls.push(`${method}:${args.join(':')}`)
    if (method === 'Subscribe' && this.mismatchOnNextSubscribe) {
      const mismatch = this.mismatchOnNextSubscribe
      this.mismatchOnNextSubscribe = null
      this.emit('LineUpMonitorSnapshot', mismatch)
      await Promise.resolve()
    }
  }

  emit(name: string, payload: unknown) { this.handlers.get(name)?.forEach(handler => handler(payload)) }
}

function runtimeSnapshot(revision = '1'): LineUpMonitorSnapshot {
  return {
    bindingId: '9001',
    publishedRevisionId: '7001',
    stationId: '501',
    sequenceSourceKey: 'sequence:a',
    definitionRevision: 'definition-1',
    snapshotRevision: revision,
    evaluatedAtUtc: '2026-08-21T10:00:00Z',
    connection: { status: 'Online', evaluatedAtUtc: '2026-08-21T10:00:00Z', lastGoodSampleAtUtc: '2026-08-21T10:00:00Z', errorCode: '' },
    steps: [],
    transitions: [],
    conditions: [],
    edges: [],
    exceptions: [],
    activeStepSourceKeys: [],
  }
}

function staticIdentity(sequenceSourceKey = 'sequence:a') {
  return {
    bindingId: '9001',
    publishedRevisionId: '7001',
    stationId: '501',
    sequenceSourceKey,
  }
}

function gapDelta(toRevision: string): LineUpMonitorDelta {
  return {
    bindingId: '9001',
    publishedRevisionId: '7001',
    stationId: '501',
    sequenceSourceKey: 'sequence:a',
    definitionRevision: 'definition-1',
    fromSnapshotRevision: '6',
    toSnapshotRevision: toRevision,
    serverTimestamp: '2026-08-21T10:00:01Z',
    connection: runtimeSnapshot().connection,
    changedSteps: [],
    changedTransitions: [],
    changedConditions: [],
    changedEdges: [],
    exceptions: [],
  }
}

function fixture() {
  const connection = new FakeConnection()
  const calls: string[] = []
  let snapshot = runtimeSnapshot()
  let publishedRevisionId = '7001'
  let recoveryDefinition: 'available' | 'unavailable' | 'mismatch' = 'available'
  const gateway: LineUpMonitorGateway = {
    async getNavigation() { return [] },
    async getBindingsBySubStation() { return [] },
    async getBindings() { return [] },
    async getDefinition(_bindingId, sequenceSourceKey) {
      calls.push('definition')
      if (recoveryDefinition === 'unavailable') {
        throw new Error('LineUpMonitorDefinitionUnavailable')
      }
      return {
        bindingId: '9001',
        publishedRevisionId: recoveryDefinition === 'mismatch' ? '7002' : publishedRevisionId,
        stationId: '501',
        sequenceSourceKey,
        sequenceDisplayName: '上料滑台 1',
        definitionRevision: 'definition-1',
        steps: [],
        transitions: [],
        edges: [],
        viewer: {
          viewerSchemaVersion: 2,
          viewerCapabilities: ['GraphTopology', 'RuntimeOverlay'],
          viewerWarnings: [],
          sourceKey: sequenceSourceKey,
          sourceName: '上料滑台 1',
          displayName: '上料滑台 1',
          nodes: [],
          actions: [],
          edges: [],
        },
      }
    },
    async getSnapshot(_bindingId, sequenceSourceKey) {
      calls.push('snapshot')
      return { ...snapshot, publishedRevisionId, sequenceSourceKey }
    },
    async getEvents() { return { items: [], nextBeforeEventId: '', hasMore: false } },
  }
  return {
    connection,
    calls,
    gateway,
    setSnapshot: (value: LineUpMonitorSnapshot) => snapshot = value,
    setPublishedRevision: (value: string) => publishedRevisionId = value,
    setRecoveryDefinition: (value: typeof recoveryDefinition) => recoveryDefinition = value,
  }
}

describe('useLineUpMonitor', () => {
  it('loads definition and snapshot in parallel with the handshake, and never opens multiple connections', async () => {
    const value = fixture()
    const monitor = useLineUpMonitor(value.gateway, () => value.connection)

    await Promise.all([
      monitor.connect(staticIdentity()),
      monitor.connect(staticIdentity()),
    ])

    expect(value.calls).toEqual(['definition', 'snapshot'])
    expect(value.connection.calls).toEqual(['start', 'Subscribe:9001:sequence:a:definition-1:1'])
    expect(monitor.snapshot.value?.snapshotRevision).toBe('1')
  })

  it('preserves the catalog mismatch error when initial Subscribe emits a newer revision before resolving', async () => {
    const value = fixture()
    value.connection.mismatchOnNextSubscribe = { ...runtimeSnapshot(), publishedRevisionId: '7002' }
    const monitor = useLineUpMonitor(value.gateway, () => value.connection)

    await monitor.connect(staticIdentity())
    await monitor.whenIdle()

    expect(monitor.connectionState.value).toBe('error')
    expect(monitor.safeErrorCode.value).toBe('LineUpMonitorCatalogRevisionMismatch')
    expect(monitor.definition.value).toBeNull()
    expect(monitor.snapshot.value).toBeNull()
    expect(monitor.projection.value).toBeNull()
  })

  it('recovers a delta gap with a full snapshot and resubscribes after reconnect', async () => {
    const value = fixture()
    const monitor = useLineUpMonitor(value.gateway, () => value.connection)
    await monitor.connect(staticIdentity())
    value.setSnapshot(runtimeSnapshot('8'))
    const gap: LineUpMonitorDelta = {
      bindingId: '9001',
      publishedRevisionId: '7001',
      stationId: '501',
      sequenceSourceKey: 'sequence:a',
      definitionRevision: 'definition-1',
      fromSnapshotRevision: '6',
      toSnapshotRevision: '7',
      serverTimestamp: '2026-08-21T10:00:01Z',
      connection: runtimeSnapshot().connection,
      changedSteps: [],
      changedTransitions: [],
      changedConditions: [],
      changedEdges: [],
      exceptions: [],
    }

    value.connection.emit('LineUpMonitorDelta', gap)
    await monitor.whenIdle()
    expect(monitor.snapshot.value?.snapshotRevision).toBe('8')
    expect(value.calls.filter(item => item === 'snapshot')).toHaveLength(2)

    value.connection.reconnected?.()
    await monitor.whenIdle()
    expect(value.connection.calls.at(-1)).toBe('Subscribe:9001:sequence:a:definition-1:8')
  })

  it('preserves the catalog mismatch error when recovery Subscribe emits a newer revision before resolving', async () => {
    const value = fixture()
    const monitor = useLineUpMonitor(value.gateway, () => value.connection)
    await monitor.connect(staticIdentity())
    value.connection.mismatchOnNextSubscribe = { ...runtimeSnapshot(), publishedRevisionId: '7002' }

    // 2026-10-09 检查清单 #10 后：SnapshotRequired 单独出现且已有匹配快照时不再触发重拉，
    // recover 路径改由真正的丢包证据（delta gap）驱动——这里用 gap 触发同一恢复链路。
    value.connection.emit('LineUpMonitorDelta', gapDelta('7'))
    await monitor.whenIdle()

    expect(monitor.connectionState.value).toBe('error')
    expect(monitor.safeErrorCode.value).toBe('LineUpMonitorCatalogRevisionMismatch')
    expect(monitor.definition.value).toBeNull()
    expect(monitor.snapshot.value).toBeNull()
    expect(monitor.projection.value).toBeNull()
  })

  it.each(['unavailable', 'mismatch'] as const)(
    'clears stale live overlay state when recovery definition is %s',
    async (recoveryDefinition) => {
      const value = fixture()
      const monitor = useLineUpMonitor(value.gateway, () => value.connection)
      await monitor.connect(staticIdentity())
      expect(monitor.projection.value).not.toBeNull()
      value.setRecoveryDefinition(recoveryDefinition)

      // 同上：恢复路径由 delta gap 触发（SnapshotRequired 不再无条件重拉）。
      value.connection.emit('LineUpMonitorDelta', gapDelta('7'))
      await monitor.whenIdle()

      expect(monitor.definition.value).toBeNull()
      expect(monitor.snapshot.value).toBeNull()
      expect(monitor.projection.value).toBeNull()
      expect(monitor.connectionState.value).toBe('error')
    },
  )

  // 2026-10-09 检查清单 #10：Hub 在版本推进时会先发 SnapshotRequired、紧接着推送完整 Snapshot——
  // 这是订阅响应的一部分，不是丢包；客户端不得因此重拉两次 HTTP 并再次 Subscribe。
  it('does not refetch or resubscribe when SnapshotRequired is immediately followed by a full snapshot', async () => {
    const value = fixture()
    const monitor = useLineUpMonitor(value.gateway, () => value.connection)
    await monitor.connect(staticIdentity())
    const snapshotCalls = value.calls.filter(item => item === 'snapshot').length
    const subscribeCalls = value.connection.calls.filter(call => call.startsWith('Subscribe:')).length

    value.connection.emit('LineUpSnapshotRequired', undefined)
    value.connection.emit('LineUpMonitorSnapshot', runtimeSnapshot('9'))
    await monitor.whenIdle()
    // 越过排队任务的等待窗口，确认没有迟到的重拉
    await new Promise(resolve => setTimeout(resolve, 400))
    await monitor.whenIdle()

    expect(monitor.snapshot.value?.snapshotRevision).toBe('9')
    expect(monitor.connectionState.value).toBe('connected')
    expect(value.calls.filter(item => item === 'snapshot').length).toBe(snapshotCalls)
    expect(value.connection.calls.filter(call => call.startsWith('Subscribe:')).length).toBe(subscribeCalls)
  })

  it('unsubscribes the old binding and sequence before changing selection', async () => {
    const value = fixture()
    const monitor = useLineUpMonitor(value.gateway, () => value.connection)
    await monitor.connect(staticIdentity())
    await monitor.connect(staticIdentity('sequence:b'))

    expect(value.connection.calls).toContain('Unsubscribe:9001:sequence:a')
    expect(value.connection.calls.at(-1)).toBe('Subscribe:9001:sequence:b:definition-1:1')
  })

  // 连接是页面级资源。切 graph / 切顺控只换 Group——重建连接要重新 negotiate + 握手，
  // 每次点击都白付一轮，正是进页面后那股顿挫的来源之一。
  it('reuses one connection across selection changes instead of rebuilding it', async () => {
    const value = fixture()
    const monitor = useLineUpMonitor(value.gateway, () => value.connection)

    await monitor.connect(staticIdentity())
    await monitor.connect(staticIdentity('sequence:b'))
    await monitor.connect(staticIdentity('sequence:c'))

    expect(value.connection.calls.filter(call => call === 'start')).toHaveLength(1)
    expect(value.connection.calls).not.toContain('stop')
  })

  it('creates exactly one connection even when several selections race', async () => {
    const value = fixture()
    let built = 0
    const monitor = useLineUpMonitor(value.gateway, () => {
      built += 1
      return value.connection
    })

    await Promise.all([
      monitor.connect(staticIdentity()),
      monitor.connect(staticIdentity('sequence:b')),
    ])

    expect(built).toBe(1)
    expect(value.connection.calls.filter(call => call === 'start')).toHaveLength(1)
  })

  it('warms the station bindings on the shared connection', async () => {
    const value = fixture()
    const monitor = useLineUpMonitor(value.gateway, () => value.connection)

    await monitor.warm(['9001', '9002'])

    expect(value.connection.calls).toContain('start')
    expect(value.connection.calls.at(-1)).toBe('Warm:9001,9002')
  })

  it('reuses the warmed connection when the user then opens a graph', async () => {
    const value = fixture()
    const monitor = useLineUpMonitor(value.gateway, () => value.connection)

    await monitor.warm(['9001'])
    await monitor.connect(staticIdentity())

    expect(value.connection.calls.filter(call => call === 'start')).toHaveLength(1)
    expect(value.connection.calls.at(-1)).toBe('Subscribe:9001:sequence:a:definition-1:1')
  })

  it('never lets a warm failure surface to the caller', async () => {
    const value = fixture()
    const monitor = useLineUpMonitor(value.gateway, () => {
      throw new Error('LineUpMonitorConnectFailed')
    })

    await expect(monitor.warm(['9001'])).resolves.toBeUndefined()
  })

  // 2026-10-09 检查清单 #11 复核：warmLoop 曾保存 finally() 链出的新 Promise 却与原始
  // loop 比较，永不清空——第一次预热完成后所有后续 warm() 都被「已有循环在跑」挡掉，
  // 请求再也不发。这里锁死两条真实用户路径：切工位、清空选择（取消预热）。
  it('still sends the warm request after a previous warm finished (station switch)', async () => {
    const value = fixture()
    const monitor = useLineUpMonitor(value.gateway, () => value.connection)

    await monitor.warm(['9001'])
    await monitor.warm(['9002'])

    expect(value.connection.calls.filter(call => call.startsWith('Warm:'))).toEqual(['Warm:9001', 'Warm:9002'])
  })

  it('still sends the cancel warm after a previous warm finished (clear selection)', async () => {
    const value = fixture()
    const monitor = useLineUpMonitor(value.gateway, () => value.connection)

    await monitor.warm(['9001'])
    await monitor.warm([])

    expect(value.connection.calls.filter(call => call.startsWith('Warm:'))).toEqual(['Warm:9001', 'Warm:'])
  })

  // 复用连接后，刚切走那一刻还可能收到上一个 Group 的 delta。
  // 不挡掉的话 applyMonitorDelta 会判成 definition-mismatch，白白触发一次全量重拉。
  it('ignores a delta addressed to the sequence that was just left', async () => {
    const value = fixture()
    const monitor = useLineUpMonitor(value.gateway, () => value.connection)
    await monitor.connect(staticIdentity('sequence:b'))
    const before = value.calls.length

    value.connection.emit('LineUpMonitorDelta', {
      bindingId: '9001',
      publishedRevisionId: '7001',
      stationId: '501',
      sequenceSourceKey: 'sequence:a',
      definitionRevision: 'definition-1',
      fromSnapshotRevision: '1',
      toSnapshotRevision: '2',
      serverTimestamp: '2026-08-21T10:00:01Z',
      connection: runtimeSnapshot().connection,
      changedSteps: [],
      changedTransitions: [],
      changedConditions: [],
      changedEdges: [],
      exceptions: [],
    } satisfies LineUpMonitorDelta)
    await monitor.whenIdle()

    expect(value.calls.length).toBe(before)
    expect(monitor.snapshot.value?.snapshotRevision).toBe('1')
  })

  // 页面内切工位/切 graph 都走这里：退订阅并清空状态，但连接留着——
  // 下一次 Subscribe 和预热都要用它，关掉就等于每次切换重新握手。
  it('disconnectAndClear synchronously clears state and unsubscribes but keeps the connection', async () => {
    const value = fixture()
    const monitor = useLineUpMonitor(value.gateway, () => value.connection)
    await monitor.connect(staticIdentity())

    const disconnecting = monitor.disconnectAndClear()
    expect(monitor.definition.value).toBeNull()
    expect(monitor.snapshot.value).toBeNull()
    expect(monitor.connectionState.value).toBe('idle')
    await disconnecting

    expect(value.connection.calls.at(-1)).toBe('Unsubscribe:9001:sequence:a')
    expect(value.connection.calls).not.toContain('stop')
  })

  it('reconnects on the kept connection after disconnectAndClear', async () => {
    const value = fixture()
    const monitor = useLineUpMonitor(value.gateway, () => value.connection)
    await monitor.connect(staticIdentity())
    await monitor.disconnectAndClear()
    await monitor.connect(staticIdentity('sequence:b'))

    expect(value.connection.calls.filter(call => call === 'start')).toHaveLength(1)
    expect(value.connection.calls.at(-1)).toBe('Subscribe:9001:sequence:b:definition-1:1')
  })

  it('disposal stops the connection and ignores late deltas', async () => {
    const value = fixture()
    const monitor = useLineUpMonitor(value.gateway, () => value.connection)
    await monitor.connect(staticIdentity())
    await monitor.dispose()
    value.connection.emit('LineUpMonitorDelta', {
      bindingId: '9001',
      publishedRevisionId: '7001',
      stationId: '501',
      sequenceSourceKey: 'sequence:a',
      definitionRevision: 'definition-1',
      fromSnapshotRevision: '1',
      toSnapshotRevision: '2',
      serverTimestamp: '2026-08-21T10:00:01Z',
      connection: runtimeSnapshot().connection,
      changedSteps: [],
      changedTransitions: [],
      changedConditions: [],
      changedEdges: [],
      exceptions: [],
    } satisfies LineUpMonitorDelta)

    expect(value.connection.calls.at(-1)).toBe('stop')
    expect(monitor.snapshot.value).toBeNull()
  })

  it('rejects a concurrently published revision that agrees internally but not with the static catalog and refetches catalog immediately', async () => {
    const value = fixture()
    value.setPublishedRevision('7002')
    let catalogRefreshes = 0
    const monitor = useLineUpMonitor(
      value.gateway,
      () => value.connection,
      async () => { catalogRefreshes += 1 },
    )

    await monitor.connect(staticIdentity())
    await monitor.whenIdle()

    expect(monitor.definition.value).toBeNull()
    expect(monitor.snapshot.value).toBeNull()
    expect(monitor.projection.value).toBeNull()
    expect(monitor.safeErrorCode.value).toBe('LineUpMonitorCatalogRevisionMismatch')
    expect(catalogRefreshes).toBe(1)
    // 握手与取数并行，所以连接可能已经起来了；关键是绝不能对陈旧目录发起订阅。
    expect(value.connection.calls.some(call => call.startsWith('Subscribe'))).toBe(false)
  })

  it('coalesces repeated live revision mismatches into one direct catalog refresh', async () => {
    const value = fixture()
    let resolveRefresh!: () => void
    const refreshGate = new Promise<void>((resolve) => {
      resolveRefresh = resolve
    })
    let catalogRefreshes = 0
    const monitor = useLineUpMonitor(
      value.gateway,
      () => value.connection,
      async () => {
        catalogRefreshes += 1
        await refreshGate
      },
    )
    await monitor.connect(staticIdentity())
    const mismatch = { ...runtimeSnapshot(), publishedRevisionId: '7002' }

    value.connection.emit('LineUpMonitorSnapshot', mismatch)
    value.connection.emit('LineUpMonitorSnapshot', mismatch)

    expect(catalogRefreshes).toBe(1)
    resolveRefresh()
    await monitor.whenIdle()
    expect(catalogRefreshes).toBe(1)
  })

  it('queues one follow-up recovery when a second publication lands during catalog refresh reconnect', async () => {
    const value = fixture()
    let catalogRefreshes = 0
    let secondMismatchState: { connection: string, definition: unknown, snapshot: unknown, projection: unknown } | null = null
    let monitor!: ReturnType<typeof useLineUpMonitor>
    const refreshCatalog = async () => {
      catalogRefreshes += 1
      if (catalogRefreshes === 1) {
        value.setPublishedRevision('7003')
        await monitor.connect({ ...staticIdentity(), publishedRevisionId: '7002' })
        secondMismatchState = {
          connection: monitor.connectionState.value,
          definition: monitor.definition.value,
          snapshot: monitor.snapshot.value,
          projection: monitor.projection.value,
        }
        return
      }
      await monitor.connect({ ...staticIdentity(), publishedRevisionId: '7003' })
    }
    monitor = useLineUpMonitor(value.gateway, () => value.connection, refreshCatalog)
    await monitor.connect(staticIdentity())

    value.connection.emit('LineUpMonitorSnapshot', { ...runtimeSnapshot(), publishedRevisionId: '7002' })
    await monitor.whenIdle()

    expect(catalogRefreshes).toBe(2)
    expect(secondMismatchState).toEqual({ connection: 'error', definition: null, snapshot: null, projection: null })
    expect(monitor.connectionState.value).toBe('connected')
    expect(monitor.definition.value?.publishedRevisionId).toBe('7003')
    expect(monitor.snapshot.value?.publishedRevisionId).toBe('7003')
    expect(monitor.projection.value).not.toBeNull()
  })
})
