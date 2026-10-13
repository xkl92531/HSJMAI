import type {
  LineUpMonitorDefinition,
  LineUpMonitorGateway,
  LineUpMonitorRealtimeConnection,
  LineUpMonitorSnapshot,
} from '@/api/modules/LineUp/monitor'
import * as signalR from '@microsoft/signalr'
import { computed, ref } from 'vue'
import lineUpMonitorApi from '@/api/modules/LineUp/monitor'
import { applyMonitorDelta, projectMonitorSnapshot } from './monitorProjection'

export type LineUpMonitorConnectionState = 'idle' | 'connecting' | 'connected' | 'reconnecting' | 'offline' | 'error'

export interface LineUpMonitorStaticIdentity {
  bindingId: string
  publishedRevisionId: string
  stationId: string
  sequenceSourceKey: string
}

function hubBaseUrl() {
  const value = import.meta.env.DEV
    ? import.meta.env.VITE_APP_API_BASEURL
    : (window as any).apiConfig?.apiUrl || ''
  return String(value || '').replace(/\/$/, '')
}

function createDefaultConnection(): LineUpMonitorRealtimeConnection {
  return new signalR.HubConnectionBuilder()
    .withUrl(`${hubBaseUrl()}/hubs/lineup-monitor`, {
      accessTokenFactory: () => localStorage.getItem('token') ?? '',
      withCredentials: false,
    })
    .withAutomaticReconnect([0, 1000, 3000, 5000, 10000])
    .configureLogging(signalR.LogLevel.Warning)
    .build()
}

