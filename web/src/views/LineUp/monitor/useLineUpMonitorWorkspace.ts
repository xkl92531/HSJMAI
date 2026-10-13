import type { LineUpMonitorStaticIdentity } from './useLineUpMonitor'
import type {
  LineUpGraphCatalogAsset,
  LineUpGraphCatalogGateway,
  LineUpGraphPreview,
} from '@/api/modules/LineUp/catalog'
import type {
  LineUpMonitorBindingSummary,
  LineUpMonitorGateway,
  LineUpMonitorNavigationNode,
} from '@/api/modules/LineUp/monitor'
import type { EntityId } from '@/api/modules/LineUp/types'
import { computed, ref } from 'vue'

type MonitorWorkspaceGateway = Pick<LineUpMonitorGateway, 'getBindings' | 'getBindingsBySubStation' | 'getNavigation'>
type CatalogWorkspaceGateway = Pick<LineUpGraphCatalogGateway, 'getAssets' | 'previewPublish'>

export interface LineUpMonitorWorkspaceLive {
  /** 退订阅并清空状态；连接保留给下一次 Subscribe / 预热复用。 */
  disconnectAndClear: () => Promise<void>
  connect: (identity: LineUpMonitorStaticIdentity) => Promise<void>
  /** 预热：把整个工位名下的绑定交给服务端提前纳入轮询，消除点到 graph 时的冷读。 */
  warm: (bindingIds: readonly string[]) => Promise<void>
  /** 页面卸载：退订阅 + 真正关闭连接。 */
  dispose: () => Promise<void>
}

export interface LineUpMonitorRouteSelection {
  subStationId?: EntityId
  bindingId?: EntityId
  sequenceSourceKey?: string
  lineId?: EntityId
  deviceId?: EntityId
  stationId?: EntityId
  /** 外部对接按编码定位：子工站编码（Basic_SubStation.SubStationCode），大小写不敏感 */
  subStationCode?: string
  /** 可选产线编码（Basic_ProductionLine.Code），用于跨产线同编码子工站消歧 */
  lineCode?: string
}

