import type { TiaGraphViewState } from '../components/tiaGraphLayout'
import type {
  EntityId,
  LineUpDisplayNameChange,
  LineUpHierarchyDecision,
  LineUpImportCommitRequest,
  LineUpImportGateway,
  LineUpImportPreview,
  LineUpImportProgress,
} from '@/api/modules/LineUp/types'
import { computed, ref } from 'vue'
import { lineUpImportGateway } from '@/api/modules/LineUp/import'
import { normalizeBindingStatus, normalizeIssueSeverity } from '@/api/modules/LineUp/types'
import pack from './components/lang'

// —— 非 vue 模块的 i18n 桥（组件外无 useI18n 上下文）——
// 优先动态接入全局 i18n 实例（@/locales 具名导出 i18n，legacy:false 下 i18n.global.t 可用），
// 并把本视图语言包幂等合并进全局消息（页面 setup 的 mergeLocaleMessage 合并同一份数据，互不冲突）；
// 动态导入在无 vite 插件链的环境（vitest）无法解析 @/locales 的虚拟模块，捕获后回退本地四语字典：
// 语言探测顺序 = localStorage 'userPreferences'（fantastic-admin 本地偏好，storageTo=local 时写入）
// → navigator.language → zh-cn，与全局 fallbackLocale 一致（测试环境因此稳定拿到 zh-cn 文案）。
type I18nBridgeParams = Record<string, string | number>
type I18nBridgeTranslate = (key: string, params?: I18nBridgeParams) => string

let globalTranslate: I18nBridgeTranslate | undefined

void import('@/locales').then((mod) => {
  // vue-i18n Composer 的泛型重载与四语字典结构不完全吻合，这里按桥签名收窄（运行时语义不变）
  const bridge = mod.i18n.global as unknown as {
    t: I18nBridgeTranslate
    mergeLocaleMessage: (locale: string, message: Record<string, unknown>) => void
  }
  for (const locale of Object.keys(pack)) {
    bridge.mergeLocaleMessage(locale, pack[locale])
  }
  globalTranslate = bridge.t
}).catch(() => {
  // vitest 等未挂 vite 插件链的环境：保持本地字典回退
})

function detectBridgeLocale(): string {
  try {
    if (typeof localStorage !== 'undefined') {
      const raw = localStorage.getItem('userPreferences')
      const parsed = raw ? JSON.parse(raw) as Record<string, { app?: { defaultLang?: string } } | undefined> : null
      if (parsed && typeof parsed === 'object') {
        for (const profile of Object.values(parsed)) {
          const lang = profile?.app?.defaultLang
          if (typeof lang === 'string' && pack[lang]) {
            return lang
          }
        }
      }
    }
  }
  catch {
    // 本地偏好读取失败（JSON 损坏等）：继续走浏览器语言探测
  }
  const nav = typeof navigator !== 'undefined' ? String(navigator.language ?? '').toLowerCase() : ''
  if (/^zh-(tw|hant|hk|mo)/.test(nav)) {
    return 'zh-tw'
  }
  if (nav.startsWith('zh')) {
    return 'zh-cn'
  }
  if (nav.startsWith('ja')) {
    return 'ja'
  }
  if (nav.startsWith('en')) {
    return 'en'
  }
  return 'zh-cn'
}

function localTranslate(key: string, params?: I18nBridgeParams): string {
  const table = pack[detectBridgeLocale()] ?? pack['zh-cn']
  let text = table.lineupWorkspace_form[key] ?? pack['zh-cn'].lineupWorkspace_form[key] ?? key
  if (params) {
    text = text.replace(/\{(\w+)\}/g, (match, name: string) => (
      params[name] === undefined ? match : String(params[name])
    ))
  }
  return text
}

function t(key: string, params?: I18nBridgeParams): string {
  if (globalTranslate) {
    return globalTranslate(`lineupWorkspace_form.${key}`, params)
  }
  return localTranslate(key, params)
}

export type LineUpSequenceViewState = TiaGraphViewState

interface WizardOptions {
  gateway?: LineUpImportGateway
  pollIntervalMs?: number
}

function safeErrorCode(error: unknown) {
  if (error instanceof Error && error.message) {
    return error.message
  }
  return 'LineUpRequestFailed'
}