export function useLineUpMonitor(
  gateway: LineUpMonitorGateway = lineUpMonitorApi,
  connectionFactory: () => LineUpMonitorRealtimeConnection = createDefaultConnection,
  refreshCatalog: () => void | Promise<void> = () => {},
) {
  const definition = ref<LineUpMonitorDefinition | null>(null)
  const snapshot = ref<LineUpMonitorSnapshot | null>(null)
  const connectionState = ref<LineUpMonitorConnectionState>('idle')
  const safeErrorCode = ref('')
  let connection: LineUpMonitorRealtimeConnection | null = null
  let connectionPromise: Promise<LineUpMonitorRealtimeConnection> | null = null
  let subscribedKey: { bindingId: string, sequenceSourceKey: string } | null = null
  let selectedBindingId = ''
  let selectedPublishedRevisionId = ''
  let selectedStationId = ''
  let selectedSequenceSourceKey = ''
  let connectPromise: Promise<void> | null = null
  let catalogRefreshPromise: Promise<void> | null = null
  let activeCatalogRefreshGeneration: number | null = null
  let pendingCatalogRefreshGeneration: number | null = null
  let queuedWork: Promise<void> = Promise.resolve()
  let generation = 0
  let permanentlyDisposed = false
  // 2026-10-09 检查清单 #10：最近一次与当前身份匹配的完整快照到达时间，与 snapshotRecoveryQueued
  // 一起用于区分「正常版本推进」与「真正丢包」。
  let lastMatchingSnapshotAt = 0
  let snapshotRecoveryQueued = false
  // 2026-10-09 检查清单 #11：warm 串行化（last-write-wins）的待发集合与串行链。
  let warmPendingIds: readonly string[] | null = null
  let warmLoop: Promise<void> | null = null

  const projection = computed(() => snapshot.value ? projectMonitorSnapshot(snapshot.value) : null)
  const isStaleOrOffline = computed(() => {
    const tone = projection.value?.connectionTone
    return tone === 'offline' || tone === 'degraded'
  })

  function queue(task: () => Promise<void>) {
    queuedWork = queuedWork.then(task, task)
    return queuedWork
  }

  function matchesStaticIdentity(
    value: Pick<LineUpMonitorDefinition | LineUpMonitorSnapshot, 'bindingId' | 'publishedRevisionId' | 'stationId' | 'sequenceSourceKey'>,
    expected: LineUpMonitorStaticIdentity,
  ) {
    return value.bindingId === expected.bindingId
      && value.publishedRevisionId === expected.publishedRevisionId
      && value.stationId === expected.stationId
      && value.sequenceSourceKey === expected.sequenceSourceKey
  }

  /** 当前是否已持有与选中身份匹配的完整快照（2026-10-09 检查清单 #10：区分版本推进与丢包）。 */
  function snapshotMatchesActiveIdentity() {
    const expected = activeIdentity()
    return !!expected && !!snapshot.value && matchesStaticIdentity(snapshot.value, expected)
  }

  function sleep(ms: number) {
    return new Promise<void>(resolve => setTimeout(resolve, ms))
  }

  /**
   * 当前选中的身份，未选中时 null。连接跨选择复用（切 graph / 切顺控只换 Group），
   * 所以实时消息不能再像以前那样把 expected 闭进 handler，必须按"此刻选中的是谁"判断。
   */
  function activeIdentity(): LineUpMonitorStaticIdentity | null {
    return selectedBindingId && selectedPublishedRevisionId && selectedStationId && selectedSequenceSourceKey
      ? {
          bindingId: selectedBindingId,
          publishedRevisionId: selectedPublishedRevisionId,
          stationId: selectedStationId,
          sequenceSourceKey: selectedSequenceSourceKey,
        }
      : null
  }

  function registerHandlers(target: LineUpMonitorRealtimeConnection) {
    target.on('LineUpMonitorSnapshot', (payload: LineUpMonitorSnapshot) => {
      const expected = activeIdentity()
      if (!expected || permanentlyDisposed) {
        return
      }
      if (payload.bindingId === expected.bindingId
        && payload.sequenceSourceKey === expected.sequenceSourceKey
        && !matchesStaticIdentity(payload, expected)) {
        triggerCatalogRefresh(generation)
        return
      }
      if (matchesStaticIdentity(payload, expected)
        && payload.definitionRevision === definition.value?.definitionRevision) {
        snapshot.value = payload
        // 2026-10-09 检查清单 #10：记录完整快照到达时间——SnapshotRequired 紧随其后的
        // 完整快照说明这只是正常版本推进，无需触发全量恢复。
        lastMatchingSnapshotAt = Date.now()
      }
    })
    target.on('LineUpSnapshotRequired', () => {
      // 2026-10-09 检查清单 #10：Hub.Subscribe 在客户端快照版本落后时会先发 SnapshotRequired、
      // 紧接着推送完整 Snapshot——这是正常版本推进，不是丢包。原先无条件 recover 排队执行
      // 两次 HTTP 全量读取 + 再次 Subscribe；期间 100ms 轮询再推进版本则服务端再发同样两条，
      // 形成重复恢复循环。现在：①合并恢复请求（同一时刻至多一个在排队/执行）；
      // ②「刚收到匹配完整快照」直接跳过；③排队后再核对一次（SnapshotRequired 先于
      // Snapshot 到达时，完整快照可能正在路上，短等代替立即重拉）。
      const expectedGeneration = generation
      if (snapshotRecoveryQueued) {
        return
      }
      if (Date.now() - lastMatchingSnapshotAt < 500 && snapshotMatchesActiveIdentity()) {
        return
      }
      snapshotRecoveryQueued = true
      void queue(async () => {
        try {
          const deadline = Date.now() + 300
          while (Date.now() < deadline && !snapshotMatchesActiveIdentity()) {
            if (generation !== expectedGeneration || permanentlyDisposed) {
              return
            }
            await sleep(50)
          }
          if (snapshotMatchesActiveIdentity()) {
            return
          }
          await recover(expectedGeneration)
        }
        finally {
          snapshotRecoveryQueued = false
        }
      })
    })
    target.on('LineUpMonitorDelta', (payload) => {
      const expected = activeIdentity()
      if (!expected || permanentlyDisposed || !snapshot.value) {
        return
      }
      // 连接复用的代价：刚切走的那一刻还可能收到上一个 Group 的 delta。
      // 不在这里挡掉的话 applyMonitorDelta 会判成 definition-mismatch，白白触发一次全量重拉。
      if (payload.bindingId !== expected.bindingId
        || payload.sequenceSourceKey !== expected.sequenceSourceKey) {
        return
      }
      const result = applyMonitorDelta(snapshot.value, payload)
      if (result.kind === 'applied') {
        snapshot.value = result.snapshot
      }
      else if (result.kind === 'gap' || result.kind === 'definition-mismatch') {
        const expectedGeneration = generation
        void queue(() => recover(expectedGeneration))
      }
    })
    target.onreconnecting(() => {
      if (!permanentlyDisposed && activeIdentity()) {
        connectionState.value = 'reconnecting'
      }
    })
    target.onreconnected(() => {
      const expectedGeneration = generation
      void queue(() => recover(expectedGeneration))
    })
    target.onclose(() => {
      if (connection === target) {
        connection = null
        subscribedKey = null
      }
      if (!permanentlyDisposed && activeIdentity()) {
        connectionState.value = 'offline'
      }
    })
  }

  /**
   * 连接是页面级资源：建一次用到底。切 graph / 切顺控只换 Group（Unsubscribe + Subscribe），
   * 不再拆掉重建——重建要重新 negotiate + 握手，每次点击都白付一轮，是顿挫的来源之一。
   * 只有 dispose 才真正关闭。
   */
  function ensureConnection(): Promise<LineUpMonitorRealtimeConnection> {
    if (connection) {
      return Promise.resolve(connection)
    }
    if (connectionPromise) {
      return connectionPromise
    }
    const next = connectionFactory()
    registerHandlers(next)
    const starting = (async () => {
      await next.start()
      if (permanentlyDisposed) {
        try {
          await next.stop()
        }
        catch {}
        throw new Error('LineUpMonitorDisposed')
      }
      connection = next
      subscribedKey = null
      return next
    })()
    const tracked = starting.finally(() => {
      if (connectionPromise === tracked) {
        connectionPromise = null
      }
    })
    connectionPromise = tracked
    return tracked
  }

  function canMarkConnected(
    expectedGeneration: number,
    expectedConnection: LineUpMonitorRealtimeConnection,
  ) {
    return generation === expectedGeneration
      && !permanentlyDisposed
      && connection === expectedConnection
      && definition.value !== null
      && snapshot.value !== null
      && safeErrorCode.value === ''
  }

  function triggerCatalogRefresh(expectedGeneration: number) {
    if (generation !== expectedGeneration || permanentlyDisposed) {
      return
    }
    definition.value = null
    snapshot.value = null
    connectionState.value = 'error'
    safeErrorCode.value = 'LineUpMonitorCatalogRevisionMismatch'
    void unsubscribeCurrent()
    if (catalogRefreshPromise) {
      if (activeCatalogRefreshGeneration !== expectedGeneration) {
        pendingCatalogRefreshGeneration = expectedGeneration
      }
      return
    }
    try {
      activeCatalogRefreshGeneration = expectedGeneration
      const current = Promise.resolve(refreshCatalog()).catch(() => undefined)
      const tracked = current.finally(() => {
        if (catalogRefreshPromise === tracked) {
          catalogRefreshPromise = null
          activeCatalogRefreshGeneration = null
          const pendingGeneration = pendingCatalogRefreshGeneration
          pendingCatalogRefreshGeneration = null
          if (pendingGeneration !== null) {
            triggerCatalogRefresh(pendingGeneration)
          }
        }
      })
      catalogRefreshPromise = tracked
    }
    catch {
      catalogRefreshPromise = null
      activeCatalogRefreshGeneration = null
    }
  }

  async function loadFullState(expected: LineUpMonitorStaticIdentity, expectedGeneration: number) {
    // 两个请求互不依赖，串行等于白付一轮往返（交叉校验在下面照做不误）。
    const [loadedDefinition, loadedSnapshot] = await Promise.all([
      gateway.getDefinition(expected.bindingId, expected.sequenceSourceKey),
      gateway.getSnapshot(expected.bindingId, expected.sequenceSourceKey),
    ])
    if (generation !== expectedGeneration || permanentlyDisposed) {
      return false
    }
    if (!matchesStaticIdentity(loadedDefinition, expected)
      || !matchesStaticIdentity(loadedSnapshot, expected)
      || loadedDefinition.publishedRevisionId !== loadedSnapshot.publishedRevisionId
      || loadedDefinition.stationId !== loadedSnapshot.stationId
      || loadedDefinition.definitionRevision !== loadedSnapshot.definitionRevision) {
      triggerCatalogRefresh(expectedGeneration)
      return false
    }
    definition.value = loadedDefinition
    snapshot.value = loadedSnapshot
    safeErrorCode.value = ''
    return true
  }

  async function recover(expectedGeneration: number) {
    if (!selectedBindingId || !selectedPublishedRevisionId || !selectedStationId || !selectedSequenceSourceKey
      || generation !== expectedGeneration || permanentlyDisposed) {
      return
    }
    const expected = {
      bindingId: selectedBindingId,
      publishedRevisionId: selectedPublishedRevisionId,
      stationId: selectedStationId,
      sequenceSourceKey: selectedSequenceSourceKey,
    }
    try {
      if (!await loadFullState(expected, expectedGeneration)) {
        return
      }
      const currentConnection = await ensureConnection()
      if (generation !== expectedGeneration || permanentlyDisposed) {
        return
      }
      await subscribeOn(currentConnection, expected)
      if (!canMarkConnected(expectedGeneration, currentConnection)) {
        return
      }
      connectionState.value = 'connected'
    }
    catch (error) {
      if (generation !== expectedGeneration || permanentlyDisposed) {
        return
      }
      definition.value = null
      snapshot.value = null
      connectionState.value = 'error'
      safeErrorCode.value = error instanceof Error ? error.message : 'LineUpMonitorRecoveryFailed'
    }
  }

  async function subscribeOn(
    target: LineUpMonitorRealtimeConnection,
    expected: LineUpMonitorStaticIdentity,
  ) {
    await target.invoke(
      'Subscribe',
      expected.bindingId,
      expected.sequenceSourceKey,
      definition.value?.definitionRevision ?? '',
      snapshot.value?.snapshotRevision ?? '',
    )
    subscribedKey = { bindingId: expected.bindingId, sequenceSourceKey: expected.sequenceSourceKey }
  }

  /** 退掉当前 Group，但保留连接——连接是页面级资源，只有 dispose 才关。 */
  async function unsubscribeCurrent() {
    const current = connection
    const key = subscribedKey
    subscribedKey = null
    if (!current || !key) {
      return
    }
    try {
      await current.invoke('Unsubscribe', key.bindingId, key.sequenceSourceKey)
    }
    catch {}
  }

  async function stopConnection() {
    const current = connection
    connection = null
    connectionPromise = null
    subscribedKey = null
    if (!current) {
      return
    }
    try {
      await current.stop()
    }
    catch {}
  }

  function clearSelection() {
    definition.value = null
    snapshot.value = null
    selectedBindingId = ''
    selectedPublishedRevisionId = ''
    selectedStationId = ''
    selectedSequenceSourceKey = ''
  }

  async function connectInternal(expected: LineUpMonitorStaticIdentity, expectedGeneration: number) {
    connectionState.value = 'connecting'
    safeErrorCode.value = ''
    try {
      // 握手与取数同时开跑：以前是取完 definition+snapshot 才 start()，白排一轮。
      const connecting = ensureConnection()
      connecting.catch(() => undefined)
      const loaded = await loadFullState(expected, expectedGeneration)
      if (!loaded) {
        return
      }
      const nextConnection = await connecting
      if (generation !== expectedGeneration || permanentlyDisposed) {
        return
      }
      await subscribeOn(nextConnection, expected)
      if (!canMarkConnected(expectedGeneration, nextConnection)) {
        return
      }
      connectionState.value = 'connected'
    }
    catch (error) {
      if (generation !== expectedGeneration || permanentlyDisposed) {
        return
      }
      connectionState.value = 'error'
      safeErrorCode.value = error instanceof Error ? error.message : 'LineUpMonitorConnectFailed'
      await unsubscribeCurrent()
      throw error
    }
  }

  function connect(identity: LineUpMonitorStaticIdentity) {
    const expected = {
      bindingId: String(identity?.bindingId || '').trim(),
      publishedRevisionId: String(identity?.publishedRevisionId || '').trim(),
      stationId: String(identity?.stationId || '').trim(),
      sequenceSourceKey: String(identity?.sequenceSourceKey || '').trim(),
    }
    if (!expected.bindingId || !expected.publishedRevisionId || !expected.stationId || !expected.sequenceSourceKey) {
      return Promise.resolve()
    }
    const sameSelection = selectedBindingId === expected.bindingId
      && selectedPublishedRevisionId === expected.publishedRevisionId
      && selectedStationId === expected.stationId
      && selectedSequenceSourceKey === expected.sequenceSourceKey
    if (connectPromise && sameSelection) {
      return connectPromise
    }
    if (connection && sameSelection && connectionState.value === 'connected') {
      return Promise.resolve()
    }
    permanentlyDisposed = false
    const previousBindingId = selectedBindingId
    const previousSequenceSourceKey = selectedSequenceSourceKey
    selectedBindingId = expected.bindingId
    selectedPublishedRevisionId = expected.publishedRevisionId
    selectedStationId = expected.stationId
    selectedSequenceSourceKey = expected.sequenceSourceKey
    const currentGeneration = ++generation
    const run = (async () => {
      // 只退旧 Group，连接留着给下一次 Subscribe 用。
      if (previousBindingId && previousSequenceSourceKey) {
        await unsubscribeCurrent()
      }
      definition.value = null
      snapshot.value = null
      await connectInternal(expected, currentGeneration)
    })()
    const finalPromise = run.finally(() => {
      if (connectPromise === finalPromise) {
        connectPromise = null
      }
    })
    connectPromise = finalPromise
    return finalPromise
  }

  async function disconnectAndClearInternal(permanent: boolean) {
    permanentlyDisposed = permanent
    generation++
    connectPromise = null
    activeCatalogRefreshGeneration = null
    pendingCatalogRefreshGeneration = null
    clearSelection()
    connectionState.value = 'idle'
    safeErrorCode.value = ''
    await unsubscribeCurrent()
    if (permanent) {
      await stopConnection()
    }
  }

  /**
   * 退订阅并清空状态，但**保留连接**：页面内切工位 / 切 graph 都走这里，
   * 连接留着下一次 Subscribe 直接用，也让预热（warm）不必重新握手。
   * 真正关闭连接只在 dispose（页面卸载，index.vue 的 onBeforeUnmount）。
   */
  function disconnectAndClear() {
    return disconnectAndClearInternal(false)
  }

  /**
   * 预热（2026-09-21）：选中工位时把该工位名下全部绑定报给服务端，让它立刻纳入轮询并预采一次。
   * 等用户点到具体 graph 时快照已经热了，Subscribe 不用再现场冷读 PLC。
   * 传空数组即取消本连接的预热。失败无害——正常轮询会补上，所以不抛给调用方。
   *
   * 2026-10-09 检查清单 #11：串行化（last-write-wins）——服务端 Warm 是整集合替换语义，
   * 并发/迟到的 warm 若乱序到达，后到的旧集合会覆盖新选择（甚至被空集清掉），表现为页面
   * 已切走但旧工位仍被轮询。这里保证请求发出顺序 = 调用顺序；飞行期间的新调用会在当前
   * invoke 完成后自动补发，最终服务端集合总是最后一次 warm 的入参。
   */
  async function warm(bindingIds: readonly string[]) {
    if (permanentlyDisposed) {
      return
    }
    warmPendingIds = bindingIds
    if (warmLoop) {
      return
    }
    const loop = (async () => {
      while (warmPendingIds !== null) {
        const ids = warmPendingIds
        warmPendingIds = null
        try {
          const target = await ensureConnection()
          if (permanentlyDisposed) {
            return
          }
          await target.invoke('Warm', [...ids])
        }
        catch {}
      }
    })()
    // 2026-10-09 检查清单 #11 复核：warmLoop 必须保存 finally() 返回的新 Promise 本身，
    // 清理回调也必须与它比较。原实现把 finally() 的返回值赋给 warmLoop、却拿它与原始
    // loop 比较（永不相等），warmLoop 永不清空——第一次预热结束后所有后续 warm() 都走
    // 「已有循环在跑」分支直接 return，warmPendingIds 再无人消费：切工位/取消预热都发
    // 不出请求，旧工位预热集合在服务端一直轮询。
    const tracked = loop.finally(() => {
      if (warmLoop === tracked) {
        warmLoop = null
      }
    })
    warmLoop = tracked
    await tracked
  }

  async function dispose() {
    await disconnectAndClearInternal(true)
  }

  async function whenIdle() {
    while (true) {
      const observedConnect = connectPromise
      const observedWork = queuedWork
      const observedRefresh = catalogRefreshPromise
      await observedConnect?.catch(() => undefined)
      await observedWork.catch(() => undefined)
      await observedRefresh?.catch(() => undefined)
      if (!connectPromise
        && !catalogRefreshPromise
        && pendingCatalogRefreshGeneration === null
        && queuedWork === observedWork) {
        return
      }
    }
  }

  return {
    definition,
    snapshot,
    projection,
    connectionState,
    safeErrorCode,
    isStaleOrOffline,
    connect,
    warm,
    disconnectAndClear,
    dispose,
    whenIdle,
  }
}