export function createLineUpMonitorWorkspace(
  monitorGateway: MonitorWorkspaceGateway,
  catalogGateway: CatalogWorkspaceGateway,
  live: LineUpMonitorWorkspaceLive,
) {
  const navigationNodes = ref<LineUpMonitorNavigationNode[]>([])
  const selectedSubStationId = ref<EntityId | null>(null)
  const bindings = ref<LineUpMonitorBindingSummary[]>([])
  const assets = ref<LineUpGraphCatalogAsset[]>([])
  const selectedBindingId = ref<EntityId>('')
  const preview = ref<LineUpGraphPreview | null>(null)
  const targetRevisionId = ref<EntityId>('')
  const publishedRevisionId = ref<EntityId>('')
  const selectedSequenceKey = ref('')
  const navigationLoading = ref(false)
  const contentLoading = ref(false)
  const safeErrorCode = ref('')
  let navigationGeneration = 0
  let selectionGeneration = 0
  let navigationKeyword = ''

  const selectedBinding = computed(() => bindings.value.find(
    binding => binding.bindingId === selectedBindingId.value,
  ) ?? null)
  const selectedAsset = computed(() => assets.value.find(asset => (
    asset.bindingId === selectedBindingId.value
  )) ?? null)
  const sequences = computed(() => preview.value?.sequences ?? [])
  const selectedSequence = computed(() => sequences.value.find(
    sequence => sequence.sourceKey === selectedSequenceKey.value,
  ) ?? null)
  const selectedPath = computed(() => {
    const byId = new Map(navigationNodes.value.map(node => [node.nodeId, node]))
    const path: LineUpMonitorNavigationNode[] = []
    let current = navigationNodes.value.find(node => (
      node.nodeType === 'SubStation' && node.entityId === selectedSubStationId.value
    ))
    while (current) {
      path.unshift(current)
      current = current.parentNodeId ? byId.get(current.parentNodeId) : undefined
    }
    return path
  })

  async function refreshNavigation(keyword = navigationKeyword) {
    const generation = ++navigationGeneration
    navigationKeyword = keyword.trim()
    navigationLoading.value = true
    try {
      const result = await monitorGateway.getNavigation(true, navigationKeyword)
      if (generation !== navigationGeneration) {
        return
      }
      navigationNodes.value = result
      if (selectedSubStationId.value && !isLeaf(selectedSubStationId.value)) {
        await clearSelection()
      }
    }
    catch (error) {
      if (generation === navigationGeneration) {
        safeErrorCode.value = errorCode(error, 'LineUpMonitorNavigationFailed')
      }
    }
    finally {
      if (generation === navigationGeneration) {
        navigationLoading.value = false
      }
    }
  }

  async function initializeRoute(route: LineUpMonitorRouteSelection) {
    await refreshNavigation()
    let subStationId = clean(route.subStationId)
    if (!subStationId && route.lineId && route.deviceId && route.stationId) {
      const legacy = await monitorGateway.getBindings(route.lineId, route.deviceId, route.stationId)
      const candidates = [...new Set(legacy.map(binding => binding.subStationId).filter(Boolean))]
      subStationId = candidates.length === 1 ? candidates[0] : ''
    }
    if (!subStationId) {
      // 外部对接编码定位：subStationCode（+可选 lineCode 消歧）→ 在已加载的导航树里解析出工位 ID
      subStationId = resolveSubStationIdByCode(clean(route.subStationCode), clean(route.lineCode))
    }
    if (!subStationId || !isLeaf(subStationId)) {
      await clearSelection()
      return false
    }
    await selectSubStation(subStationId, clean(route.bindingId), clean(route.sequenceSourceKey))
    return selectedSubStationId.value === subStationId && bindings.value.length > 0
  }

  function resolveSubStationIdByCode(subStationCode: string, lineCode: string) {
    if (!subStationCode) {
      return ''
    }
    let candidates = navigationNodes.value.filter(node => (
      node.nodeType === 'SubStation' && sameCode(node.code, subStationCode)
    ))
    if (lineCode) {
      const byId = new Map(navigationNodes.value.map(node => [node.nodeId, node]))
      const lineNodeIds = new Set(navigationNodes.value.filter(node => (
        node.nodeType === 'Line' && sameCode(node.code, lineCode)
      )).map(node => node.nodeId))
      candidates = candidates.filter((node) => {
        let current: LineUpMonitorNavigationNode | undefined = node
        while (current) {
          if (lineNodeIds.has(current.nodeId)) {
            return true
          }
          current = current.parentNodeId ? byId.get(current.parentNodeId) : undefined
        }
        return false
      })
    }
    return candidates[0]?.entityId ?? ''
  }

  async function selectSubStation(
    subStationId: EntityId,
    preferredBindingId = '',
    preferredSequenceKey = '',
  ) {
    if (!isLeaf(subStationId)) {
      await clearSelection()
      return
    }
    const generation = ++selectionGeneration
    selectedSubStationId.value = subStationId
    clearContent()
    contentLoading.value = true
    const disconnecting = live.disconnectAndClear()
    try {
      const [loadedBindings, loadedAssets] = await Promise.all([
        monitorGateway.getBindingsBySubStation(subStationId),
        loadAssets(subStationId, generation),
        disconnecting,
      ])
      if (!isCurrent(generation, subStationId)) {
        return
      }
      bindings.value = loadedBindings.filter(binding => binding.subStationId === subStationId)
      assets.value = loadedAssets.filter(asset => asset.subStationId === subStationId)
      // 预热要抢在 previewPublish / getDefinition 前面发出去，让服务端那一次 PLC 读
      // 与后面这几个请求并行跑掉；等点到 graph 时快照已经热了。不 await：预热失败无害。
      void live.warm(bindings.value
        .filter(binding => binding.monitoringEnabled)
        .map(binding => binding.bindingId))
      const nextBindingId = bindings.value.some(binding => binding.bindingId === preferredBindingId)
        ? preferredBindingId
        : bindings.value[0]?.bindingId ?? ''
      await loadBindingPreview(nextBindingId, preferredSequenceKey, generation)
    }
    catch (error) {
      if (isCurrent(generation, subStationId)) {
        safeErrorCode.value = errorCode(error, 'LineUpMonitorSelectionFailed')
      }
    }
    finally {
      if (isCurrent(generation, subStationId)) {
        contentLoading.value = false
      }
    }
  }

  async function selectBinding(bindingId: EntityId, preferredSequenceKey = '') {
    const subStationId = selectedSubStationId.value
    if (!subStationId || !bindings.value.some(binding => binding.bindingId === bindingId)) {
      return
    }
    const generation = ++selectionGeneration
    selectedBindingId.value = bindingId
    preview.value = null
    selectedSequenceKey.value = ''
    targetRevisionId.value = ''
    publishedRevisionId.value = ''
    safeErrorCode.value = ''
    contentLoading.value = true
    // 不先断：connect() 内部本来就会退掉旧订阅，先断只是白等一轮往返；
    // 走不到 connect 的路径由 connectLive 的守卫和下面的 catch 负责退订阅。
    try {
      await loadBindingPreview(bindingId, preferredSequenceKey, generation)
    }
    catch (error) {
      if (isCurrent(generation, subStationId)) {
        safeErrorCode.value = errorCode(error, 'LineUpMonitorGraphFailed')
        // 2026-10-09 检查清单 #9：断开副作用必须与错误文案同受守卫保护——
        // 已在 A、点了 B 再切 C 时，B 的 previewPublish 迟到失败不能把 C 的实时连接清掉。
        await live.disconnectAndClear()
      }
    }
    finally {
      if (isCurrent(generation, subStationId)) {
        contentLoading.value = false
      }
    }
  }

  async function selectSequence(sourceKey: string) {
    const binding = selectedBinding.value
    const subStationId = selectedSubStationId.value
    if (!binding || !subStationId || !sequences.value.some(sequence => sequence.sourceKey === sourceKey)) {
      return
    }
    const generation = ++selectionGeneration
    selectedSequenceKey.value = sourceKey
    safeErrorCode.value = ''
    try {
      if (isCurrent(generation, subStationId)) {
        await connectLive(binding, sourceKey)
      }
    }
    catch (error) {
      if (isCurrent(generation, subStationId)) {
        safeErrorCode.value = errorCode(error, 'LineUpMonitorConnectFailed')
        // 2026-10-09 检查清单 #9：同 selectBinding——旧选择的迟到失败不断开当前监控。
        await live.disconnectAndClear()
      }
    }
  }

  async function refresh() {
    const subStationId = selectedSubStationId.value
    const bindingId = selectedBindingId.value
    const sequenceSourceKey = selectedSequenceKey.value
    await refreshNavigation()
    if (subStationId && isLeaf(subStationId)) {
      await selectSubStation(subStationId, bindingId, sequenceSourceKey)
    }
  }

  async function refreshSelectedGraph() {
    const bindingId = selectedBindingId.value
    if (bindingId) {
      await selectBinding(bindingId, selectedSequenceKey.value)
    }
  }

  async function clearSelection() {
    selectionGeneration++
    selectedSubStationId.value = null
    clearContent()
    contentLoading.value = false
    // 2026-10-09 检查清单 #11：清空选择要撤掉预热——服务端预热集合独立于订阅，
    // Unsubscribe 不会释放它，不撤的话旧工位会持续轮询到下一次成功预热或连接关闭。
    // warm 内部已串行化（last-write-wins），迟到的旧预热不会覆盖这次撤销。
    void live.warm([])
    await live.disconnectAndClear()
  }

  async function dispose() {
    navigationGeneration++
    await clearSelection()
    // clearSelection 只退订阅（连接是页面级资源），页面卸载才真正关掉它。
    await live.dispose()
  }

  async function loadAssets(subStationId: EntityId, generation: number) {
    const result: LineUpGraphCatalogAsset[] = []
    let page = 1
    let total = 0
    do {
      const response = await catalogGateway.getAssets({ subStationId, page, pageSize: 100 })
      if (!isCurrent(generation, subStationId)) {
        return []
      }
      result.push(...response.items)
      total = response.total
      page++
    }
    while (result.length < total)
    return result.filter(asset => asset.bindingId && asset.publishedRevisionId)
  }

  async function loadBindingPreview(bindingId: EntityId, preferredSequenceKey: string, generation: number) {
    const subStationId = selectedSubStationId.value
    const binding = bindings.value.find(item => item.bindingId === bindingId)
    const asset = assets.value.find(item => item.bindingId === bindingId)
    if (!subStationId || !binding || !asset?.publishedRevisionId || !isCurrent(generation, subStationId)) {
      return
    }
    selectedBindingId.value = bindingId
    const publication = await catalogGateway.previewPublish(bindingId, asset.publishedRevisionId)
    if (!isCurrent(generation, subStationId) || selectedBindingId.value !== bindingId) {
      return
    }
    preview.value = publication.preview
    targetRevisionId.value = publication.targetRevisionId
    publishedRevisionId.value = publication.publishedRevisionId ?? ''
    selectedSequenceKey.value = publication.preview.sequences.some(
      sequence => sequence.sourceKey === preferredSequenceKey,
    )
      ? preferredSequenceKey
      : publication.preview.sequences[0]?.sourceKey ?? ''
    // 2026-09-23 图结构到手就撤掉整页遮罩：实时连接（握手 + 订阅 + 首帧快照，冷读时要等 PLC）
    // 在后台继续，状态条显示「连接中」，值到了再上色。以前遮罩要等到连接完成才撤，看上去像卡住。
    contentLoading.value = false
    await connectLive(binding, selectedSequenceKey.value)
  }

  async function connectLive(binding: LineUpMonitorBindingSummary, sequenceSourceKey: string) {
    if (binding.bindingStatus !== 'Resolved' || !binding.runtimeBindingReady
      || !binding.monitoringEnabled || !sequenceSourceKey || !targetRevisionId.value) {
      // 这个绑定不可监控：调用方已不再先行断开，这里必须保证旧订阅不残留。
      await live.disconnectAndClear()
      return
    }
    await live.connect({
      bindingId: binding.bindingId,
      publishedRevisionId: targetRevisionId.value,
      stationId: binding.stationId,
      sequenceSourceKey,
    })
  }

  function isLeaf(subStationId: EntityId) {
    return navigationNodes.value.some(node => (
      node.nodeType === 'SubStation' && node.entityId === subStationId
    ))
  }

  function isCurrent(generation: number, subStationId: EntityId) {
    return generation === selectionGeneration && selectedSubStationId.value === subStationId
  }

  function clearContent() {
    bindings.value = []
    assets.value = []
    selectedBindingId.value = ''
    preview.value = null
    targetRevisionId.value = ''
    publishedRevisionId.value = ''
    selectedSequenceKey.value = ''
    safeErrorCode.value = ''
  }

  return {
    navigationNodes,
    selectedSubStationId,
    bindings,
    selectedBinding,
    assets,
    selectedAsset,
    preview,
    sequences,
    selectedSequence,
    selectedSequenceKey,
    targetRevisionId,
    publishedRevisionId,
    selectedPath,
    navigationLoading,
    contentLoading,
    safeErrorCode,
    initializeRoute,
    refreshNavigation,
    selectSubStation,
    selectBinding,
    selectSequence,
    refresh,
    refreshSelectedGraph,
    clearSelection,
    dispose,
  }
}

function clean(value: unknown) {
  return String(value ?? '').trim()
}

function sameCode(left: string, right: string) {
  return left.trim().toLowerCase() === right.trim().toLowerCase()
}

function errorCode(error: unknown, fallback: string) {
  return error instanceof Error && /^[a-z][a-z0-9]{2,80}$/i.test(error.message)
    ? error.message
    : fallback
}