export function createLineUpImportWizard(options: WizardOptions = {}) {
  const gateway = options.gateway ?? lineUpImportGateway
  const pollIntervalMs = options.pollIntervalMs ?? 1000
  const batchId = ref<EntityId>('')
  const progress = ref<LineUpImportProgress | null>(null)
  const preview = ref<LineUpImportPreview | null>(null)
  const receipt = ref<Awaited<ReturnType<LineUpImportGateway['commit']>> | null>(null)
  const errorCode = ref('')
  const isUploading = ref(false)
  const isPolling = ref(false)
  const isCommitting = ref(false)
  const selectedSequenceKey = ref('')
  const hierarchyDecisions = ref(new Map<string, string>())
  const displayNameChanges = ref(new Map<string, string>())
  const sequenceViews = ref(new Map<string, LineUpSequenceViewState>())
  let pollingTimer: ReturnType<typeof setTimeout> | null = null
  let pollingGeneration = 0

  const sequences = computed(() => preview.value?.documents.flatMap(document => document.sequences) ?? [])
  const displaySequences = computed(() => sequences.value.map(sequence => ({
    ...sequence,
    displayName: displayNameChanges.value.get(sequence.sourceKey) ?? sequence.displayName,
    nodes: sequence.nodes.map(node => ({
      ...node,
      displayName: displayNameChanges.value.get(node.sourceKey) ?? node.displayName,
    })),
  })))
  const allIssues = computed(() => {
    if (!preview.value) {
      return []
    }
    return [
      ...preview.value.issues,
      ...preview.value.documents.flatMap(document => [
        ...document.issues,
        ...document.address.issues,
        ...document.hierarchy.issues,
      ]),
    ]
  })
  const hasBlockingIssues = computed(() =>
    allIssues.value.some(issue => normalizeIssueSeverity(issue.severity) === 'Blocking'),
  )
  const allHierarchyDecisionsComplete = computed(() => {
    if (sequences.value.length === 0) {
      return false
    }
    return sequences.value.every(sequence => Boolean(hierarchyDecisions.value.get(sequence.sourceKey)))
  })
  // 预览期的就绪口径：实例 DB 已被 callsite 证明（IGW 读回验证在 commit 的 Validate 环节执行）
  const allRuntimeBindingsResolved = computed(() => {
    if (!preview.value) {
      return false
    }
    return preview.value.documents.length > 0
      && preview.value.documents.every(document => Boolean(document.address?.instanceDbBlockId))
  })
  // 批次是一次性消费的：已提交（Committed）的批次再点保存只会得到 BatchAlreadyCommitted，
  // 结构有变化应「重新生成预览」产生新批次后再改派/保存
  const batchCommitted = computed(() => preview.value?.status === 'Committed')
  const canCommitStructure = computed(() =>
    preview.value?.canCommitStructure === true
    && !hasBlockingIssues.value
    && allHierarchyDecisionsComplete.value
    && !batchCommitted.value,
  )
  const canCommitForMonitoring = computed(() =>
    preview.value?.canCommitForMonitoring === true
    && canCommitStructure.value
    && allRuntimeBindingsResolved.value,
  )
  const runtimeStatusText = computed(() => {
    if (!preview.value) {
      return t('runtimeWaitGraphParse')
    }
    if (batchCommitted.value) {
      return t('runtimeBatchCommitted')
    }
    if (hasBlockingIssues.value) {
      return t('runtimeBlockingIssues')
    }
    if (canCommitForMonitoring.value) {
      return t('runtimeAddressConfirmed')
    }
    // 复用 GraphPreviewPanel 的 defaultBindingStatus（同文案同语义）
    return t('defaultBindingStatus')
  })
  const currentSequenceView = computed(() =>
    sequenceViews.value.get(selectedSequenceKey.value) ?? null,
  )

  function stopPolling() {
    pollingGeneration += 1
    if (pollingTimer) {
      clearTimeout(pollingTimer)
    }
    pollingTimer = null
    isPolling.value = false
  }

  function acceptPreview(value: LineUpImportPreview) {
    const isNewBatch = batchId.value !== value.batchId
    stopPolling()
    if (isNewBatch) {
      displayNameChanges.value = new Map()
      sequenceViews.value = new Map()
      receipt.value = null
    }
    preview.value = value
    progress.value = {
      batchId: value.batchId,
      status: value.status,
      errorCode: null,
      previewRevision: value.previewRevision,
      canLoadPreview: true,
    }
    batchId.value = value.batchId
    // 预览就绪即重建节点→BOM→点位图关联快照（AI_LineUp_GraphNodeBom），失败静默不影响预览
    if (gateway.rebuildGraphNodeBom) {
      void gateway.rebuildGraphNodeBom(value.batchId).catch(() => {})
    }
    errorCode.value = ''
    hierarchyDecisions.value = new Map()
    for (const document of value.documents) {
      for (const resolution of document.hierarchy.sequences) {
        if (resolution.selectedCandidateKey) {
          hierarchyDecisions.value.set(resolution.sequenceSourceKey, resolution.selectedCandidateKey)
        }
      }
    }
    const availableKeys = new Set(value.documents.flatMap(document => document.sequences.map(item => item.sourceKey)))
    if (!availableKeys.has(selectedSequenceKey.value)) {
      selectedSequenceKey.value = value.documents[0]?.sequences[0]?.sourceKey ?? ''
    }
  }

  async function pollOnce(generation: number) {
    if (generation !== pollingGeneration || !batchId.value) {
      return
    }
    try {
      const value = await gateway.getProgress(batchId.value)
      if (generation !== pollingGeneration) {
        return
      }
      progress.value = value
      if (value.status === 'Failed') {
        errorCode.value = value.errorCode || 'LineUpPreviewFailed'
        stopPolling()
        return
      }
      if (value.canLoadPreview && value.status === 'PreviewReady') {
        acceptPreview(await gateway.getPreview(batchId.value))
        return
      }
      if (value.status === 'Committed') {
        stopPolling()
        return
      }
      pollingTimer = setTimeout(() => void pollOnce(generation), pollIntervalMs)
    }
    catch (error) {
      if (generation !== pollingGeneration) {
        return
      }
      errorCode.value = safeErrorCode(error)
      stopPolling()
    }
  }

  function startPolling(value: EntityId) {
    stopPolling()
    batchId.value = value
    pollingGeneration += 1
    const generation = pollingGeneration
    isPolling.value = true
    void pollOnce(generation)
  }

  /** 加载已存在的可预览批次（「查看流程图」跳转直达最新预览），成功后进入 step2 */
  async function restoreExistingPreview(targetBatchId: EntityId) {
    stopPolling()
    preview.value = null
    receipt.value = null
    errorCode.value = ''
    isUploading.value = false
    batchId.value = targetBatchId
    progress.value = await gateway.getProgress(targetBatchId)
    acceptPreview(await gateway.getPreview(targetBatchId))
  }

  async function startPreview(file: File, lineId: EntityId, deviceId: EntityId) {
    stopPolling()
    preview.value = null
    receipt.value = null
    errorCode.value = ''
    isUploading.value = true
    try {
      const started = await gateway.startPreview(file, lineId, deviceId)
      batchId.value = started.batchId
      progress.value = {
        batchId: started.batchId,
        status: started.status,
        errorCode: null,
        previewRevision: 0,
        canLoadPreview: false,
      }
      startPolling(started.batchId)
      return started
    }
    catch (error) {
      errorCode.value = safeErrorCode(error)
      throw error
    }
    finally {
      isUploading.value = false
    }
  }

  /** 从 PLC GRAPH 块免上传生成预览（graph 结构管理页弹框入口） */
  async function startPreviewFromBlock(blockId: EntityId, lineId: EntityId, deviceId: EntityId) {
    if (!gateway.startPreviewFromBlock) {
      throw new Error('LineUpPreviewFromBlockUnsupported')
    }
    stopPolling()
    preview.value = null
    receipt.value = null
    errorCode.value = ''
    isUploading.value = true
    try {
      const started = await gateway.startPreviewFromBlock(blockId, lineId, deviceId)
      batchId.value = started.batchId
      progress.value = {
        batchId: started.batchId,
        status: started.status,
        errorCode: null,
        previewRevision: 0,
        canLoadPreview: false,
      }
      startPolling(started.batchId)
      return started
    }
    catch (error) {
      errorCode.value = safeErrorCode(error)
      throw error
    }
    finally {
      isUploading.value = false
    }
  }

  function selectHierarchyCandidate(sequenceSourceKey: string, candidateKey: string) {
    hierarchyDecisions.value.set(sequenceSourceKey, candidateKey)
    hierarchyDecisions.value = new Map(hierarchyDecisions.value)
  }

  function rename(sourceKey: string, displayName: string) {
    const value = displayName.trim()
    const original = sequences.value.find(item => item.sourceKey === sourceKey)?.displayName ?? ''
    if (!value || value === original) {
      displayNameChanges.value.delete(sourceKey)
    }
    else {
      displayNameChanges.value.set(sourceKey, value)
    }
    displayNameChanges.value = new Map(displayNameChanges.value)
  }

  function buildCommitRequest(): LineUpImportCommitRequest {
    if (!preview.value) {
      throw new Error('PreviewNotReady')
    }
    const decisions: LineUpHierarchyDecision[] = [...hierarchyDecisions.value]
      .map(([sequenceSourceKey, candidateKey]) => ({ sequenceSourceKey, candidateKey }))
      .sort((left, right) => left.sequenceSourceKey.localeCompare(right.sequenceSourceKey))
    const names: LineUpDisplayNameChange[] = [...displayNameChanges.value]
      .map(([sourceKey, displayName]) => ({ sourceKey, displayName }))
      .sort((left, right) => left.sourceKey.localeCompare(right.sourceKey))
    return {
      commitMode: 'StructureOnly',
      previewRevision: preview.value.previewRevision,
      previewHash: preview.value.previewHash,
      diffToken: preview.value.diffToken,
      hierarchyDecisions: decisions,
      displayNameChanges: names,
    }
  }

  async function commitStructure() {
    if (!batchId.value || !canCommitStructure.value) {
      throw new Error('GraphStructureBlocked')
    }
    isCommitting.value = true
    errorCode.value = ''
    try {
      receipt.value = await gateway.commit(batchId.value, buildCommitRequest())
      return receipt.value
    }
    catch (error) {
      errorCode.value = safeErrorCode(error)
      throw error
    }
    finally {
      isCommitting.value = false
    }
  }

  /** 保存结构并启用实时监控：commit(ReadyForMonitoring) → 后端编排 绑定→发布→Prepare→Validate→SetMonitoring */
  async function commitForMonitoring() {
    if (!batchId.value || !canCommitForMonitoring.value) {
      throw new Error('RuntimeBindingUnresolved')
    }
    isCommitting.value = true
    errorCode.value = ''
    try {
      const request = { ...buildCommitRequest(), commitMode: 'ReadyForMonitoring' as const }
      receipt.value = await gateway.commit(batchId.value, request)
      return receipt.value
    }
    catch (error) {
      errorCode.value = safeErrorCode(error)
      throw error
    }
    finally {
      isCommitting.value = false
    }
  }

  /** 改派（HierarchyReassign）落库后刷新本地预览：预览快照不回写绑定，
   * 但读接口会用绑定表动态合并层级归属（MergeCurrentHierarchyBindingsAsync），
   * 重拉一次即可让面板/弹框立刻看到最新绑定（顺控行编码、BOM 上下文同步更新）。 */
  async function refreshPreviewFromServer() {
    if (!batchId.value) {
      return
    }
    acceptPreview(await gateway.getPreview(batchId.value))
  }

  function selectSequence(sourceKey: string) {
    selectedSequenceKey.value = sourceKey
  }

  function updateSequenceView(sourceKey: string, state: LineUpSequenceViewState) {
    sequenceViews.value.set(sourceKey, { ...state })
    sequenceViews.value = new Map(sequenceViews.value)
  }

  return {
    batchId,
    progress,
    preview,
    receipt,
    errorCode,
    isUploading,
    isPolling,
    isCommitting,
    startPreviewFromBlock,
    restoreExistingPreview,
    selectedSequenceKey,
    sequences,
    displaySequences,
    allIssues,
    hasBlockingIssues,
    canCommitStructure,
    canCommitForMonitoring,
    runtimeStatusText,
    currentSequenceView,
    hierarchyDecisions,
    displayNameChanges,
    startPreview,
    startPolling,
    stopPolling,
    acceptPreview,
    refreshPreviewFromServer,
    selectHierarchyCandidate,
    rename,
    buildCommitRequest,
    commitStructure,
    commitForMonitoring,
    selectSequence,
    updateSequenceView,
    dispose: stopPolling,
  }
}
