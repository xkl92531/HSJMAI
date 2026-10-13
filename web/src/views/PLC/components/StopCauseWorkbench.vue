<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import StopCauseDiagnosisAPI from '@/api/modules/PLC/stopCauseDiagnosis'
import LogicUnitAPI from '@/api/modules/PLC/logic'
import { useI18n } from 'vue-i18n'
import messages from './stopCauseWorkbench.lang'

const props = defineProps<{
  projectId: string | number
}>()

const { t, mergeLocaleMessage } = useI18n()

// 遍历语言包并合并到当前实例
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

type GenericRecord = Record<string, any>
type WorkbenchCache = {
  activeTab: string
  controlForm: GenericRecord
  semanticForm: GenericRecord
  writerSearch: GenericRecord
  writerPagination: GenericRecord
  compileSearch: GenericRecord
  compilePagination: GenericRecord
  writerFragments: GenericRecord[]
  extractResult: GenericRecord | null
  stationMapResult: GenericRecord | null
  compileResult: GenericRecord | null
  semanticResult: GenericRecord | null
  savedAt: string
}

const cacheKey = computed(() => `stop-cause-workbench:${props.projectId}`)

const loading = ref(false)
const writerLoading = ref(false)
const activeTab = ref('station-map')

const controlForm = ref({
  stationCode: '',
  blockId: undefined as number | undefined,
  useLlmAnnotation: true,
  includePhysicalTags: true,
  includeVariableGroups: true,
  includeLogicUnitMarking: true,
  includeSourceItems: true,
  maxDetailCountPerCategory: 200,
  includePhysicalAnchors: true,
  includeVirtualAnchors: true,
  includeHandshakeAnchors: false,
  includeAstSeeds: true,
  includeAstCompileResults: true,
  rawContentMaxLength: 0,
})

const semanticForm = ref({
  stationCode: '',
})

const writerSearch = ref({
  blockId: '',
  blockName: '',
  stationCode: '',
  networkId: undefined as number | undefined,
  title: '',
  unitType: '',
  eligibleOnly: true,
  minWriteVarsCount: 1,
  rawContentMaxLength: 200,
})

const compileSearch = ref({
  stationCode: '',
  blockName: '',
  variableName: '',
  compileStatus: '',
})

const writerPagination = ref({
  page: 1,
  limit: 10,
  total: 0,
  sortField: 'WriteVarsCount',
  sortType: 'desc',
})

const compilePagination = ref({
  page: 1,
  limit: 20,
  total: 0,
})

const compileQueryLoading = ref(false)
const compileQueryMode = ref(false)
const compileQueryRows = ref<GenericRecord[]>([])
const semanticRunning = ref(false)
const semanticProgress = ref<GenericRecord | null>(null)
const semanticProgressKey = ref('')
const persistedSemanticRows = ref<GenericRecord[]>([])
const persistedSemanticTotal = ref(0)
let semanticProgressTimer: number | null = null

// ── 一键分析入库进度 ──
const fullAnalysisTaskId = ref('')
const fullAnalysisProgress = ref<GenericRecord | null>(null)
const fullAnalysisDialogVisible = ref(false)
let fullAnalysisTimer: number | null = null

// ── 批量Q点提取 ──
const batchDiscoverRunning = ref(false)
const batchDiscoverDialogVisible = ref(false)
const batchDiscoverResult = ref<GenericRecord | null>(null)
const batchDiscoverProgress = ref<GenericRecord | null>(null)
const batchDiscoverTaskId = ref('')
let batchDiscoverTimer: number | null = null

const writerFragments = ref<GenericRecord[]>([])
const stationWriterAstDialogVisible = ref(false)
const stationWriterAstDialogLoading = ref(false)
const stationWriterAstContext = ref<GenericRecord>({
  stationCode: '',
  blockCount: 0,
  logicUnitCount: 0,
})
const stationWriterAstRows = ref<GenericRecord[]>([])
const stationWriterAstPagination = ref({
  page: 1,
  limit: 20,
  total: 0,
})

const extractResult = ref<GenericRecord | null>(null)
const stationMapResult = ref<GenericRecord | null>(null)
const compileResult = ref<GenericRecord | null>(null)
const semanticResult = ref<GenericRecord | null>(null)
const monitoringLoading = ref(false)
const monitoringForm = ref({
  stationCode: '',
  stationMode: '',
  runtimeNodeName: '',
  persistHistory: true,
  persistSnapshot: true,
  persistEvents: true,
  signalLimit: 100,
  timelineLimit: 50,
})
const monitoringStations = ref<GenericRecord[]>([])
const monitoringOverview = ref<GenericRecord | null>(null)
const monitoringSignals = ref<GenericRecord[]>([])
const monitoringChanges = ref<GenericRecord[]>([])
const monitoringBundles = ref<GenericRecord[]>([])
const monitoringEvents = ref<GenericRecord[]>([])
const monitoringSnapshot = ref<GenericRecord | null>(null)
const monitoringRunResult = ref<GenericRecord | null>(null)

// ── 覆盖度 ──
const coverageLoading = ref(false)
const coverageSearch = ref({ blockName: '', blockType: '', programmingLanguage: '' })
const coveragePagination = ref({ page: 1, limit: 50 })
const coverageStats = ref<GenericRecord | null>(null)
const coverageByType = ref<GenericRecord[]>([])
const coverageRows = ref<GenericRecord[]>([])
const coverageFilteredTotal = ref(0)

const readCache = () => {
  const raw = sessionStorage.getItem(cacheKey.value)
  if (!raw) {
    return null
  }

  try {
    return JSON.parse(raw) as WorkbenchCache
  }
  catch {
    sessionStorage.removeItem(cacheKey.value)
    return null
  }
}

const writeCache = () => {
  const payload: WorkbenchCache = {
    activeTab: activeTab.value,
    controlForm: { ...controlForm.value },
    semanticForm: { ...semanticForm.value },
    writerSearch: { ...writerSearch.value },
    writerPagination: { ...writerPagination.value },
    compileSearch: { ...compileSearch.value },
    compilePagination: { ...compilePagination.value },
    writerFragments: [...writerFragments.value],
    extractResult: extractResult.value,
    stationMapResult: stationMapResult.value,
    compileResult: compileResult.value,
    semanticResult: semanticResult.value,
    savedAt: new Date().toISOString(),
  }
  sessionStorage.setItem(cacheKey.value, JSON.stringify(payload))
}

const restoreCache = () => {
  const cache = readCache()
  if (!cache) {
    return
  }

  controlForm.value = {
    ...controlForm.value,
    ...cache.controlForm,
  }
  semanticForm.value = {
    ...semanticForm.value,
    ...(cache.semanticForm || {}),
  }
  writerSearch.value = {
    ...writerSearch.value,
    ...cache.writerSearch,
  }
  writerPagination.value = {
    ...writerPagination.value,
    ...cache.writerPagination,
  }
  compileSearch.value = {
    ...compileSearch.value,
    ...cache.compileSearch,
  }
  compilePagination.value = {
    ...compilePagination.value,
    ...cache.compilePagination,
  }
  activeTab.value = cache.activeTab || activeTab.value
  writerFragments.value = cache.writerFragments || []
  extractResult.value = cache.extractResult
  stationMapResult.value = cache.stationMapResult
  compileResult.value = cache.compileResult
  semanticResult.value = cache.semanticResult
}

const clearCache = () => {
  sessionStorage.removeItem(cacheKey.value)
  writerFragments.value = []
  writerPagination.value.total = 0
  extractResult.value = null
  stationMapResult.value = null
  compileResult.value = null
  semanticResult.value = null
  persistedSemanticRows.value = []
  persistedSemanticTotal.value = 0
  semanticProgress.value = null
  semanticProgressKey.value = ''
  semanticForm.value = {
    stationCode: '',
  }
  activeTab.value = 'station-map'
  ElMessage.success(t('stopCauseWorkbench.cacheCleared'))
}

const getPayload = (response: GenericRecord | null | undefined) => {
  if (!response) {
    return null
  }
  return response.data ?? response.backResult ?? response
}

const buildStationExtractionRequest = () => ({
  projectId: props.projectId,
  stationCode: controlForm.value.stationCode || undefined,
  blockId: controlForm.value.blockId,
  useLlmAnnotation: controlForm.value.useLlmAnnotation,
  includePhysicalTags: controlForm.value.includePhysicalTags,
  includeVariableGroups: controlForm.value.includeVariableGroups,
  includeLogicUnitMarking: controlForm.value.includeLogicUnitMarking,
  includeSourceItems: controlForm.value.includeSourceItems,
  maxDetailCountPerCategory: controlForm.value.maxDetailCountPerCategory,
})

const buildPersistedStationExtractionRequest = () => ({
  ...buildStationExtractionRequest(),
  persistStationMarking: true,
  updateBlockStationCode: true,
})

const buildStationMapRequest = () => ({
  projectId: props.projectId,
  stationCode: controlForm.value.stationCode || undefined,
  blockId: controlForm.value.blockId,
  useLlmAnnotation: controlForm.value.useLlmAnnotation,
  includePhysicalAnchors: controlForm.value.includePhysicalAnchors,
  includeVirtualAnchors: false,
  includeHandshakeAnchors: false,
  includeAstSeeds: false,
  includeAstCompileResults: false,
  includeStationColoring: false,
  persistStationMap: false,
  rawContentMaxLength: controlForm.value.rawContentMaxLength,
})

const buildAstCompileRequest = () => ({
  projectId: props.projectId,
  stationCode: controlForm.value.stationCode || undefined,
  blockId: controlForm.value.blockId,
  useLlmAnnotation: controlForm.value.useLlmAnnotation,
  includePhysicalAnchors: controlForm.value.includePhysicalAnchors,
  includeVirtualAnchors: false,
  includeHandshakeAnchors: false,
  includeAstSeeds: controlForm.value.includeAstSeeds,
  includeAstCompileResults: controlForm.value.includeAstCompileResults,
  includeStationColoring: false,
  persistStationMap: false,
  persistAstResults: false,
  rawContentMaxLength: controlForm.value.rawContentMaxLength,
})

const buildSemanticCompileRequest = () => ({
  projectId: props.projectId,
  stationCode: semanticForm.value.stationCode || undefined,
  blockId: controlForm.value.blockId,
  useLlmAnnotation: controlForm.value.useLlmAnnotation,
  includePhysicalAnchors: controlForm.value.includePhysicalAnchors,
  includeVirtualAnchors: controlForm.value.includeVirtualAnchors,
  includeHandshakeAnchors: controlForm.value.includeHandshakeAnchors,
  includeAstSeeds: controlForm.value.includeAstSeeds,
  includeAstCompileResults: controlForm.value.includeAstCompileResults,
  includeStationColoring: false,
  persistStationMap: true,
  persistAstResults: true,
  semanticProgressKey: semanticProgressKey.value || undefined,
  rawContentMaxLength: controlForm.value.rawContentMaxLength,
})

const buildPersistedCompileRequest = (persistStationMap = true) => ({
  ...buildAstCompileRequest(),
  includePhysicalAnchors: true,
  includeVirtualAnchors: false,
  includeHandshakeAnchors: false,
  includeStationColoring: persistStationMap,
  persistStationMap,
  persistAstResults: true,
})

const buildPersistedCompileQueryRequest = () => ({
  projectId: props.projectId,
  stationCode: compileSearch.value.stationCode || controlForm.value.stationCode || undefined,
  blockId: controlForm.value.blockId,
  blockName: compileSearch.value.blockName || undefined,
  variableName: compileSearch.value.variableName || undefined,
  compileStatus: compileSearch.value.compileStatus || undefined,
  page: compilePagination.value.page,
  limit: compilePagination.value.limit,
})

const buildPersistedSemanticQueryRequest = () => ({
  projectId: props.projectId,
  stationCode: semanticForm.value.stationCode || undefined,
  blockId: controlForm.value.blockId,
  page: 1,
  limit: 500,
})

const buildPersistedStationMapQueryRequest = () => ({
  projectId: props.projectId,
  stationCode: controlForm.value.stationCode || undefined,
})

const buildWriterFragmentRequest = () => ({
  ProjectId: props.projectId,
  BlockId: writerSearch.value.blockId || undefined,
  BlockName: writerSearch.value.blockName || undefined,
  StationCode: writerSearch.value.stationCode || undefined,
  NetworkId: writerSearch.value.networkId,
  Title: writerSearch.value.title || undefined,
  UnitType: writerSearch.value.unitType || undefined,
  EligibleOnly: writerSearch.value.eligibleOnly,
  MinWriteVarsCount: writerSearch.value.minWriteVarsCount,
  RawContentMaxLength: writerSearch.value.rawContentMaxLength,
})

const callApi = async (action: () => Promise<any>, successMessage: string) => {
  const response = await action()
  if (response?.isSuccess === false) {
    throw new Error(response.errorMessage || t('stopCauseWorkbench.requestFailed'))
  }
  ElMessage.success(successMessage)
  return getPayload(response)
}

const runSemanticQPointExtraction = async () => {
  if (!semanticForm.value.stationCode) {
    ElMessage.warning(t('stopCauseWorkbench.semanticStationRequired'))
    return
  }

  if (!controlForm.value.includeVirtualAnchors && !controlForm.value.includeHandshakeAnchors) {
    ElMessage.warning(t('stopCauseWorkbench.enableVirtualOrHandshake'))
    return
  }

  semanticRunning.value = true
  semanticProgressKey.value = `ast-semantic:${props.projectId}:${semanticForm.value.stationCode}:${Date.now()}`
  semanticProgress.value = {
    key: semanticProgressKey.value,
    status: 'Running',
    totalCandidates: 0,
    completedCandidates: 0,
    totalBatches: 0,
    completedBatches: 0,
    foundCount: 0,
    message: t('stopCauseWorkbench.semanticStarting'),
  }
  startSemanticProgressPolling()
  try {
    semanticResult.value = await callApi(
      () => StopCauseDiagnosisAPI.compilePreview(buildSemanticCompileRequest()),
      t('stopCauseWorkbench.semanticDoneForStation', { station: semanticForm.value.stationCode }),
    )
    await pollSemanticProgressOnce()
    await loadPersistedSemanticRows()
    activeTab.value = 'qpoints'
  }
  catch (error: any) {
    ElMessage.error(error.message || t('stopCauseWorkbench.semanticFailed'))
  }
  finally {
    await pollSemanticProgressOnce()
    stopSemanticProgressPolling()
    semanticRunning.value = false
  }
}

const runBatchQPointDiscovery = async () => {
  if (!controlForm.value.includeVirtualAnchors && !controlForm.value.includeHandshakeAnchors) {
    ElMessage.warning(t('stopCauseWorkbench.enableVirtualOrHandshake'))
    return
  }

  try {
    await ElMessageBox.confirm(
      t('stopCauseWorkbench.batchDiscoverConfirm'),
      t('stopCauseWorkbench.batchDiscoverTitle'),
      { confirmButtonText: t('stopCauseWorkbench.startExtract'), cancelButtonText: t('common.cancel'), type: 'info' },
    )
  }
  catch {
    return
  }

  batchDiscoverRunning.value = true
  batchDiscoverResult.value = null
  batchDiscoverProgress.value = null
  batchDiscoverDialogVisible.value = true

  try {
    const response = await StopCauseDiagnosisAPI.batchDiscover({
      projectId: props.projectId,
      stationCodes: [],
      maxParallelStations: 2,
      includePhysicalAnchors: controlForm.value.includePhysicalAnchors,
      includeVirtualAnchors: controlForm.value.includeVirtualAnchors,
      includeHandshakeAnchors: controlForm.value.includeHandshakeAnchors,
      includeAstSeeds: controlForm.value.includeAstSeeds,
      includeAstCompileResults: controlForm.value.includeAstCompileResults,
      useLlmAnnotation: controlForm.value.useLlmAnnotation,
      persistStationMap: true,
      persistAstResults: true,
      rawContentMaxLength: controlForm.value.rawContentMaxLength,
    }) as any

    const payload = response?.backResult ?? response?.BackResult ?? response
    const taskId = payload?.taskId ?? payload?.TaskId
    if (!taskId) {
      ElMessage.error(payload?.errorMessage ?? payload?.ErrorMessage ?? t('stopCauseWorkbench.batchDiscoverStartFailed'))
      batchDiscoverRunning.value = false
      return
    }

    batchDiscoverTaskId.value = taskId
    batchDiscoverProgress.value = {
      taskId,
      status: 'Running',
      currentStep: 0,
      totalSteps: 2,
      stepName: t('stopCauseWorkbench.initializing'),
      message: t('stopCauseWorkbench.taskSubmitted'),
      percentage: 0,
    }
    startBatchDiscoverPolling()
  }
  catch (error: any) {
    ElMessage.error(error.message || t('stopCauseWorkbench.batchDiscoverStartFailed'))
    batchDiscoverRunning.value = false
  }
}

const startBatchDiscoverPolling = () => {
  stopBatchDiscoverPolling()
  void pollBatchDiscoverOnce()
  batchDiscoverTimer = window.setInterval(() => {
    void pollBatchDiscoverOnce()
  }, 1500)
}

const stopBatchDiscoverPolling = () => {
  if (batchDiscoverTimer !== null) {
    window.clearInterval(batchDiscoverTimer)
    batchDiscoverTimer = null
  }
}

const pollBatchDiscoverOnce = async () => {
  if (!batchDiscoverTaskId.value) return
  try {
    const response = await StopCauseDiagnosisAPI.getFullAnalysisProgress({
      taskId: batchDiscoverTaskId.value,
    }) as any
    batchDiscoverProgress.value = response ?? null

    const status = String(response?.status ?? response?.Status ?? '').toLowerCase()
    if (status === 'completed') {
      stopBatchDiscoverPolling()
      batchDiscoverRunning.value = false
      const summary = response?.summary ?? response?.Summary
      const stationCount = summary?.stationCount ?? summary?.StationCount ?? '?'
      const anchorCount = summary?.anchorBindingCount ?? summary?.AnchorBindingCount ?? '?'
      ElMessage.success(t('stopCauseWorkbench.batchDiscoverCompleted', { stations: stationCount, anchors: anchorCount }))
      await loadPersistedSemanticRows()
      activeTab.value = 'qpoints'
    }
    else if (status === 'failed') {
      stopBatchDiscoverPolling()
      batchDiscoverRunning.value = false
      ElMessage.error(response?.errorMessage ?? response?.ErrorMessage ?? t('stopCauseWorkbench.batchDiscoverFailed'))
    }
  }
  catch {
    // 轮询中网络错误不中断
  }
}

const loadWriterFragments = async () => {
  writerLoading.value = true
  try {
    const response = await LogicUnitAPI.getWriterFragmentsPaged({
      page: writerPagination.value.page,
      limit: writerPagination.value.limit,
      sortField: writerPagination.value.sortField,
      sortType: writerPagination.value.sortType,
      ...buildWriterFragmentRequest(),
    }) as any

    if (response?.data) {
      writerFragments.value = response.data
      writerPagination.value.total = response.total || 0
      activeTab.value = 'writers'
      return
    }

    writerFragments.value = []
    writerPagination.value.total = 0
  }
  catch (error: any) {
    writerFragments.value = []
    writerPagination.value.total = 0
    ElMessage.error(error.message || t('stopCauseWorkbench.writersLoadFailed'))
  }
  finally {
    writerLoading.value = false
  }
}

const parseJsonSafe = (text: string | null | undefined) => {
  if (!text) {
    return null
  }

  try {
    return JSON.parse(text)
  }
  catch {
    return null
  }
}

const ensureArray = <T = GenericRecord>(value: any): T[] => Array.isArray(value) ? value : []

const normalizeStringArray = (value: any): string[] => ensureArray(value)
  .map(item => String(item ?? '').trim())
  .filter(Boolean)

const normalizeAstNode = (value: any): GenericRecord | null => {
  const source = typeof value === 'string' ? parseJsonSafe(value) : value
  if (!source || typeof source !== 'object') {
    return null
  }

  return {
    ...source,
    nodeType: source.nodeType ?? source.NodeType ?? '',
    displayName: source.displayName ?? source.DisplayName ?? source.variableName ?? source.VariableName ?? '',
    variableName: source.variableName ?? source.VariableName ?? '',
    metadata: source.metadata ?? source.Metadata ?? {},
    children: ensureArray(source.children ?? source.Children).map(child => normalizeAstNode(child)).filter(Boolean),
  }
}

const normalizeCoverageReport = (value: any): GenericRecord => ({
  ...value,
  logicUnitId: value?.logicUnitId ?? value?.LogicUnitId,
  networkId: value?.networkId ?? value?.NetworkId,
  blockName: value?.blockName ?? value?.BlockName ?? '',
  unitType: value?.unitType ?? value?.UnitType ?? '',
  sourceType: value?.sourceType ?? value?.SourceType ?? '',
  title: value?.title ?? value?.Title ?? '',
  inputVariables: normalizeStringArray(value?.inputVariables ?? value?.InputVariables),
  outputVariables: normalizeStringArray(value?.outputVariables ?? value?.OutputVariables),
  rawContentExcerpt: value?.rawContentExcerpt ?? value?.RawContentExcerpt ?? '',
  unsupportedReason: value?.unsupportedReason ?? value?.UnsupportedReason ?? '',
})

const normalizeCompileRow = (item: GenericRecord) => {
  const rootNode = normalizeAstNode(item.rootNode ?? item.RootNode ?? item.rootNodeJson ?? item.RootNodeJson)
  const coverageSource = item.coverageReports ?? item.CoverageReports ?? parseJsonSafe(item.coverageReportsJson || item.CoverageReportsJson) ?? []
  const coverageReports = ensureArray(coverageSource).map(normalizeCoverageReport)
  const blockNames = [...new Set(coverageReports.map(report => report.blockName).filter(Boolean))]
  const metadata = rootNode?.metadata || {}

  return {
    ...item,
    variableName: rootNode?.variableName || (item.outputVariables || item.OutputVariables || [])[0] || item.variableName || item.VariableName || '',
    anchorType: metadata.AnchorType || metadata.anchorType || '-',
    stationCode: item.stationCode || item.StationCode || '',
    blockNameSummary: blockNames.join(', ') || item.blockName || item.BlockName || '',
    compileStatus: item.compileStatus || item.CompileStatus || '',
    compilerName: item.compilerName || item.CompilerName || '',
    coverageReportCount: coverageReports.length,
    coverageReports,
    rootNode,
    messages: normalizeStringArray(item.messages || item.Messages),
    logicUnitId: item.logicUnitId || item.LogicUnitId,
    programmingLanguage: item.programmingLanguage || item.ProgrammingLanguage,
    networkId: item.networkId || item.NetworkId,
  }
}

const normalizeStationBucket = (item: GenericRecord): GenericRecord => ({
  ...item,
  stationCode: item.stationCode ?? item.StationCode ?? '',
  confidence: item.confidence ?? item.Confidence ?? 0,
  anchorCount: item.anchorCount ?? item.AnchorCount ?? 0,
  logicUnitCount: item.logicUnitCount ?? item.LogicUnitCount ?? 0,
  blockCount: item.blockCount ?? item.BlockCount ?? 0,
  variableNames: normalizeStringArray(item.variableNames ?? item.VariableNames),
  anchorKeys: normalizeStringArray(item.anchorKeys ?? item.AnchorKeys),
  evidence: normalizeStringArray(item.evidence ?? item.Evidence),
})

const normalizeAnchorAssignment = (item: GenericRecord): GenericRecord => ({
  ...item,
  anchorKey: item.anchorKey ?? item.AnchorKey ?? '',
  variableName: item.variableName ?? item.VariableName ?? '',
  anchorType: item.anchorType ?? item.AnchorType ?? '',
  stationCode: item.stationCode ?? item.StationCode ?? '',
  confidence: item.confidence ?? item.Confidence ?? 0,
  hasConflict: item.hasConflict ?? item.HasConflict ?? false,
  conflictingStations: normalizeStringArray(item.conflictingStations ?? item.ConflictingStations),
  evidence: normalizeStringArray(item.evidence ?? item.Evidence),
})

const normalizeStationConflict = (item: GenericRecord): GenericRecord => ({
  ...item,
  variableName: item.variableName ?? item.VariableName ?? '',
  anchorKeys: normalizeStringArray(item.anchorKeys ?? item.AnchorKeys),
  recommendedStation: item.recommendedStation ?? item.RecommendedStation ?? '',
  recommendedConfidence: item.recommendedConfidence ?? item.RecommendedConfidence ?? 0,
  candidateStations: normalizeStringArray(item.candidateStations ?? item.CandidateStations),
  reason: item.reason ?? item.Reason ?? '',
  evidence: normalizeStringArray(item.evidence ?? item.Evidence),
})

const normalizeOrphanAnchor = (item: GenericRecord): GenericRecord => ({
  ...item,
  anchorKey: item.anchorKey ?? item.AnchorKey ?? '',
  variableName: item.variableName ?? item.VariableName ?? '',
  anchorType: item.anchorType ?? item.AnchorType ?? '',
  reason: item.reason ?? item.Reason ?? '',
  evidence: normalizeStringArray(item.evidence ?? item.Evidence),
})

const normalizePersistedStationMapResponse = (raw: any): GenericRecord => ({
  ...(raw ?? {}),
  extractionSummary: raw?.extractionSummary ?? raw?.ExtractionSummary ?? {},
  stationMapCounts: raw?.stationMapCounts ?? raw?.StationMapCounts ?? {},
  stationBuckets: ensureArray(raw?.stationBuckets ?? raw?.StationBuckets).map(normalizeStationBucket),
  anchorAssignments: ensureArray(raw?.anchorAssignments ?? raw?.AnchorAssignments).map(normalizeAnchorAssignment),
  stationConflicts: ensureArray(raw?.stationConflicts ?? raw?.StationConflicts).map(normalizeStationConflict),
  orphanAnchors: ensureArray(raw?.orphanAnchors ?? raw?.OrphanAnchors).map(normalizeOrphanAnchor),
  variablePropagations: ensureArray(raw?.variablePropagations ?? raw?.VariablePropagations),
})

const summarizeRootNode = (rootNodeJson: string | null | undefined) => {
  const root = normalizeAstNode(rootNodeJson)
  if (!root) {
    return '-'
  }

  const nodeType = root.nodeType || '-'
  const displayName = root.displayName || root.variableName || '-'
  const childCount = ensureArray(root.children).length
  return `${nodeType} / ${displayName}${childCount > 0 ? ` / ${t('stopCauseWorkbench.childNodes', { n: childCount })}` : ''}`
}

const normalizePersistedCompileRow = (item: GenericRecord) => {
  return normalizeCompileRow(item)
}

const normalizeSemanticFinding = (item: GenericRecord): GenericRecord => ({
  ...item,
  logicUnitId: item.logicUnitId ?? item.LogicUnitId,
  blockId: item.blockId ?? item.BlockId,
  blockName: item.blockName ?? item.BlockName ?? '',
  stationCode: item.stationCode ?? item.StationCode ?? '',
  variableName: item.variableName ?? item.VariableName ?? '',
  category: item.category ?? item.Category ?? '',
  semanticLabel: item.semanticLabel ?? item.SemanticLabel ?? '',
  reason: item.reason ?? item.Reason ?? '',
  confidence: item.confidence ?? item.Confidence ?? 0,
  ioMapping: item.ioMapping ?? item.IoMapping ?? '',
  relatedIOTags: normalizeStringArray(item.relatedIOTags ?? item.RelatedIOTags),
})

const normalizeMonitoringStation = (item: GenericRecord): GenericRecord => ({
  ...item,
  projectId: item.projectId ?? item.ProjectId,
  stationCode: item.stationCode ?? item.StationCode ?? '',
  enabled: item.enabled ?? item.Enabled ?? false,
  status: item.status ?? item.Status ?? '',
  stationMode: item.stationMode ?? item.StationMode ?? '',
  isSilent: item.isSilent ?? item.IsSilent ?? false,
  silentSeconds: item.silentSeconds ?? item.SilentSeconds ?? 0,
  lastSampleTime: item.lastSampleTime ?? item.LastSampleTime ?? '',
  lastBundleTime: item.lastBundleTime ?? item.LastBundleTime ?? '',
  runtimeStatus: item.runtimeStatus ?? item.RuntimeStatus ?? '',
  runtimeNodeName: item.runtimeNodeName ?? item.RuntimeNodeName ?? '',
  runtimeHeartbeat: item.runtimeHeartbeat ?? item.RuntimeHeartbeat ?? '',
  coreSignalCount: item.coreSignalCount ?? item.CoreSignalCount ?? 0,
  contextSignalCount: item.contextSignalCount ?? item.ContextSignalCount ?? 0,
})

const normalizeMonitoringSignal = (item: GenericRecord): GenericRecord => ({
  ...item,
  signalKey: item.signalKey ?? item.SignalKey ?? '',
  variableName: item.variableName ?? item.VariableName ?? '',
  currentValue: item.currentValue ?? item.CurrentValue,
  previousValue: item.previousValue ?? item.PreviousValue,
  lastChangeTime: item.lastChangeTime ?? item.LastChangeTime ?? '',
  quality: item.quality ?? item.Quality ?? '',
  groupName: item.groupName ?? item.GroupName ?? '',
  signalType: item.signalType ?? item.SignalType ?? '',
  anchorType: item.anchorType ?? item.AnchorType ?? '',
  isCore: item.isCore ?? item.IsCore ?? false,
  semanticLabel: item.semanticLabel ?? item.SemanticLabel ?? '',
})

const normalizeMonitoringChange = (item: GenericRecord): GenericRecord => ({
  ...item,
  changeId: item.changeId ?? item.ChangeId ?? '',
  signalKey: item.signalKey ?? item.SignalKey ?? '',
  oldValue: item.oldValue ?? item.OldValue,
  newValue: item.newValue ?? item.NewValue,
  changedAt: item.changedAt ?? item.ChangedAt ?? '',
  stationMode: item.stationMode ?? item.StationMode ?? '',
  groupName: item.groupName ?? item.GroupName ?? '',
  signalType: item.signalType ?? item.SignalType ?? '',
  quality: item.quality ?? item.Quality ?? '',
})

const normalizeMonitoringBundle = (item: GenericRecord): GenericRecord => ({
  ...item,
  bundleId: item.bundleId ?? item.BundleId ?? '',
  startTime: item.startTime ?? item.StartTime ?? '',
  endTime: item.endTime ?? item.EndTime ?? '',
  stationMode: item.stationMode ?? item.StationMode ?? '',
  signalKeys: normalizeStringArray(item.signalKeys ?? item.SignalKeys),
  bundleSignature: item.bundleSignature ?? item.BundleSignature ?? '',
  bundleLabel: item.bundleLabel ?? item.BundleLabel ?? '',
  concurrencyCount: item.concurrencyCount ?? item.ConcurrencyCount ?? 0,
})

const normalizeMonitoringEvent = (item: GenericRecord): GenericRecord => ({
  ...item,
  id: item.id ?? item.Id,
  stationCode: item.stationCode ?? item.StationCode ?? '',
  eventType: item.eventType ?? item.EventType ?? '',
  eventLevel: item.eventLevel ?? item.EventLevel ?? '',
  triggeredAt: item.triggeredAt ?? item.TriggeredAt ?? '',
  stationMode: item.stationMode ?? item.StationMode ?? '',
  lastBundleId: item.lastBundleId ?? item.LastBundleId ?? '',
  lastChangedSignal: item.lastChangedSignal ?? item.LastChangedSignal ?? '',
  diagnosisStatus: item.diagnosisStatus ?? item.DiagnosisStatus ?? '',
  sourceRuntimeNode: item.sourceRuntimeNode ?? item.SourceRuntimeNode ?? '',
})

const normalizeMonitoringSnapshot = (item: GenericRecord | null | undefined): GenericRecord | null => {
  if (!item || typeof item !== 'object') {
    return null
  }

  return {
    ...item,
    snapshotId: item.snapshotId ?? item.SnapshotId ?? '',
    stationCode: item.stationCode ?? item.StationCode ?? '',
    snapshotTime: item.snapshotTime ?? item.SnapshotTime ?? '',
    stationMode: item.stationMode ?? item.StationMode ?? '',
    triggerType: item.triggerType ?? item.TriggerType ?? '',
    requestedSymbols: normalizeStringArray(item.requestedSymbols ?? item.RequestedSymbols),
    recentBundles: ensureArray(item.recentBundles ?? item.RecentBundles).map(normalizeMonitoringBundle),
  }
}

const extractApiData = (response: any) => {
  const payload = getPayload(response)
  if (!payload || typeof payload !== 'object') {
    return payload
  }

  return payload.data ?? payload.Data ?? payload
}

const resolveMonitoringStationCode = () => String(monitoringForm.value.stationCode || controlForm.value.stationCode || '').trim()

const buildMonitoringBaseQuery = () => ({
  projectId: props.projectId,
  stationCode: resolveMonitoringStationCode(),
})

const buildMonitoringSignalQuery = () => ({
  ...buildMonitoringBaseQuery(),
  limit: monitoringForm.value.signalLimit,
})

const buildMonitoringTimelineQuery = () => ({
  ...buildMonitoringBaseQuery(),
  limit: monitoringForm.value.timelineLimit,
})

const buildMonitoringRunCycleRequest = () => ({
  projectId: props.projectId,
  stationCode: resolveMonitoringStationCode(),
  stationMode: monitoringForm.value.stationMode || undefined,
  runtimeNodeName: monitoringForm.value.runtimeNodeName || undefined,
  persistHistory: monitoringForm.value.persistHistory,
  persistSnapshot: monitoringForm.value.persistSnapshot,
  persistEvents: monitoringForm.value.persistEvents,
  jobSource: 'Workbench',
  snapshotRecentBundleCount: monitoringForm.value.timelineLimit,
})

const loadMonitoringStations = async () => {
  const data = extractApiData(await StopCauseDiagnosisAPI.getMonitoringStations({ projectId: props.projectId }))
  monitoringStations.value = ensureArray(data).map(normalizeMonitoringStation)
}

const clearMonitoringDetails = () => {
  monitoringOverview.value = null
  monitoringSignals.value = []
  monitoringChanges.value = []
  monitoringBundles.value = []
  monitoringEvents.value = []
  monitoringSnapshot.value = null
}

const loadMonitoringDetails = async () => {
  const stationCode = resolveMonitoringStationCode()
  if (!stationCode) {
    clearMonitoringDetails()
    return
  }

  const overviewQuery = buildMonitoringBaseQuery()
  const [overviewResponse, signalResponse, changeResponse, bundleResponse, eventResponse, snapshotResponse] = await Promise.all([
    StopCauseDiagnosisAPI.getMonitoringStationOverview(overviewQuery),
    StopCauseDiagnosisAPI.getMonitoringSignals(buildMonitoringSignalQuery()),
    StopCauseDiagnosisAPI.getMonitoringChanges(buildMonitoringTimelineQuery()),
    StopCauseDiagnosisAPI.getMonitoringBundles(buildMonitoringTimelineQuery()),
    StopCauseDiagnosisAPI.getMonitoringEvents(buildMonitoringTimelineQuery()),
    StopCauseDiagnosisAPI.getMonitoringSnapshot(overviewQuery),
  ])

  monitoringOverview.value = extractApiData(overviewResponse) || null
  monitoringSignals.value = ensureArray(extractApiData(signalResponse)).map(normalizeMonitoringSignal)
  monitoringChanges.value = ensureArray(extractApiData(changeResponse)).map(normalizeMonitoringChange)
  monitoringBundles.value = ensureArray(extractApiData(bundleResponse)).map(normalizeMonitoringBundle)
  monitoringEvents.value = ensureArray(extractApiData(eventResponse)).map(normalizeMonitoringEvent)
  monitoringSnapshot.value = normalizeMonitoringSnapshot(extractApiData(snapshotResponse))
}

const refreshMonitoringData = async (includeStations = false) => {
  monitoringLoading.value = true
  try {
    if (includeStations) {
      await loadMonitoringStations()
    }
    await loadMonitoringDetails()
  }
  catch (error: any) {
    ElMessage.error(error.message || t('stopCauseWorkbench.monitoringLoadFailed'))
  }
  finally {
    monitoringLoading.value = false
  }
}

const applyMonitoringStation = async (stationCode: string) => {
  monitoringForm.value.stationCode = stationCode
  await refreshMonitoringData(false)
}

const runMonitoringCycle = async () => {
  if (!resolveMonitoringStationCode()) {
    ElMessage.warning(t('stopCauseWorkbench.monitoringStationRequired'))
    return
  }

  monitoringLoading.value = true
  try {
    monitoringRunResult.value = await callApi(
      () => StopCauseDiagnosisAPI.runMonitoringCycle(buildMonitoringRunCycleRequest()),
      t('stopCauseWorkbench.monitoringCycleDone', { station: resolveMonitoringStationCode() }),
    )
    await refreshMonitoringData(true)
    activeTab.value = 'monitoring'
  }
  catch (error: any) {
    ElMessage.error(error.message || t('stopCauseWorkbench.monitoringCycleFailed'))
  }
  finally {
    monitoringLoading.value = false
  }
}

const loadPersistedCompileRows = async () => {
  compileQueryLoading.value = true

  try {
    const response = await StopCauseDiagnosisAPI.getPersistedWriterAstPaged(buildPersistedCompileQueryRequest()) as any
    const payload = (typeof response?.total === 'number' || typeof response?.Total === 'number' || Array.isArray(response?.data) || Array.isArray(response?.Data))
      ? (response ?? {})
      : (response?.data ?? response ?? {})
    const rowsSource = payload.data ?? payload.Data ?? []
    const rows = Array.isArray(rowsSource) ? rowsSource : []

    compileQueryRows.value = rows.map(normalizePersistedCompileRow)
    compilePagination.value.total = payload.total ?? payload.Total ?? 0
    compileQueryMode.value = true
  }
  catch (error: any) {
    compileQueryRows.value = []
    compilePagination.value.total = 0
    ElMessage.error(error.message || t('stopCauseWorkbench.compileQueryFailed'))
  }
  finally {
    compileQueryLoading.value = false
  }
}

const useCompilePreviewRows = () => {
  compileQueryMode.value = false
  compilePagination.value.total = previewFilteredCompileRows.value.length
}

const stopSemanticProgressPolling = () => {
  if (semanticProgressTimer !== null) {
    window.clearInterval(semanticProgressTimer)
    semanticProgressTimer = null
  }
}

const pollSemanticProgressOnce = async () => {
  if (!semanticProgressKey.value) {
    return
  }

  try {
    const response = await StopCauseDiagnosisAPI.getAstSemanticProgress({ progressKey: semanticProgressKey.value }) as any
    semanticProgress.value = response ?? null

    const status = String(semanticProgress.value?.status || semanticProgress.value?.Status || '').toLowerCase()
    if (status === 'completed' || status === 'failed') {
      stopSemanticProgressPolling()
    }
  }
  catch {
  }
}

const startSemanticProgressPolling = () => {
  stopSemanticProgressPolling()
  void pollSemanticProgressOnce()
  semanticProgressTimer = window.setInterval(() => {
    void pollSemanticProgressOnce()
  }, 1000)
}

const loadPersistedSemanticRows = async () => {
  try {
    const response = await StopCauseDiagnosisAPI.getPersistedSemanticFindingsPaged(buildPersistedSemanticQueryRequest()) as any
    const payload = (typeof response?.total === 'number' || typeof response?.Total === 'number' || Array.isArray(response?.data) || Array.isArray(response?.Data))
      ? (response ?? {})
      : (response?.data ?? response ?? {})
    const rowsSource = payload.data ?? payload.Data ?? []
    persistedSemanticRows.value = Array.isArray(rowsSource) ? rowsSource.map(normalizeSemanticFinding) : []
    persistedSemanticTotal.value = payload.total ?? payload.Total ?? 0
  }
  catch {
    persistedSemanticRows.value = []
    persistedSemanticTotal.value = 0
  }
}

const loadPersistedStationMap = async () => {
  try {
    const response = await StopCauseDiagnosisAPI.getPersistedStationMap(buildPersistedStationMapQueryRequest()) as any
    stationMapResult.value = normalizePersistedStationMapResponse(response)
  }
  catch {
    if (!stationMapResult.value) {
      stationMapResult.value = null
    }
  }
}

const loadStationWriterAstRows = async () => {
  const stationCode = stationWriterAstContext.value.stationCode
  if (!stationCode) {
    stationWriterAstRows.value = []
    stationWriterAstPagination.value.total = 0
    return
  }

  stationWriterAstDialogLoading.value = true

  try {
    const writerResponse = await LogicUnitAPI.getWriterFragmentsPaged({
      page: stationWriterAstPagination.value.page,
      limit: stationWriterAstPagination.value.limit,
      sortField: 'WriteVarsCount',
      sortType: 'desc',
      ProjectId: props.projectId,
      StationCode: stationCode,
      EligibleOnly: true,
      MinWriteVarsCount: 1,
      RawContentMaxLength: 400,
    }) as any

    const writerList = Array.isArray(writerResponse?.data) ? writerResponse.data : []
    stationWriterAstPagination.value.total = writerResponse?.total || 0

    const logicUnitIds = writerList
      .map((item: GenericRecord) => item.logicUnitId ?? item.LogicUnitId)
      .filter((item: number | null | undefined) => !!item)

    let persistedAstList: GenericRecord[] = []
    if (logicUnitIds.length > 0) {
      const astResponse = await StopCauseDiagnosisAPI.getPersistedWriterAstDetails({
        projectId: props.projectId,
        stationCode,
        logicUnitIds: logicUnitIds.join(','),
        limit: logicUnitIds.length,
      }) as any

      persistedAstList = Array.isArray(astResponse?.data)
        ? astResponse.data
        : (Array.isArray(astResponse?.Data) ? astResponse.Data : [])
    }

    const astMap = new Map(persistedAstList.map((item: GenericRecord) => [item.logicUnitId ?? item.LogicUnitId, item]))

    stationWriterAstRows.value = writerList.map((item: GenericRecord) => {
      const ast = astMap.get(item.logicUnitId ?? item.LogicUnitId)
      return {
        ...item,
        ast,
        astSummary: summarizeRootNode(ast?.rootNodeJson || ast?.RootNodeJson),
        semanticSummary: (ast?.semanticFindings || ast?.SemanticFindings || []).map((finding: GenericRecord) => `${finding.category || finding.Category}:${finding.variableName || finding.VariableName}`).join(' ; '),
      }
    })
  }
  catch (error: any) {
    stationWriterAstRows.value = []
    stationWriterAstPagination.value.total = 0
    ElMessage.error(error.message || t('stopCauseWorkbench.stationAstLoadFailed'))
  }
  finally {
    stationWriterAstDialogLoading.value = false
  }
}

const openStationWriterAstDialog = async (row: GenericRecord) => {
  const stationCode = row.stationCode || row.StationCode
  if (!stationCode) {
    ElMessage.warning(t('stopCauseWorkbench.missingStationCode'))
    return
  }

  stationWriterAstDialogVisible.value = true
  stationWriterAstContext.value = {
    stationCode,
    blockCount: row.blockCount ?? row.BlockCount ?? 0,
    logicUnitCount: row.logicUnitCount ?? row.LogicUnitCount ?? 0,
  }
  stationWriterAstPagination.value.page = 1
  await loadStationWriterAstRows()
}

const handleWriterSearch = async () => {
  writerPagination.value.page = 1
  await loadWriterFragments()
}

const handleWriterReset = async () => {
  writerSearch.value = {
    blockId: '',
    blockName: '',
    stationCode: '',
    networkId: undefined,
    title: '',
    unitType: '',
    eligibleOnly: true,
    minWriteVarsCount: 1,
    rawContentMaxLength: 200,
  }
  writerPagination.value.page = 1
  await loadWriterFragments()
}

const onWriterPageChange = async (page: number) => {
  writerPagination.value.page = page
  await loadWriterFragments()
}

const onWriterPageSizeChange = async (size: number) => {
  writerPagination.value.limit = size
  writerPagination.value.page = 1
  await loadWriterFragments()
}

const handleCompileSearch = async () => {
  compilePagination.value.page = 1
  await loadPersistedCompileRows()
}

const handleCompileReset = async () => {
  compileSearch.value = {
    stationCode: '',
    blockName: '',
    variableName: '',
    compileStatus: '',
  }
  compilePagination.value.page = 1
  await loadPersistedCompileRows()
}

const onCompilePageChange = async (page: number) => {
  compilePagination.value.page = page
  if (compileQueryMode.value) {
    await loadPersistedCompileRows()
  }
}

const onCompilePageSizeChange = async (size: number) => {
  compilePagination.value.limit = size
  compilePagination.value.page = 1
  if (compileQueryMode.value) {
    await loadPersistedCompileRows()
  }
}

const onStationWriterAstPageChange = async (page: number) => {
  stationWriterAstPagination.value.page = page
  await loadStationWriterAstRows()
}

const onStationWriterAstPageSizeChange = async (size: number) => {
  stationWriterAstPagination.value.limit = size
  stationWriterAstPagination.value.page = 1
  await loadStationWriterAstRows()
}

const runAllOfflineAnalysis = async () => {
  try {
    const response = await StopCauseDiagnosisAPI.startFullAnalysis({
      projectId: props.projectId,
      stationCode: controlForm.value.stationCode || undefined,
      blockId: controlForm.value.blockId,
      useLlmAnnotation: controlForm.value.useLlmAnnotation,
      rawContentMaxLength: controlForm.value.rawContentMaxLength,
    }) as any

    const payload = response?.backResult ?? response?.BackResult ?? response
    const taskId = payload?.taskId ?? payload?.TaskId
    if (!taskId) {
      ElMessage.error(response?.errorMessage ?? response?.ErrorMessage ?? t('stopCauseWorkbench.analysisStartFailed'))
      return
    }

    fullAnalysisTaskId.value = taskId
    fullAnalysisProgress.value = {
      taskId,
      status: 'Running',
      currentStep: 0,
      totalSteps: 5,
      stepName: t('stopCauseWorkbench.initializing'),
      message: t('stopCauseWorkbench.taskSubmitted'),
      percentage: 0,
    }
    fullAnalysisDialogVisible.value = true
    startFullAnalysisPolling()
  }
  catch (error: any) {
    ElMessage.error(error.message || t('stopCauseWorkbench.fullAnalysisStartFailed'))
  }
}

const startFullAnalysisPolling = () => {
  stopFullAnalysisPolling()
  void pollFullAnalysisOnce()
  fullAnalysisTimer = window.setInterval(() => {
    void pollFullAnalysisOnce()
  }, 1500)
}

const stopFullAnalysisPolling = () => {
  if (fullAnalysisTimer !== null) {
    window.clearInterval(fullAnalysisTimer)
    fullAnalysisTimer = null
  }
}

const pollFullAnalysisOnce = async () => {
  if (!fullAnalysisTaskId.value) return
  try {
    const response = await StopCauseDiagnosisAPI.getFullAnalysisProgress({
      taskId: fullAnalysisTaskId.value,
    }) as any
    fullAnalysisProgress.value = response ?? null

    const status = String(response?.status ?? response?.Status ?? '').toLowerCase()
    if (status === 'completed') {
      stopFullAnalysisPolling()
      ElMessage.success(t('stopCauseWorkbench.fullAnalysisDone'))
      await loadPersistedStationMap()
      await loadPersistedCompileRows()
      activeTab.value = 'compile'
    }
    else if (status === 'failed') {
      stopFullAnalysisPolling()
      ElMessage.error(response?.errorMessage ?? response?.ErrorMessage ?? t('stopCauseWorkbench.fullAnalysisFailed'))
    }
  }
  catch {
    // 轮询中网络错误不中断
  }
}

const confirmAndPersist = async () => {
  await ElMessageBox.confirm(
    t('stopCauseWorkbench.persistConfirmText'),
    t('stopCauseWorkbench.persistConfirmTitle'),
    {
      confirmButtonText: t('stopCauseWorkbench.confirmPersist'),
      cancelButtonText: t('common.cancel'),
      type: 'warning',
    },
  )

  loading.value = true
  try {
    extractResult.value = await callApi(
      () => StopCauseDiagnosisAPI.extractStations(buildPersistedStationExtractionRequest()),
      t('stopCauseWorkbench.backfillDone'),
    )
    compileResult.value = await callApi(
      () => StopCauseDiagnosisAPI.compilePreview(buildPersistedCompileRequest()),
      t('stopCauseWorkbench.persistedDone'),
    )
    await loadPersistedStationMap()
    await loadPersistedCompileRows()
    activeTab.value = 'acceptance'
  }
  catch (error: any) {
    ElMessage.error(error.message || t('stopCauseWorkbench.persistFailed'))
  }
  finally {
    loading.value = false
  }
}

const extractPayload = computed(() => extractResult.value ?? {})
const stationMapPayload = computed(() => stationMapResult.value ?? {})
const compilePayload = computed(() => compileResult.value ?? {})
const semanticPayload = computed(() => semanticResult.value ?? {})
const stationBuckets = computed(() => {
  const persistedRows = ensureArray(stationMapPayload.value.stationBuckets ?? stationMapPayload.value.StationBuckets)
  if (persistedRows.length > 0) {
    return persistedRows.map(normalizeStationBucket)
  }

  return ensureArray(extractPayload.value.stationBuckets ?? extractPayload.value.StationBuckets).map(normalizeStationBucket)
})
const stationConflicts = computed(() => ensureArray(stationMapPayload.value.stationConflicts ?? stationMapPayload.value.StationConflicts).map(normalizeStationConflict))
const orphanAnchors = computed(() => ensureArray(stationMapPayload.value.orphanAnchors ?? stationMapPayload.value.OrphanAnchors).map(normalizeOrphanAnchor))
const stationAnchorAssignments = computed(() => ensureArray(stationMapPayload.value.anchorAssignments ?? stationMapPayload.value.AnchorAssignments).map(normalizeAnchorAssignment))
const semanticResponseFindings = computed(() => ensureArray(semanticPayload.value.astSemanticFindings ?? semanticPayload.value.AstSemanticFindings).map(normalizeSemanticFinding))
const semanticFindings = computed(() => persistedSemanticRows.value.length > 0 ? persistedSemanticRows.value : semanticResponseFindings.value)

const extractSummary = computed(() => {
  const raw = extractPayload.value.summary ?? extractPayload.value.Summary ?? stationMapPayload.value.extractionSummary ?? stationMapPayload.value.ExtractionSummary ?? {}
  const physicalAnchorCount = stationAnchorAssignments.value.filter((item: GenericRecord) => {
    const anchorType = String(item.anchorType ?? item.AnchorType ?? '').toLowerCase()
    return anchorType.includes('physical') || anchorType.includes('qpoint')
  }).length

  return {
    ...raw,
    stationCount: raw.stationCount ?? stationBuckets.value.length,
    taggedPhysicalTagCount: raw.taggedPhysicalTagCount ?? physicalAnchorCount,
    variableGroupCount: raw.variableGroupCount ?? stationAnchorAssignments.value.length,
    taggedVariableGroupCount: raw.taggedVariableGroupCount ?? stationAnchorAssignments.value.length,
  }
})

const stationMapCounts = computed(() => {
  const raw = stationMapPayload.value.stationMapCounts ?? stationMapPayload.value.StationMapCounts ?? {}
  return {
    stationBuckets: raw.stationBuckets ?? raw.StationBuckets ?? stationBuckets.value.length,
    stationConflicts: raw.stationConflicts ?? raw.StationConflicts ?? stationConflicts.value.length,
    orphanAnchors: raw.orphanAnchors ?? raw.OrphanAnchors ?? orphanAnchors.value.length,
    variablePropagations: raw.variablePropagations ?? raw.VariablePropagations ?? (stationMapPayload.value.variablePropagations ?? stationMapPayload.value.VariablePropagations ?? []).length,
  }
})

const compileCounts = computed(() => {
  const raw = compilePayload.value.counts ?? compilePayload.value.Counts ?? {}
  return {
    astSeeds: raw.astSeeds ?? raw.AstSeeds ?? displayedCompileTotal.value,
    astCompileResults: raw.astCompileResults ?? raw.AstCompileResults ?? displayedCompileTotal.value,
  }
})

const compileSummary = computed(() => {
  const raw = compilePayload.value.summary ?? compilePayload.value.Summary ?? {}
  const stationAnchorCount = compileQueryRows.value.reduce((acc: Record<string, number>, row: GenericRecord) => {
    const stationCode = String(row.stationCode || '').trim()
    if (!stationCode) {
      return acc
    }
    acc[stationCode] = (acc[stationCode] || 0) + 1
    return acc
  }, {})

  return {
    ...raw,
    llmClassifiedCount: raw.llmClassifiedCount ?? semanticFindings.value.length,
    stationAnchorCount: raw.stationAnchorCount ?? stationAnchorCount,
  }
})

const semanticSummary = computed(() => {
  const raw = semanticPayload.value.summary ?? semanticPayload.value.Summary ?? {}
  const stationAnchorCount = semanticFindings.value.reduce((acc: Record<string, number>, item: GenericRecord) => {
    const stationCode = String(item.stationCode || item.StationCode || '').trim()
    if (!stationCode) {
      return acc
    }
    acc[stationCode] = (acc[stationCode] || 0) + 1
    return acc
  }, {})

  return {
    ...raw,
    llmClassifiedCount: raw.llmClassifiedCount ?? semanticFindings.value.length,
    confirmedCount: raw.confirmedCount ?? semanticFindings.value.length,
    reviewQueueSize: raw.reviewQueueSize ?? 0,
    stationAnchorCount: raw.stationAnchorCount ?? stationAnchorCount,
  }
})

const semanticProgressStatusText = computed(() => {
  const message = semanticProgress.value?.message || semanticProgress.value?.Message
  if (message) {
    return message
  }

  if (persistedSemanticTotal.value > 0) {
    return t('stopCauseWorkbench.restoredFromDb')
  }

  return t('stopCauseWorkbench.notStarted')
})

const llmDiagnostics = computed(() => {
  const raw = semanticPayload.value.llmDiagnostics ?? semanticPayload.value.LlmDiagnostics ?? compilePayload.value.llmDiagnostics ?? compilePayload.value.LlmDiagnostics ?? {}
  return {
    totalContextCount: raw.totalContextCount ?? semanticProgress.value?.totalCandidates ?? semanticProgress.value?.TotalCandidates ?? 0,
    stationFilteredCount: raw.stationFilteredCount ?? semanticProgress.value?.totalCandidates ?? semanticProgress.value?.TotalCandidates ?? 0,
    sentToLlmCount: raw.sentToLlmCount ?? semanticProgress.value?.totalCandidates ?? semanticProgress.value?.TotalCandidates ?? 0,
    suppressedCount: raw.suppressedCount ?? 0,
    llmReturnedCount: raw.llmReturnedCount ?? semanticFindings.value.length,
    confirmedByLlmCount: raw.confirmedByLlmCount ?? semanticVirtualFindings.value.length,
    handshakeByLlmCount: raw.handshakeByLlmCount ?? semanticHandshakeFindings.value.length,
    reviewByLlmCount: raw.reviewByLlmCount ?? 0,
    unknownDroppedCount: raw.unknownDroppedCount ?? 0,
    stateFlagDroppedCount: raw.stateFlagDroppedCount ?? 0,
  }
})

const hasExtractSummary = computed(() => extractSummary.value.stationCount > 0)
const hasStationMap = computed(() => stationBuckets.value.length > 0 || stationConflicts.value.length > 0 || orphanAnchors.value.length > 0)
const hasCompileData = computed(() => displayedCompileTotal.value > 0)
const hasSemanticData = computed(() => semanticFindings.value.length > 0 || persistedSemanticTotal.value > 0 || !!semanticResult.value)

const previewCompileRows = computed(() => (compilePayload.value.compileResults || []).map((item: GenericRecord) => {
  return normalizeCompileRow(item)
}))

const previewFilteredCompileRows = computed(() => {
  const stationCode = compileSearch.value.stationCode.trim().toLowerCase()
  const blockName = compileSearch.value.blockName.trim().toLowerCase()
  const variableName = compileSearch.value.variableName.trim().toLowerCase()
  const compileStatus = compileSearch.value.compileStatus.trim().toLowerCase()

  return previewCompileRows.value.filter((item: GenericRecord) => {
    const currentStationCode = String(item.stationCode || '').toLowerCase()
    const currentBlockName = String(item.blockNameSummary || '').toLowerCase()
    const currentVariableName = String(item.variableName || '').toLowerCase()
    const currentCompileStatus = String(item.compileStatus || '').toLowerCase()

    if (stationCode && !currentStationCode.includes(stationCode)) {
      return false
    }

    if (blockName && !currentBlockName.includes(blockName)) {
      return false
    }

    if (variableName && !currentVariableName.includes(variableName)) {
      return false
    }

    if (compileStatus && currentCompileStatus !== compileStatus) {
      return false
    }

    return true
  })
})

const previewPagedCompileRows = computed(() => {
  const start = Math.max(compilePagination.value.page - 1, 0) * compilePagination.value.limit
  return previewFilteredCompileRows.value.slice(start, start + compilePagination.value.limit)
})

const displayedCompileRows = computed(() => compileQueryMode.value ? compileQueryRows.value : previewPagedCompileRows.value)
const displayedCompileTotal = computed(() => compileQueryMode.value ? compilePagination.value.total : previewFilteredCompileRows.value.length)

const stationCoverageRows = computed(() => {
  const stationAnchorCount = semanticSummary.value.stationAnchorCount ?? compileSummary.value.stationAnchorCount ?? {}
  return Object.entries(stationAnchorCount).map(([stationCode, anchorCount]) => ({ stationCode, anchorCount }))
})

const semanticVirtualFindings = computed(() => semanticFindings.value.filter((item: GenericRecord) => String(item.category || item.Category) === 'VirtualQPoint'))
const semanticHandshakeFindings = computed(() => semanticFindings.value.filter((item: GenericRecord) => String(item.category || item.Category) === 'HandshakeSignal'))
const semanticIoMappingFindings = computed(() => semanticFindings.value.filter((item: GenericRecord) => {
  const ioMapping = String(item.ioMapping || item.IoMapping || '').trim()
  const relatedIOTags = ensureArray(item.relatedIOTags ?? item.RelatedIOTags)
  return ioMapping.length > 0 || relatedIOTags.length > 0
}))
const semanticSensorMappingFindings = computed(() => semanticIoMappingFindings.value.filter((item: GenericRecord) => {
  const variableName = String(item.variableName || item.VariableName || '').toLowerCase()
  return variableName.includes('.di.sensor[') || variableName.includes('.do.sensor[')
}))

// ── 覆盖度查询 ──
const loadCoverageData = async () => {
  coverageLoading.value = true
  try {
    const res = await StopCauseDiagnosisAPI.getUnassignedBlocks({
      projectId: props.projectId,
      blockName: coverageSearch.value.blockName || undefined,
      blockType: coverageSearch.value.blockType || undefined,
      programmingLanguage: coverageSearch.value.programmingLanguage || undefined,
      page: coveragePagination.value.page,
      limit: coveragePagination.value.limit,
    })
    const data = (res as any)?.backResult ?? (res as any)?.data ?? res
    coverageStats.value = {
      total: data.total ?? 0,
      assigned: data.assigned ?? 0,
      unassigned: data.unassigned ?? 0,
      rate: data.rate ?? '0%',
    }
    coverageByType.value = data.byType ?? []
    coverageRows.value = data.data ?? []
    coverageFilteredTotal.value = data.filteredTotal ?? 0
  }
  catch (err: any) {
    ElMessage.error(t('stopCauseWorkbench.coverageLoadFailed', { message: err?.message || err }))
  }
  finally {
    coverageLoading.value = false
  }
}

const acceptanceFindings = computed(() => {
  const findings: Array<{ type: 'success' | 'warning' | 'info', title: string, description: string }> = []

  if (stationMapCounts.value.stationConflicts === 0 && stationMapCounts.value.orphanAnchors === 0 && stationMapResult.value) {
    findings.push({
      type: 'success',
      title: t('stopCauseWorkbench.findingStableTitle'),
      description: t('stopCauseWorkbench.findingStableDesc'),
    })
  }

  if ((semanticSummary.value.reviewQueueSize ?? 0) > Math.max((semanticSummary.value.confirmedCount ?? 0) * 5, 10)) {
    findings.push({
      type: 'warning',
      title: t('stopCauseWorkbench.findingReviewTitle'),
      description: t('stopCauseWorkbench.findingReviewDesc'),
    })
  }

  if (semanticResult.value && (semanticSummary.value.llmClassifiedCount ?? 0) === 0) {
    findings.push({
      type: 'warning',
      title: t('stopCauseWorkbench.findingLlmTitle'),
      description: t('stopCauseWorkbench.findingLlmDesc'),
    })
  }

  if ((compilePayload.value.compileResults ?? []).length > 0) {
    findings.push({
      type: 'info',
      title: t('stopCauseWorkbench.findingAstTitle'),
      description: t('stopCauseWorkbench.findingAstDesc'),
    })
  }

  if (findings.length === 0) {
    findings.push({
      type: 'info',
      title: t('stopCauseWorkbench.findingWaitTitle'),
      description: t('stopCauseWorkbench.findingWaitDesc'),
    })
  }

  return findings
})

const metricCards = computed(() => ([
  {
    label: t('stopCauseWorkbench.metricStationBuckets'),
    value: stationMapCounts.value.stationBuckets ?? extractSummary.value.stationCount ?? 0,
  },
  {
    label: t('stopCauseWorkbench.metricAstCompile'),
    value: compileCounts.value.astCompileResults ?? 0,
  },
  {
    label: t('stopCauseWorkbench.metricSemanticQ'),
    value: semanticFindings.value.length ?? 0,
  },
  {
    label: t('stopCauseWorkbench.metricIoMapping'),
    value: semanticIoMappingFindings.value.length ?? 0,
  },
  {
    label: t('stopCauseWorkbench.llmClassified'),
    value: semanticSummary.value.llmClassifiedCount ?? 0,
  },
  {
    label: t('stopCauseWorkbench.monitoringStation'),
    value: monitoringStations.value.length,
  },
]))

const monitoringOverviewState = computed(() => monitoringOverview.value?.state ?? monitoringOverview.value?.State ?? null)
const monitoringOverviewRuntimeLease = computed(() => monitoringOverview.value?.runtimeLease ?? monitoringOverview.value?.RuntimeLease ?? null)
const monitoringLatestEvent = computed(() => {
  const event = monitoringOverview.value?.latestEvent ?? monitoringOverview.value?.LatestEvent ?? null
  return event ? normalizeMonitoringEvent(event) : null
})
const monitoringSnapshotText = computed(() => {
  if (!monitoringSnapshot.value) {
    return '-'
  }

  try {
    return JSON.stringify(monitoringSnapshot.value, null, 2)
  }
  catch {
    return '-'
  }
})

watch(activeTab, async (tab) => {
  if (tab === 'writers' && writerFragments.value.length === 0 && !writerLoading.value) {
    await loadWriterFragments()
  }

  if (tab === 'compile' &&
    !compileQueryLoading.value &&
    compileQueryRows.value.length === 0 &&
    (compilePayload.value.compileResults ?? []).length === 0)
  {
    await loadPersistedCompileRows()
  }

  if ((tab === 'qpoints' || tab === 'io-mappings') && persistedSemanticRows.value.length === 0) {
    await loadPersistedSemanticRows()
  }

  if (tab === 'monitoring' && !monitoringLoading.value) {
    await refreshMonitoringData(monitoringStations.value.length === 0)
  }

  if (tab === 'coverage' && !coverageLoading.value && coverageRows.value.length === 0) {
    await loadCoverageData()
  }
})

watch(() => semanticForm.value.stationCode, async (stationCode, previousStationCode) => {
  if (stationCode === previousStationCode) {
    return
  }

  persistedSemanticRows.value = []
  persistedSemanticTotal.value = 0

  if (activeTab.value === 'qpoints' || activeTab.value === 'io-mappings') {
    await loadPersistedSemanticRows()
  }
})

watch([compileQueryMode, previewFilteredCompileRows], () => {
  if (!compileQueryMode.value) {
    const maxPage = Math.max(1, Math.ceil(previewFilteredCompileRows.value.length / compilePagination.value.limit))
    if (compilePagination.value.page > maxPage) {
      compilePagination.value.page = maxPage
    }
    compilePagination.value.total = previewFilteredCompileRows.value.length
  }
}, { deep: true })

watch(
  [activeTab, controlForm, semanticForm, writerSearch, writerPagination, compileSearch, compilePagination, writerFragments, extractResult, stationMapResult, compileResult, semanticResult],
  () => {
    writeCache()
  },
  { deep: true },
)

onMounted(() => {
  restoreCache()
  void loadPersistedStationMap()
  void loadPersistedCompileRows()
  void loadPersistedSemanticRows()
  void loadMonitoringStations()
})

onUnmounted(() => {
  stopSemanticProgressPolling()
  stopFullAnalysisPolling()
  stopBatchDiscoverPolling()
})
</script>

<template>
  <div class="stop-cause-workbench" v-loading="loading || writerLoading || monitoringLoading">
    <el-card shadow="never" class="control-card">
      <template #header>
        <div class="card-header">
          <span>{{ $t('stopCauseWorkbench.title') }}</span>
          <el-tag type="info" size="small">{{ $t('stopCauseWorkbench.projectTag', { id: projectId }) }}</el-tag>
        </div>
      </template>

      <el-form :inline="true" class="control-form">
        <el-form-item :label="$t('stopCauseWorkbench.stationCode')">
          <el-input
            v-model="controlForm.stationCode"
            :placeholder="$t('stopCauseWorkbench.stationCodePlaceholder')"
            clearable
            style="width: 180px"
          />
        </el-form-item>
        <el-form-item :label="$t('stopCauseWorkbench.qpointStation')">
          <el-input
            v-model="semanticForm.stationCode"
            :placeholder="$t('stopCauseWorkbench.qpointStationPlaceholder')"
            clearable
            style="width: 220px"
          />
        </el-form-item>
        <el-form-item label="BlockId">
          <el-input-number v-model="controlForm.blockId" :min="1" :step="1" :placeholder="$t('stopCauseWorkbench.blockIdPlaceholder')" style="width: 180px" />
        </el-form-item>
        <el-form-item :label="$t('stopCauseWorkbench.llmAnnotation')">
          <el-switch v-model="controlForm.useLlmAnnotation" />
        </el-form-item>
        <el-form-item :label="$t('stopCauseWorkbench.physicalQ')">
          <el-switch v-model="controlForm.includePhysicalAnchors" />
        </el-form-item>
        <el-form-item :label="$t('stopCauseWorkbench.virtualQ')">
          <el-switch v-model="controlForm.includeVirtualAnchors" />
        </el-form-item>
        <el-form-item :label="$t('stopCauseWorkbench.handshake')">
          <el-switch v-model="controlForm.includeHandshakeAnchors" />
        </el-form-item>
        <el-form-item :label="$t('stopCauseWorkbench.summaryLength')">
          <el-input-number v-model="controlForm.rawContentMaxLength" :min="0" :max="20000" :step="200" />
          <span class="form-tip">{{ $t('stopCauseWorkbench.unlimitedTip') }}</span>
        </el-form-item>
      </el-form>

      <div class="action-row">
        <el-button type="primary" @click="runAllOfflineAnalysis">{{ $t('stopCauseWorkbench.runFullAnalysis') }}</el-button>
        <el-button type="warning" :loading="semanticRunning" @click="runSemanticQPointExtraction">{{ $t('stopCauseWorkbench.runSemanticExtraction') }}</el-button>
        <el-button type="success" :loading="batchDiscoverRunning" @click="runBatchQPointDiscovery">{{ $t('stopCauseWorkbench.batchQPointBtn') }}</el-button>
        <el-button type="info" :loading="monitoringLoading" @click="runMonitoringCycle">{{ $t('stopCauseWorkbench.runMonitoringCycle') }}</el-button>
        <el-button type="success" @click="confirmAndPersist">{{ $t('stopCauseWorkbench.forceRepersist') }}</el-button>
        <el-button @click="clearCache">{{ $t('stopCauseWorkbench.clearLocalResults') }}</el-button>
      </div>

      <div class="mt-4 writer-hint">
        {{ $t('stopCauseWorkbench.workbenchHint') }}
      </div>

      <div class="metric-grid">
        <div v-for="item in metricCards" :key="item.label" class="metric-item">
          <div class="metric-label">{{ item.label }}</div>
          <div class="metric-value">{{ item.value }}</div>
        </div>
      </div>
    </el-card>

    <el-tabs v-model="activeTab">
      <el-tab-pane :label="$t('stopCauseWorkbench.tabWriters')" name="writers">
        <el-card shadow="never">
          <template #header>
            <div class="card-header">
              <span>{{ $t('stopCauseWorkbench.writersQuery') }}</span>
              <el-tag type="warning" size="small">{{ $t('stopCauseWorkbench.writersTestTip') }}</el-tag>
            </div>
          </template>

          <el-form :inline="true" class="control-form" @submit.prevent="handleWriterSearch">
            <el-form-item label="BlockId">
              <el-input
                v-model="writerSearch.blockId"
                :placeholder="$t('stopCauseWorkbench.writersBlockIdPlaceholder')"
                clearable
                style="width: 220px"
              />
            </el-form-item>
            <el-form-item :label="$t('stopCauseWorkbench.blockNameKeyword')">
              <el-input
                v-model="writerSearch.blockName"
                :placeholder="$t('stopCauseWorkbench.blockNamePlaceholder')"
                clearable
                style="width: 220px"
              />
            </el-form-item>
            <el-form-item :label="$t('stopCauseWorkbench.stationCode')">
              <el-input
                v-model="writerSearch.stationCode"
                :placeholder="$t('stopCauseWorkbench.stationCodeExample')"
                clearable
                style="width: 180px"
              />
            </el-form-item>
            <el-form-item label="NetworkId">
              <el-input-number v-model="writerSearch.networkId" :min="1" :step="1" style="width: 180px" />
            </el-form-item>
            <el-form-item label="UnitType">
              <el-input
                v-model="writerSearch.unitType"
                :placeholder="$t('stopCauseWorkbench.unitTypePlaceholder')"
                clearable
                style="width: 180px"
              />
            </el-form-item>
            <el-form-item :label="$t('stopCauseWorkbench.titleKeyword')">
              <el-input
                v-model="writerSearch.title"
                :placeholder="$t('stopCauseWorkbench.titlePlaceholder')"
                clearable
                style="width: 220px"
              />
            </el-form-item>
            <el-form-item :label="$t('stopCauseWorkbench.eligibleOnly')">
              <el-switch v-model="writerSearch.eligibleOnly" />
            </el-form-item>
            <el-form-item :label="$t('stopCauseWorkbench.minWriteVars')">
              <el-input-number v-model="writerSearch.minWriteVarsCount" :min="1" :max="1000" :step="1" />
            </el-form-item>
            <el-form-item :label="$t('stopCauseWorkbench.contentTruncate')">
              <el-input-number v-model="writerSearch.rawContentMaxLength" :min="0" :max="5000" :step="100" />
            </el-form-item>
          </el-form>

          <div class="action-row">
            <el-button type="primary" :loading="writerLoading" @click="handleWriterSearch">{{ $t('stopCauseWorkbench.searchWriters') }}</el-button>
            <el-button :loading="writerLoading" @click="handleWriterReset">{{ $t('stopCauseWorkbench.resetAndSearch') }}</el-button>
          </div>

          <div class="mt-4 writer-hint">
            {{ $t('stopCauseWorkbench.writersHint') }}
          </div>
        </el-card>

        <el-card shadow="never" class="mt-4">
          <template #header>
            <div class="card-header">
              <span>{{ $t('stopCauseWorkbench.writersResult') }}</span>
              <el-tag type="info" size="small">{{ $t('stopCauseWorkbench.totalCount', { n: writerPagination.total }) }}</el-tag>
            </div>
          </template>

          <el-table :data="writerFragments" stripe>
            <el-table-column prop="blockName" :label="$t('stopCauseWorkbench.colBlockName')" min-width="180" show-overflow-tooltip />
            <el-table-column prop="stationCode" :label="$t('stopCauseWorkbench.colStation')" width="120" show-overflow-tooltip />
            <el-table-column prop="unitType" :label="$t('stopCauseWorkbench.colUnitType')" width="140" />
            <el-table-column prop="networkId" :label="$t('stopCauseWorkbench.colNetwork')" width="100" />
            <el-table-column prop="title" :label="$t('common.title')" min-width="180" show-overflow-tooltip />
            <el-table-column prop="writeVarsCount" :label="$t('stopCauseWorkbench.colWriteCount')" width="90" />
            <el-table-column prop="readVarsCount" :label="$t('stopCauseWorkbench.colReadCount')" width="90" />
            <el-table-column prop="callsCount" :label="$t('stopCauseWorkbench.colCallCount')" width="90" />
            <el-table-column :label="$t('stopCauseWorkbench.colRawSnippet')" min-width="420">
              <template #default="{ row }">
                <div class="raw-snippet">{{ row.rawContent || '-' }}</div>
              </template>
            </el-table-column>
          </el-table>

          <el-empty v-if="!writerLoading && writerFragments.length === 0" :description="$t('stopCauseWorkbench.noWriters')" />

          <div class="pagination-row">
            <el-pagination
              v-model:current-page="writerPagination.page"
              v-model:page-size="writerPagination.limit"
              :total="writerPagination.total"
              :page-sizes="[10, 20, 50]"
              layout="total, sizes, prev, pager, next"
              @current-change="onWriterPageChange"
              @size-change="onWriterPageSizeChange"
            />
          </div>
        </el-card>
      </el-tab-pane>

      <el-tab-pane :label="$t('stopCauseWorkbench.tabStationMap')" name="station-map">
        <div class="panel-grid">
          <el-card shadow="never">
            <template #header>
              <div class="card-header">
                <span>{{ $t('stopCauseWorkbench.extractSummaryTitle') }}</span>
              </div>
            </template>
            <el-descriptions v-if="hasExtractSummary" :column="2" border>
              <el-descriptions-item :label="$t('stopCauseWorkbench.stationBucketCount')">{{ extractSummary.stationCount ?? 0 }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.taggedPhysicalTags')">{{ extractSummary.taggedPhysicalTagCount ?? 0 }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.variableGroupCount')">{{ extractSummary.variableGroupCount ?? 0 }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.taggedVariableGroups')">{{ extractSummary.taggedVariableGroupCount ?? 0 }}</el-descriptions-item>
            </el-descriptions>
            <el-empty v-else :description="$t('stopCauseWorkbench.extractionNotRun')" />
          </el-card>

          <el-card shadow="never">
            <template #header>
              <div class="card-header">
                <span>{{ $t('stopCauseWorkbench.stationMapSummary') }}</span>
              </div>
            </template>
            <el-descriptions v-if="hasStationMap" :column="2" border>
              <el-descriptions-item label="StationBuckets">{{ stationMapCounts.stationBuckets ?? 0 }}</el-descriptions-item>
              <el-descriptions-item label="StationConflicts">{{ stationMapCounts.stationConflicts ?? 0 }}</el-descriptions-item>
              <el-descriptions-item label="OrphanAnchors">{{ stationMapCounts.orphanAnchors ?? 0 }}</el-descriptions-item>
              <el-descriptions-item label="VariablePropagations">{{ stationMapCounts.variablePropagations ?? 0 }}</el-descriptions-item>
            </el-descriptions>
            <el-empty v-else :description="$t('stopCauseWorkbench.stationMapNotGenerated')" />
          </el-card>
        </div>

        <el-card shadow="never" class="mt-4">
          <template #header>
            <div class="card-header">
              <span>{{ $t('stopCauseWorkbench.stationBucketsTitle') }}</span>
            </div>
          </template>
          <el-table :data="stationBuckets" stripe>
            <el-table-column prop="stationCode" :label="$t('stopCauseWorkbench.colStation')" min-width="120" />
            <el-table-column prop="anchorCount" :label="$t('stopCauseWorkbench.colAnchorCount')" width="100" />
            <el-table-column prop="logicUnitCount" :label="$t('stopCauseWorkbench.colLogicUnit')" width="110" />
            <el-table-column prop="blockCount" :label="$t('stopCauseWorkbench.colBlockCount')" width="100" />
            <el-table-column prop="confidence" :label="$t('stopCauseWorkbench.colConfidence')" width="100" />
            <el-table-column :label="$t('common.operate')" width="180" fixed="right">
              <template #default="{ row }">
                <el-button link type="primary" @click="openStationWriterAstDialog(row)">{{ $t('stopCauseWorkbench.viewWriterAst') }}</el-button>
              </template>
            </el-table-column>
          </el-table>
        </el-card>

        <div class="panel-grid mt-4">
          <el-card shadow="never">
            <template #header>
              <div class="card-header">
                <span>{{ $t('stopCauseWorkbench.stationConflictsTitle') }}</span>
              </div>
            </template>
            <el-table :data="stationConflicts" stripe>
              <el-table-column prop="variableName" :label="$t('stopCauseWorkbench.colVariable')" min-width="180" show-overflow-tooltip />
              <el-table-column prop="recommendedStation" :label="$t('stopCauseWorkbench.colRecommendedStation')" width="120" />
              <el-table-column prop="reason" :label="$t('stopCauseWorkbench.colReason')" min-width="180" show-overflow-tooltip />
            </el-table>
          </el-card>

          <el-card shadow="never">
            <template #header>
              <div class="card-header">
                <span>{{ $t('stopCauseWorkbench.orphanAnchorsTitle') }}</span>
              </div>
            </template>
            <el-table :data="orphanAnchors" stripe>
              <el-table-column prop="anchorKey" :label="$t('stopCauseWorkbench.colAnchorKey')" min-width="180" show-overflow-tooltip />
              <el-table-column prop="variableName" :label="$t('stopCauseWorkbench.colVariable')" min-width="160" show-overflow-tooltip />
              <el-table-column prop="reason" :label="$t('stopCauseWorkbench.colReason')" min-width="180" show-overflow-tooltip />
            </el-table>
          </el-card>
        </div>
      </el-tab-pane>

      <el-tab-pane :label="$t('stopCauseWorkbench.tabQPoints')" name="qpoints">
        <div class="panel-grid">
          <el-card shadow="never">
            <template #header>
              <div class="card-header">
                <span>{{ $t('stopCauseWorkbench.semanticSummary') }}</span>
              </div>
            </template>
            <el-descriptions v-if="hasSemanticData" :column="2" border>
              <el-descriptions-item :label="$t('stopCauseWorkbench.semanticFindingCount')">{{ semanticFindings.length }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.llmClassified')">{{ semanticSummary.llmClassifiedCount ?? 0 }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.virtualQ')">{{ semanticVirtualFindings.length }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.handshake')">{{ semanticHandshakeFindings.length }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.metricIoMapping')">{{ semanticIoMappingFindings.length }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.sensorMapping')">{{ semanticSensorMappingFindings.length }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.persistedCount')">{{ persistedSemanticTotal }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.progressStatus')">{{ semanticProgressStatusText }}</el-descriptions-item>
            </el-descriptions>
            <el-empty v-else :description="$t('stopCauseWorkbench.semanticNotRun')" />
          </el-card>

          <el-card shadow="never">
            <template #header>
              <div class="card-header">
                <span>{{ $t('stopCauseWorkbench.stationCoverage') }}</span>
              </div>
            </template>
            <el-table :data="stationCoverageRows" stripe>
              <el-table-column prop="stationCode" :label="$t('stopCauseWorkbench.colStation')" min-width="120" />
              <el-table-column prop="anchorCount" :label="$t('stopCauseWorkbench.colAnchorCount')" width="100" />
            </el-table>
          </el-card>
        </div>

        <el-card shadow="never" class="mt-4" v-if="semanticRunning || semanticProgress">
          <template #header>
            <div class="card-header">
              <span>{{ $t('stopCauseWorkbench.analysisProgress') }}</span>
            </div>
          </template>
          <el-progress
            :percentage="Number(semanticProgress?.progressPercent || semanticProgress?.ProgressPercent || 0)"
            :status="String(semanticProgress?.status || semanticProgress?.Status || '').toLowerCase() === 'failed' ? 'exception' : undefined"
          />
          <div class="secondary-text mt-2">
            {{ $t('stopCauseWorkbench.semanticProgressText', { total: semanticProgress?.totalCandidates ?? semanticProgress?.TotalCandidates ?? 0,
            done: semanticProgress?.completedCandidates ?? semanticProgress?.CompletedCandidates ?? 0,
            found: semanticProgress?.foundCount ?? semanticProgress?.FoundCount ?? 0 }) }}
          </div>
          <div class="secondary-text">
            {{ semanticProgress?.message || semanticProgress?.Message || '-' }}
          </div>
        </el-card>

        <div class="panel-grid mt-4">
          <el-card shadow="never">
            <template #header>
              <div class="card-header">
                <span>{{ $t('stopCauseWorkbench.virtualQResults') }}</span>
              </div>
            </template>
            <el-table :data="semanticVirtualFindings" stripe max-height="420">
              <el-table-column prop="variableName" :label="$t('stopCauseWorkbench.colVariable')" min-width="180" show-overflow-tooltip />
              <el-table-column prop="stationCode" :label="$t('stopCauseWorkbench.colStation')" width="120" />
              <el-table-column prop="semanticLabel" :label="$t('stopCauseWorkbench.colSemanticLabel')" min-width="160" show-overflow-tooltip />
              <el-table-column prop="confidence" :label="$t('stopCauseWorkbench.colConfidence')" width="100" />
              <el-table-column prop="ioMapping" label="IoMapping" min-width="180" show-overflow-tooltip />
            </el-table>
          </el-card>

          <el-card shadow="never">
            <template #header>
              <div class="card-header">
                <span>{{ $t('stopCauseWorkbench.handshakeResults') }}</span>
              </div>
            </template>
            <el-table :data="semanticHandshakeFindings" stripe max-height="420">
              <el-table-column prop="variableName" :label="$t('stopCauseWorkbench.colVariable')" min-width="180" show-overflow-tooltip />
              <el-table-column prop="stationCode" :label="$t('stopCauseWorkbench.colStation')" width="120" />
              <el-table-column prop="semanticLabel" :label="$t('stopCauseWorkbench.colSemanticLabel')" min-width="160" show-overflow-tooltip />
              <el-table-column prop="reason" :label="$t('stopCauseWorkbench.colReason')" min-width="180" show-overflow-tooltip />
              <el-table-column prop="confidence" :label="$t('stopCauseWorkbench.colConfidence')" width="100" />
            </el-table>
          </el-card>
        </div>

        <div class="panel-grid mt-4">
          <el-card shadow="never">
            <template #header>
              <div class="card-header">
                <span>{{ $t('stopCauseWorkbench.llmDiagnosticsTitle') }}</span>
              </div>
            </template>
            <el-descriptions :column="2" border>
              <el-descriptions-item :label="$t('stopCauseWorkbench.totalCandidates')">{{ semanticProgress?.totalCandidates ?? semanticProgress?.TotalCandidates ?? llmDiagnostics.totalContextCount ?? 0 }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.stationFiltered')">{{ llmDiagnostics.stationFilteredCount ?? 0 }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.sentToLlm')">{{ llmDiagnostics.sentToLlmCount ?? 0 }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.suppressed')">{{ llmDiagnostics.suppressedCount ?? 0 }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.llmReturned')">{{ llmDiagnostics.llmReturnedCount ?? 0 }}</el-descriptions-item>
              <el-descriptions-item label="LLM Confirmed">{{ llmDiagnostics.confirmedByLlmCount ?? 0 }}</el-descriptions-item>
              <el-descriptions-item label="LLM Handshake">{{ llmDiagnostics.handshakeByLlmCount ?? 0 }}</el-descriptions-item>
              <el-descriptions-item label="LLM Review">{{ llmDiagnostics.reviewByLlmCount ?? 0 }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.unknownDropped')">{{ llmDiagnostics.unknownDroppedCount ?? 0 }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.stateFlagDropped')">{{ llmDiagnostics.stateFlagDroppedCount ?? 0 }}</el-descriptions-item>
            </el-descriptions>
          </el-card>

          <el-card shadow="never">
            <template #header>
              <div class="card-header">
                <span>{{ $t('stopCauseWorkbench.astSemanticDetails') }}</span>
              </div>
            </template>
            <el-table :data="semanticFindings" stripe max-height="420">
              <el-table-column prop="variableName" :label="$t('stopCauseWorkbench.colVariable')" min-width="180" show-overflow-tooltip />
              <el-table-column prop="category" :label="$t('stopCauseWorkbench.colCategory')" width="140" />
              <el-table-column prop="stationCode" :label="$t('stopCauseWorkbench.colStation')" width="100" />
              <el-table-column prop="blockName" :label="$t('stopCauseWorkbench.colBlockName')" min-width="180" show-overflow-tooltip />
              <el-table-column prop="reason" :label="$t('stopCauseWorkbench.colReason')" min-width="220" show-overflow-tooltip />
            </el-table>
          </el-card>
        </div>
      </el-tab-pane>

      <el-tab-pane :label="$t('stopCauseWorkbench.tabIoMappings')" name="io-mappings">
        <div class="panel-grid">
          <el-card shadow="never">
            <template #header>
              <div class="card-header">
                <span>{{ $t('stopCauseWorkbench.mappingSummary') }}</span>
              </div>
            </template>
            <el-descriptions v-if="semanticIoMappingFindings.length > 0" :column="2" border>
              <el-descriptions-item :label="$t('stopCauseWorkbench.mappingTotal')">{{ semanticIoMappingFindings.length }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.sensorMapping')">{{ semanticSensorMappingFindings.length }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.currentQpointStation')">{{ semanticForm.stationCode || $t('stopCauseWorkbench.allPersistedStations') }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.persistedCount')">{{ persistedSemanticTotal }}</el-descriptions-item>
            </el-descriptions>
            <el-empty v-else :description="$t('stopCauseWorkbench.noIoMappings')" />
          </el-card>

          <el-card shadow="never">
            <template #header>
              <div class="card-header">
                <span>{{ $t('stopCauseWorkbench.mappingTypeDist') }}</span>
              </div>
            </template>
            <el-table :data="stationCoverageRows" stripe>
              <el-table-column prop="stationCode" :label="$t('stopCauseWorkbench.colStation')" min-width="120" />
              <el-table-column prop="anchorCount" :label="$t('stopCauseWorkbench.semanticResultCount')" width="120" />
            </el-table>
          </el-card>
        </div>

        <el-card shadow="never" class="mt-4">
          <template #header>
            <div class="card-header">
              <span>{{ $t('stopCauseWorkbench.ioMappingDetails') }}</span>
            </div>
          </template>
          <el-table :data="semanticIoMappingFindings" stripe max-height="480">
            <el-table-column prop="variableName" :label="$t('stopCauseWorkbench.colLhsVariable')" min-width="220" show-overflow-tooltip />
            <el-table-column prop="stationCode" :label="$t('stopCauseWorkbench.colStation')" width="100" />
            <el-table-column prop="category" :label="$t('stopCauseWorkbench.colCategory')" width="120" />
            <el-table-column prop="blockName" :label="$t('stopCauseWorkbench.colBlockName')" min-width="180" show-overflow-tooltip />
            <el-table-column :label="$t('stopCauseWorkbench.colMappedIOTag')" min-width="220" show-overflow-tooltip>
              <template #default="{ row }">
                {{ (row.relatedIOTags || []).join(', ') || '-' }}
              </template>
            </el-table-column>
            <el-table-column prop="ioMapping" :label="$t('stopCauseWorkbench.colMappingRelation')" min-width="300" show-overflow-tooltip />
            <el-table-column prop="reason" :label="$t('stopCauseWorkbench.colSource')" min-width="220" show-overflow-tooltip />
          </el-table>
        </el-card>

        <el-card shadow="never" class="mt-4">
          <template #header>
            <div class="card-header">
              <span>{{ $t('stopCauseWorkbench.sensorMappingDetails') }}</span>
            </div>
          </template>
          <el-table :data="semanticSensorMappingFindings" stripe max-height="480">
            <el-table-column prop="variableName" :label="$t('stopCauseWorkbench.colSensorVariable')" min-width="220" show-overflow-tooltip />
            <el-table-column prop="stationCode" :label="$t('stopCauseWorkbench.colStation')" width="100" />
            <el-table-column prop="blockName" :label="$t('stopCauseWorkbench.colBlockName')" min-width="180" show-overflow-tooltip />
            <el-table-column prop="semanticLabel" :label="$t('stopCauseWorkbench.colLabel')" min-width="160" show-overflow-tooltip />
            <el-table-column prop="ioMapping" :label="$t('stopCauseWorkbench.colMappingRelation')" min-width="320" show-overflow-tooltip />
          </el-table>
        </el-card>
      </el-tab-pane>

      <el-tab-pane :label="$t('stopCauseWorkbench.tabCompile')" name="compile">
        <el-card shadow="never">
          <template #header>
            <div class="card-header">
              <span>{{ $t('stopCauseWorkbench.compileSummary') }}</span>
            </div>
          </template>
          <el-descriptions v-if="hasCompileData" :column="3" border>
            <el-descriptions-item label="AstSeeds">{{ compileCounts.astSeeds ?? 0 }}</el-descriptions-item>
            <el-descriptions-item label="AstCompileResults">{{ compileCounts.astCompileResults ?? 0 }}</el-descriptions-item>
            <el-descriptions-item label="LlmClassified">{{ compileSummary.llmClassifiedCount ?? 0 }}</el-descriptions-item>
          </el-descriptions>
          <el-empty v-else :description="$t('stopCauseWorkbench.compilePreviewNotGenerated')" />
        </el-card>

        <el-card shadow="never" class="mt-4">
          <template #header>
            <div class="card-header">
              <span>{{ $t('stopCauseWorkbench.compileResults') }}</span>
              <el-tag type="info" size="small">{{ compileQueryMode ? $t('stopCauseWorkbench.persistedQuery') : $t('stopCauseWorkbench.currentPreview') }} {{ $t('stopCauseWorkbench.itemsCount', { n: displayedCompileTotal }) }}</el-tag>
            </div>
          </template>

          <el-form :inline="true" class="control-form" @submit.prevent="handleCompileSearch">
            <el-form-item :label="$t('stopCauseWorkbench.stationCode')">
              <el-input
                v-model="compileSearch.stationCode"
                :placeholder="$t('stopCauseWorkbench.stationCodeExample')"
                clearable
                style="width: 180px"
              />
            </el-form-item>
            <el-form-item :label="$t('stopCauseWorkbench.blockNameKeyword')">
              <el-input
                v-model="compileSearch.blockName"
                :placeholder="$t('stopCauseWorkbench.compileBlockPlaceholder')"
                clearable
                style="width: 220px"
              />
            </el-form-item>
            <el-form-item :label="$t('stopCauseWorkbench.colVariable')">
              <el-input
                v-model="compileSearch.variableName"
                :placeholder="$t('stopCauseWorkbench.anchorVarPlaceholder')"
                clearable
                style="width: 220px"
              />
            </el-form-item>
            <el-form-item :label="$t('stopCauseWorkbench.colCompileStatus')">
              <el-select v-model="compileSearch.compileStatus" clearable :placeholder="$t('stopCauseWorkbench.allStatus')" style="width: 180px">
                <el-option label="Compiled" value="Compiled" />
                <el-option label="PartiallyCompiled" value="PartiallyCompiled" />
                <el-option label="Unsupported" value="Unsupported" />
                <el-option label="Failed" value="Failed" />
                <el-option label="Pending" value="Pending" />
              </el-select>
            </el-form-item>
          </el-form>

          <div class="action-row">
            <el-button type="primary" @click="handleCompileSearch">{{ $t('stopCauseWorkbench.filterCompile') }}</el-button>
            <el-button @click="handleCompileReset">{{ $t('stopCauseWorkbench.resetFilter') }}</el-button>
            <el-button v-if="compileQueryMode && (compilePayload.compileResults || []).length > 0" @click="useCompilePreviewRows">{{ $t('stopCauseWorkbench.backToPreview') }}</el-button>
          </div>

          <div class="mt-4 writer-hint">
            {{ $t('stopCauseWorkbench.compileHint') }}
          </div>

          <el-table :data="displayedCompileRows" stripe class="mt-4" v-loading="compileQueryLoading">
            <el-table-column prop="variableName" :label="$t('stopCauseWorkbench.colVariable')" min-width="180" show-overflow-tooltip />
            <el-table-column prop="anchorType" :label="$t('stopCauseWorkbench.colAnchorType')" width="130" />
            <el-table-column prop="stationCode" :label="$t('stopCauseWorkbench.colStation')" width="120" />
            <el-table-column prop="blockNameSummary" :label="$t('stopCauseWorkbench.colBlockName')" min-width="180" show-overflow-tooltip />
            <el-table-column prop="compileStatus" :label="$t('stopCauseWorkbench.colCompileStatus')" width="130" />
            <el-table-column prop="compilerName" :label="$t('stopCauseWorkbench.colCompiler')" width="140" />
            <el-table-column prop="coverageReportCount" :label="$t('stopCauseWorkbench.colCoverageReport')" width="110" />
            <el-table-column :label="$t('stopCauseWorkbench.colAstRoot')" min-width="260">
              <template #default="{ row }">
                <div class="message-list">
                  <div>{{ row.rootNode?.nodeType || '-' }}</div>
                  <div class="secondary-text">{{ row.rootNode?.displayName || row.rootNode?.variableName || '-' }}</div>
                  <div v-if="row.rootNode?.children?.length" class="secondary-text">
                    {{ $t('stopCauseWorkbench.childCount', { n: row.rootNode.children.length }) }}
                  </div>
                </div>
              </template>
            </el-table-column>
            <el-table-column :label="$t('stopCauseWorkbench.colCoveragePreview')" min-width="360">
              <template #default="{ row }">
                <div class="message-list">
                  <div v-for="report in row.coverageReports || []" :key="`${report.logicUnitId}-${report.title}-${report.networkId}`" class="coverage-report">
                    <div>{{ report.blockName || '-' }} / {{ report.unitType || report.sourceType || '-' }}</div>
                    <div class="secondary-text">{{ report.title || $t('stopCauseWorkbench.unnamedFragment') }}</div>
                    <div class="secondary-text">{{ $t('stopCauseWorkbench.inputLabel') }}: {{ (report.inputVariables || []).join(', ') || '-' }}</div>
                    <div class="secondary-text">{{ $t('stopCauseWorkbench.outputLabel') }}: {{ (report.outputVariables || []).join(', ') || '-' }}</div>
                    <div class="raw-snippet compact">{{ report.rawContentExcerpt || report.unsupportedReason || '-' }}</div>
                  </div>
                </div>
              </template>
            </el-table-column>
            <el-table-column :label="$t('stopCauseWorkbench.colMessages')" min-width="220">
              <template #default="{ row }">
                <div class="message-list">
                  <div v-for="message in row.messages || []" :key="message">{{ message }}</div>
                </div>
              </template>
            </el-table-column>
          </el-table>

          <div class="pagination-row mt-4">
            <el-pagination
              v-model:current-page="compilePagination.page"
              v-model:page-size="compilePagination.limit"
              :total="compilePagination.total"
              :page-sizes="[10, 20, 50, 100]"
              layout="total, sizes, prev, pager, next"
              @current-change="onCompilePageChange"
              @size-change="onCompilePageSizeChange"
            />
          </div>
        </el-card>
      </el-tab-pane>

      <el-tab-pane :label="$t('stopCauseWorkbench.tabMonitoring')" name="monitoring">
        <el-card shadow="never">
          <template #header>
            <div class="card-header">
              <span>{{ $t('stopCauseWorkbench.monitoringRun') }}</span>
              <el-tag type="info" size="small">{{ $t('stopCauseWorkbench.manualSampleTag') }}</el-tag>
            </div>
          </template>

          <el-form :inline="true" class="control-form">
            <el-form-item :label="$t('stopCauseWorkbench.monitoringStation')">
              <el-input
                v-model="monitoringForm.stationCode"
                :placeholder="$t('stopCauseWorkbench.monitoringStationPlaceholder')"
                clearable
                style="width: 180px"
              />
            </el-form-item>
            <el-form-item :label="$t('stopCauseWorkbench.colStationMode')">
              <el-input
                v-model="monitoringForm.stationMode"
                :placeholder="$t('stopCauseWorkbench.stationModePlaceholder')"
                clearable
                style="width: 160px"
              />
            </el-form-item>
            <el-form-item label="RuntimeNode">
              <el-input
                v-model="monitoringForm.runtimeNodeName"
                :placeholder="$t('stopCauseWorkbench.optional')"
                clearable
                style="width: 180px"
              />
            </el-form-item>
            <el-form-item :label="$t('stopCauseWorkbench.signalLimit')">
              <el-input-number v-model="monitoringForm.signalLimit" :min="20" :max="500" :step="20" />
            </el-form-item>
            <el-form-item :label="$t('stopCauseWorkbench.timelineLimit')">
              <el-input-number v-model="monitoringForm.timelineLimit" :min="10" :max="200" :step="10" />
            </el-form-item>
            <el-form-item :label="$t('stopCauseWorkbench.persistHistory')">
              <el-switch v-model="monitoringForm.persistHistory" />
            </el-form-item>
            <el-form-item :label="$t('stopCauseWorkbench.persistSnapshot')">
              <el-switch v-model="monitoringForm.persistSnapshot" />
            </el-form-item>
            <el-form-item :label="$t('stopCauseWorkbench.persistEvents')">
              <el-switch v-model="monitoringForm.persistEvents" />
            </el-form-item>
          </el-form>

          <div class="action-row">
            <el-button type="primary" :loading="monitoringLoading" @click="runMonitoringCycle">{{ $t('stopCauseWorkbench.runCycle') }}</el-button>
            <el-button :loading="monitoringLoading" @click="refreshMonitoringData(true)">{{ $t('stopCauseWorkbench.refreshMonitoring') }}</el-button>
          </div>

          <div class="mt-4 writer-hint">
            {{ $t('stopCauseWorkbench.monitoringHint') }}
          </div>
        </el-card>

        <div class="panel-grid mt-4">
          <el-card shadow="never">
            <template #header>
              <div class="card-header">
                <span>{{ $t('stopCauseWorkbench.lastRunResult') }}</span>
              </div>
            </template>
            <el-descriptions v-if="monitoringRunResult" :column="2" border>
              <el-descriptions-item :label="$t('stopCauseWorkbench.colStation')">{{ monitoringRunResult.stationCode || monitoringRunResult.StationCode || '-' }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.colStatus')">{{ monitoringRunResult.status || monitoringRunResult.Status || '-' }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.sampleTime')">{{ monitoringRunResult.sampleTime || monitoringRunResult.SampleTime || '-' }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.changedSignals')">{{ monitoringRunResult.changedSignalCount || monitoringRunResult.ChangedSignalCount || 0 }}</el-descriptions-item>
              <el-descriptions-item label="Bundle">{{ monitoringRunResult.bundleId || monitoringRunResult.BundleId || '-' }}</el-descriptions-item>
              <el-descriptions-item label="Snapshot">{{ monitoringRunResult.snapshotId || monitoringRunResult.SnapshotId || '-' }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.silentLabel')">{{ (monitoringRunResult.isSilent || monitoringRunResult.IsSilent) ? $t('common.yes') : $t('common.no') }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.missingSymbols')">{{ monitoringRunResult.missingSymbolCount || monitoringRunResult.MissingSymbolCount || 0 }}</el-descriptions-item>
            </el-descriptions>
            <el-empty v-else :description="$t('stopCauseWorkbench.monitoringNotRun')" />
          </el-card>

          <el-card shadow="never">
            <template #header>
              <div class="card-header">
                <span>{{ $t('stopCauseWorkbench.stationOverview') }}</span>
              </div>
            </template>
            <el-descriptions v-if="monitoringOverviewState" :column="2" border>
              <el-descriptions-item :label="$t('stopCauseWorkbench.colStation')">{{ resolveMonitoringStationCode() || '-' }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.runStatus')">{{ monitoringOverviewState.status || monitoringOverviewState.Status || '-' }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.colStationMode')">{{ monitoringOverviewState.stationMode || monitoringOverviewState.StationMode || '-' }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.silentSeconds')">{{ monitoringOverviewState.silentSeconds || monitoringOverviewState.SilentSeconds || 0 }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.lastSample')">{{ monitoringOverviewState.lastSampleTime || monitoringOverviewState.LastSampleTime || '-' }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.lastAction')">{{ monitoringOverviewState.lastActionTime || monitoringOverviewState.LastActionTime || '-' }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.lastBundle')">{{ monitoringOverviewState.lastBundleTime || monitoringOverviewState.LastBundleTime || '-' }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.lastSnapshot')">{{ monitoringOverviewState.lastSnapshotId || monitoringOverviewState.LastSnapshotId || '-' }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.runtimeStatus')">{{ monitoringOverviewRuntimeLease?.status || monitoringOverviewRuntimeLease?.Status || '-' }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.runtimeNode')">{{ monitoringOverviewRuntimeLease?.nodeName || monitoringOverviewRuntimeLease?.NodeName || '-' }}</el-descriptions-item>
            </el-descriptions>
            <el-empty v-else :description="$t('stopCauseWorkbench.selectStationFirst')" />
          </el-card>
        </div>

        <div class="panel-grid mt-4">
          <el-card shadow="never">
            <template #header>
              <div class="card-header">
                <span>{{ $t('stopCauseWorkbench.monitoringStationsTitle') }}</span>
                <el-tag type="info" size="small">{{ $t('stopCauseWorkbench.countUnit', { n: monitoringStations.length }) }}</el-tag>
              </div>
            </template>
            <el-table :data="monitoringStations" stripe max-height="360">
              <el-table-column prop="stationCode" :label="$t('stopCauseWorkbench.colStation')" min-width="120" />
              <el-table-column prop="status" :label="$t('stopCauseWorkbench.colStatus')" width="120" />
              <el-table-column prop="stationMode" :label="$t('stopCauseWorkbench.colMode')" width="120" />
              <el-table-column :label="$t('stopCauseWorkbench.silentLabel')" width="90">
                <template #default="{ row }">
                  {{ row.isSilent ? $t('common.yes') : $t('common.no') }}
                </template>
              </el-table-column>
              <el-table-column prop="silentSeconds" :label="$t('stopCauseWorkbench.silentSeconds')" width="100" />
              <el-table-column prop="lastSampleTime" :label="$t('stopCauseWorkbench.lastSample')" min-width="170" show-overflow-tooltip />
              <el-table-column :label="$t('common.operate')" width="110" fixed="right">
                <template #default="{ row }">
                  <el-button link type="primary" @click="applyMonitoringStation(row.stationCode)">{{ $t('stopCauseWorkbench.viewLabel') }}</el-button>
                </template>
              </el-table-column>
            </el-table>
          </el-card>

          <el-card shadow="never">
            <template #header>
              <div class="card-header">
                <span>{{ $t('stopCauseWorkbench.latestEvent') }}</span>
              </div>
            </template>
            <el-descriptions v-if="monitoringLatestEvent" :column="1" border>
              <el-descriptions-item :label="$t('stopCauseWorkbench.eventType')">{{ monitoringLatestEvent.eventType || '-' }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.eventLevel')">{{ monitoringLatestEvent.eventLevel || '-' }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.triggeredAt')">{{ monitoringLatestEvent.triggeredAt || '-' }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.lastChangedSignal')">{{ monitoringLatestEvent.lastChangedSignal || '-' }}</el-descriptions-item>
              <el-descriptions-item :label="$t('stopCauseWorkbench.diagnosisStatus')">{{ monitoringLatestEvent.diagnosisStatus || '-' }}</el-descriptions-item>
            </el-descriptions>
            <el-empty v-else :description="$t('stopCauseWorkbench.noEvents')" />
          </el-card>
        </div>

        <div class="panel-grid mt-4">
          <el-card shadow="never">
            <template #header>
              <div class="card-header">
                <span>{{ $t('stopCauseWorkbench.recentChanges') }}</span>
                <el-tag type="info" size="small">{{ $t('stopCauseWorkbench.itemsCount', { n: monitoringChanges.length }) }}</el-tag>
              </div>
            </template>
            <el-table :data="monitoringChanges" stripe max-height="360">
              <el-table-column prop="signalKey" label="SignalKey" min-width="180" show-overflow-tooltip />
              <el-table-column prop="oldValue" :label="$t('stopCauseWorkbench.oldValue')" width="100" />
              <el-table-column prop="newValue" :label="$t('stopCauseWorkbench.newValue')" width="100" />
              <el-table-column prop="groupName" :label="$t('stopCauseWorkbench.colGroup')" width="140" show-overflow-tooltip />
              <el-table-column prop="changedAt" :label="$t('stopCauseWorkbench.changedAt')" min-width="170" show-overflow-tooltip />
            </el-table>
          </el-card>

          <el-card shadow="never">
            <template #header>
              <div class="card-header">
                <span>{{ $t('stopCauseWorkbench.recentBundles') }}</span>
                <el-tag type="info" size="small">{{ $t('stopCauseWorkbench.itemsCount', { n: monitoringBundles.length }) }}</el-tag>
              </div>
            </template>
            <el-table :data="monitoringBundles" stripe max-height="360">
              <el-table-column prop="bundleId" label="BundleId" min-width="220" show-overflow-tooltip />
              <el-table-column prop="bundleLabel" :label="$t('stopCauseWorkbench.colLabel')" min-width="160" show-overflow-tooltip />
              <el-table-column prop="concurrencyCount" :label="$t('stopCauseWorkbench.concurrency')" width="90" />
              <el-table-column prop="startTime" :label="$t('stopCauseWorkbench.colStart')" min-width="160" show-overflow-tooltip />
              <el-table-column prop="endTime" :label="$t('stopCauseWorkbench.colEnd')" min-width="160" show-overflow-tooltip />
            </el-table>
          </el-card>
        </div>

        <el-card shadow="never" class="mt-4">
          <template #header>
            <div class="card-header">
              <span>{{ $t('stopCauseWorkbench.signalMirror') }}</span>
              <el-tag type="info" size="small">{{ $t('stopCauseWorkbench.itemsCount', { n: monitoringSignals.length }) }}</el-tag>
            </div>
          </template>
          <el-table :data="monitoringSignals" stripe max-height="420">
            <el-table-column prop="signalKey" label="SignalKey" min-width="180" show-overflow-tooltip />
            <el-table-column prop="variableName" :label="$t('stopCauseWorkbench.colVariable')" min-width="220" show-overflow-tooltip />
            <el-table-column prop="currentValue" :label="$t('stopCauseWorkbench.currentValue')" width="110" />
            <el-table-column prop="previousValue" :label="$t('stopCauseWorkbench.previousValue')" width="110" />
            <el-table-column prop="quality" :label="$t('stopCauseWorkbench.colQuality')" width="100" />
            <el-table-column prop="groupName" :label="$t('stopCauseWorkbench.colGroup')" width="140" show-overflow-tooltip />
            <el-table-column prop="signalType" :label="$t('stopCauseWorkbench.colType')" width="120" />
            <el-table-column prop="lastChangeTime" :label="$t('stopCauseWorkbench.lastChange')" min-width="170" show-overflow-tooltip />
          </el-table>
        </el-card>

        <div class="panel-grid mt-4">
          <el-card shadow="never">
            <template #header>
              <div class="card-header">
                <span>{{ $t('stopCauseWorkbench.eventTimeline') }}</span>
              </div>
            </template>
            <el-table :data="monitoringEvents" stripe max-height="360">
              <el-table-column prop="eventType" :label="$t('stopCauseWorkbench.colType')" width="110" />
              <el-table-column prop="eventLevel" :label="$t('stopCauseWorkbench.colLevel')" width="100" />
              <el-table-column prop="stationMode" :label="$t('stopCauseWorkbench.colMode')" width="120" />
              <el-table-column prop="lastChangedSignal" :label="$t('stopCauseWorkbench.colSignal')" min-width="180" show-overflow-tooltip />
              <el-table-column prop="triggeredAt" :label="$t('stopCauseWorkbench.triggeredAt')" min-width="170" show-overflow-tooltip />
            </el-table>
          </el-card>

          <el-card shadow="never">
            <template #header>
              <div class="card-header">
                <span>Snapshot</span>
              </div>
            </template>
            <div class="raw-snippet monitoring-snapshot">{{ monitoringSnapshotText }}</div>
          </el-card>
        </div>
      </el-tab-pane>

      <!-- ==================== 覆盖度 Tab ==================== -->
      <el-tab-pane :label="$t('stopCauseWorkbench.tabCoverage')" name="coverage">
        <div style="margin-bottom: 12px; display: flex; gap: 12px; align-items: center; flex-wrap: wrap;">
          <el-input v-model="coverageSearch.blockName" :placeholder="$t('stopCauseWorkbench.coverageBlockName')" clearable style="width: 180px;" />
          <el-select v-model="coverageSearch.blockType" :placeholder="$t('stopCauseWorkbench.blockType')" clearable style="width: 130px;">
            <el-option label="FB" value="FB" />
            <el-option label="FC" value="FC" />
            <el-option label="OB" value="OB" />
            <el-option label="DB" value="DB" />
            <el-option label="UDT" value="UDT" />
          </el-select>
          <el-select v-model="coverageSearch.programmingLanguage" :placeholder="$t('stopCauseWorkbench.programmingLanguage')" clearable style="width: 130px;">
            <el-option label="LAD" value="LAD" />
            <el-option label="FBD" value="FBD" />
            <el-option label="STL" value="STL" />
            <el-option label="SCL" value="SCL" />
            <el-option label="GRAPH" value="GRAPH" />
          </el-select>
          <el-button type="primary" @click="loadCoverageData">{{ $t('stopCauseWorkbench.queryBtn') }}</el-button>
        </div>

        <el-row :gutter="16" style="margin-bottom: 16px;" v-if="coverageStats">
          <el-col :span="6">
            <el-statistic :title="$t('stopCauseWorkbench.totalBlocks')" :value="coverageStats.total" />
          </el-col>
          <el-col :span="6">
            <el-statistic :title="$t('stopCauseWorkbench.assigned')" :value="coverageStats.assigned">
              <template #suffix>
                <span style="font-size: 14px; color: #67c23a;">✓</span>
              </template>
            </el-statistic>
          </el-col>
          <el-col :span="6">
            <el-statistic :title="$t('stopCauseWorkbench.unassigned')" :value="coverageStats.unassigned">
              <template #suffix>
                <span style="font-size: 14px; color: #f56c6c;">✗</span>
              </template>
            </el-statistic>
          </el-col>
          <el-col :span="6">
            <el-statistic :title="$t('stopCauseWorkbench.coverageRate')" :value="coverageStats.rate" />
          </el-col>
        </el-row>

        <el-table v-if="coverageByType && coverageByType.length"
          :data="coverageByType" border size="small" style="margin-bottom: 16px;">
          <el-table-column prop="blockType" :label="$t('stopCauseWorkbench.blockType')" width="100" />
          <el-table-column prop="total" :label="$t('stopCauseWorkbench.totalCountLabel')" width="80" />
          <el-table-column prop="assigned" :label="$t('stopCauseWorkbench.assigned')" width="80" />
          <el-table-column prop="unassigned" :label="$t('stopCauseWorkbench.unassigned')" width="80" />
          <el-table-column :label="$t('stopCauseWorkbench.coverageRate')" width="120">
            <template #default="{ row }">
              <el-progress
                :percentage="row.total > 0 ? Math.round(row.assigned / row.total * 100) : 0"
                :stroke-width="14"
                :text-inside="true"
                style="width: 100%;"
              />
            </template>
          </el-table-column>
        </el-table>

        <el-table :data="coverageRows" border size="small"
          v-loading="coverageLoading" max-height="520">
          <el-table-column prop="blockName" :label="$t('stopCauseWorkbench.coverageBlockName')" min-width="200" show-overflow-tooltip />
          <el-table-column prop="blockType" :label="$t('stopCauseWorkbench.colType')" width="70" />
          <el-table-column prop="blockNumber" :label="$t('stopCauseWorkbench.colNumber')" width="70" />
          <el-table-column prop="programmingLanguage" :label="$t('stopCauseWorkbench.colLanguage')" width="80" />
          <el-table-column prop="description" :label="$t('stopCauseWorkbench.colDescription')" min-width="200" show-overflow-tooltip />
          <el-table-column prop="isPotentialEntry" :label="$t('stopCauseWorkbench.entryBlock')" width="70">
            <template #default="{ row }">
              <el-tag v-if="row.isPotentialEntry" type="warning" size="small">{{ $t('stopCauseWorkbench.entryTag') }}</el-tag>
            </template>
          </el-table-column>
          <el-table-column prop="callsOutCount" :label="$t('stopCauseWorkbench.outDegree')" width="60" />
          <el-table-column prop="calledByCount" :label="$t('stopCauseWorkbench.inDegree')" width="60" />
          <el-table-column prop="networksCount" :label="$t('stopCauseWorkbench.networkCountLabel')" width="90" />
        </el-table>

        <div style="margin-top: 12px; display: flex; justify-content: flex-end;" v-if="coverageFilteredTotal > 0">
          <el-pagination
            v-model:current-page="coveragePagination.page"
            v-model:page-size="coveragePagination.limit"
            :total="coverageFilteredTotal"
            :page-sizes="[20, 50, 100]"
            layout="total, sizes, prev, pager, next"
            @size-change="loadCoverageData"
            @current-change="loadCoverageData"
          />
        </div>
      </el-tab-pane>

      <el-tab-pane :label="$t('stopCauseWorkbench.tabAcceptance')" name="acceptance">
        <div class="acceptance-grid">
          <el-alert
            v-for="finding in acceptanceFindings"
            :key="finding.title"
            :title="finding.title"
            :description="finding.description"
            :type="finding.type"
            show-icon
            :closable="false"
          />
        </div>

        <el-card shadow="never" class="mt-4">
          <template #header>
            <div class="card-header">
              <span>{{ $t('stopCauseWorkbench.acceptanceAdvice') }}</span>
            </div>
          </template>
          <el-timeline>
            <el-timeline-item timestamp="1" placement="top">
              {{ $t('stopCauseWorkbench.acceptanceStep1') }}
            </el-timeline-item>
            <el-timeline-item timestamp="2" placement="top">
              {{ $t('stopCauseWorkbench.acceptanceStep2') }}
            </el-timeline-item>
            <el-timeline-item timestamp="3" placement="top">
              {{ $t('stopCauseWorkbench.acceptanceStep3') }}
            </el-timeline-item>
            <el-timeline-item timestamp="4" placement="top">
              {{ $t('stopCauseWorkbench.acceptanceStep4') }}
            </el-timeline-item>
          </el-timeline>
        </el-card>
      </el-tab-pane>
    </el-tabs>

    <el-dialog
      v-model="stationWriterAstDialogVisible"
      :title="$t('stopCauseWorkbench.stationAstDialogTitle')"
      width="88%"
      destroy-on-close
    >
      <div v-loading="stationWriterAstDialogLoading">
        <el-descriptions :column="3" border>
          <el-descriptions-item :label="$t('stopCauseWorkbench.colStation')">{{ stationWriterAstContext.stationCode || '-' }}</el-descriptions-item>
          <el-descriptions-item :label="$t('stopCauseWorkbench.colBlockCount')">{{ stationWriterAstContext.blockCount ?? 0 }}</el-descriptions-item>
          <el-descriptions-item :label="$t('stopCauseWorkbench.colLogicUnit')">{{ stationWriterAstContext.logicUnitCount ?? 0 }}</el-descriptions-item>
        </el-descriptions>

        <div class="dialog-tip mt-3">
          {{ $t('stopCauseWorkbench.stationAstDialogTip') }}
        </div>

        <el-table :data="stationWriterAstRows" stripe class="mt-4">
          <el-table-column prop="blockName" :label="$t('stopCauseWorkbench.colBlockName')" min-width="180" show-overflow-tooltip />
          <el-table-column prop="networkId" :label="$t('stopCauseWorkbench.colNetwork')" width="90" />
          <el-table-column prop="unitType" :label="$t('stopCauseWorkbench.colUnitType')" width="140" />
          <el-table-column prop="title" :label="$t('common.title')" min-width="180" show-overflow-tooltip />
          <el-table-column prop="writeVarsCount" :label="$t('stopCauseWorkbench.colWriteCount')" width="90" />
          <el-table-column :label="$t('stopCauseWorkbench.astStatus')" width="120">
            <template #default="{ row }">
              {{ row.ast?.compileStatus || row.ast?.CompileStatus || '-' }}
            </template>
          </el-table-column>
          <el-table-column :label="$t('stopCauseWorkbench.astSummaryCol')" min-width="240" show-overflow-tooltip>
            <template #default="{ row }">
              <span>{{ row.astSummary }}</span>
            </template>
          </el-table-column>
          <el-table-column :label="$t('stopCauseWorkbench.colSemanticResult')" min-width="220" show-overflow-tooltip>
            <template #default="{ row }">
              <span>{{ row.semanticSummary || '-' }}</span>
            </template>
          </el-table-column>
          <el-table-column :label="$t('stopCauseWorkbench.colRawSnippet')" min-width="320">
            <template #default="{ row }">
              <div class="raw-snippet compact">{{ row.rawContent || '-' }}</div>
            </template>
          </el-table-column>
          <el-table-column label="AST JSON" min-width="320">
            <template #default="{ row }">
              <div class="raw-snippet compact">{{ row.ast?.rootNodeJson || row.ast?.RootNodeJson || '-' }}</div>
            </template>
          </el-table-column>
        </el-table>

        <div class="pagination-row mt-4">
          <el-pagination
            v-model:current-page="stationWriterAstPagination.page"
            v-model:page-size="stationWriterAstPagination.limit"
            :total="stationWriterAstPagination.total"
            :page-sizes="[10, 20, 50]"
            layout="total, sizes, prev, pager, next"
            @current-change="onStationWriterAstPageChange"
            @size-change="onStationWriterAstPageSizeChange"
          />
        </div>

        <el-empty v-if="!stationWriterAstDialogLoading && stationWriterAstRows.length === 0" :description="$t('stopCauseWorkbench.noStationAstData')" />
      </div>
    </el-dialog>

    <!-- 一键分析进度弹窗 -->
    <el-dialog
      v-model="fullAnalysisDialogVisible"
      :title="$t('stopCauseWorkbench.fullAnalysisDialogTitle')"
      width="520px"
      :close-on-click-modal="false"
      :close-on-press-escape="false"
      :show-close="fullAnalysisProgress?.status !== 'Running'"
      @closed="stopFullAnalysisPolling"
    >
      <div v-if="fullAnalysisProgress" style="padding: 8px 0;">
        <div style="margin-bottom: 12px; font-size: 14px; color: #606266;">
          {{ $t('stopCauseWorkbench.stepOf', { current: fullAnalysisProgress.currentStep ?? fullAnalysisProgress.CurrentStep ?? 0,
          total: fullAnalysisProgress.totalSteps ?? fullAnalysisProgress.TotalSteps ?? 5 }) }}
          <strong>{{ fullAnalysisProgress.stepName ?? fullAnalysisProgress.StepName ?? '' }}</strong>
        </div>
        <el-progress
          :percentage="fullAnalysisProgress.percentage ?? fullAnalysisProgress.Percentage ?? 0"
          :status="
            (fullAnalysisProgress.status ?? fullAnalysisProgress.Status) === 'Completed' ? 'success'
            : (fullAnalysisProgress.status ?? fullAnalysisProgress.Status) === 'Failed' ? 'exception'
            : undefined
          "
          :stroke-width="20"
          :text-inside="true"
          style="margin-bottom: 12px;"
        />
        <div style="font-size: 13px; color: #909399;">
          {{ fullAnalysisProgress.message ?? fullAnalysisProgress.Message ?? '' }}
        </div>

        <el-alert
          v-if="(fullAnalysisProgress.status ?? fullAnalysisProgress.Status) === 'Failed'"
          type="error"
          :title="fullAnalysisProgress.errorMessage ?? fullAnalysisProgress.ErrorMessage ?? $t('stopCauseWorkbench.analysisError')"
          :closable="false"
          style="margin-top: 12px;"
        />

        <template v-if="(fullAnalysisProgress.status ?? fullAnalysisProgress.Status) === 'Completed'">
          <el-divider />
          <el-descriptions :column="2" border size="small">
            <el-descriptions-item :label="$t('stopCauseWorkbench.stationCount')">
              {{ (fullAnalysisProgress.summary ?? fullAnalysisProgress.Summary)?.stationCount
                ?? (fullAnalysisProgress.summary ?? fullAnalysisProgress.Summary)?.StationCount ?? '-' }}
            </el-descriptions-item>
            <el-descriptions-item :label="$t('stopCauseWorkbench.astSeeds')">
              {{ (fullAnalysisProgress.summary ?? fullAnalysisProgress.Summary)?.astSeedCount
                ?? (fullAnalysisProgress.summary ?? fullAnalysisProgress.Summary)?.AstSeedCount ?? '-' }}
            </el-descriptions-item>
            <el-descriptions-item :label="$t('stopCauseWorkbench.astCompile')">
              {{ (fullAnalysisProgress.summary ?? fullAnalysisProgress.Summary)?.astCompileResultCount
                ?? (fullAnalysisProgress.summary ?? fullAnalysisProgress.Summary)?.AstCompileResultCount ?? '-' }}
            </el-descriptions-item>
            <el-descriptions-item :label="$t('stopCauseWorkbench.anchorBindings')">
              {{ (fullAnalysisProgress.summary ?? fullAnalysisProgress.Summary)?.anchorBindingCount
                ?? (fullAnalysisProgress.summary ?? fullAnalysisProgress.Summary)?.AnchorBindingCount ?? '-' }}
            </el-descriptions-item>
          </el-descriptions>
        </template>
      </div>
    </el-dialog>

    <!-- 批量Q点提取进度弹窗 -->
    <el-dialog
      v-model="batchDiscoverDialogVisible"
      :title="$t('stopCauseWorkbench.batchDiscoverTitle')"
      width="520px"
      :close-on-click-modal="false"
      :close-on-press-escape="!batchDiscoverRunning"
      :show-close="!batchDiscoverRunning"
      @closed="stopBatchDiscoverPolling"
    >
      <div v-if="batchDiscoverProgress" style="padding: 8px 0;">
        <div style="margin-bottom: 12px; font-size: 14px; color: #606266;">
          {{ batchDiscoverProgress.stepName ?? batchDiscoverProgress.StepName ?? $t('stopCauseWorkbench.initializing') }}
        </div>
        <el-progress
          :percentage="batchDiscoverProgress.percentage ?? batchDiscoverProgress.Percentage ?? 0"
          :status="
            (batchDiscoverProgress.status ?? batchDiscoverProgress.Status) === 'Completed' ? 'success'
            : (batchDiscoverProgress.status ?? batchDiscoverProgress.Status) === 'Failed' ? 'exception'
            : undefined
          "
          :stroke-width="20"
          :text-inside="true"
          style="margin-bottom: 12px;"
        />
        <div style="font-size: 13px; color: #909399;">
          {{ batchDiscoverProgress.message ?? batchDiscoverProgress.Message ?? '' }}
        </div>

        <el-alert
          v-if="(batchDiscoverProgress.status ?? batchDiscoverProgress.Status) === 'Failed'"
          type="error"
          :title="batchDiscoverProgress.errorMessage ?? batchDiscoverProgress.ErrorMessage ?? $t('stopCauseWorkbench.batchDiscoverError')"
          :closable="false"
          style="margin-top: 12px;"
        />

        <template v-if="(batchDiscoverProgress.status ?? batchDiscoverProgress.Status) === 'Completed'">
          <el-divider />
          <el-descriptions :column="2" border size="small">
            <el-descriptions-item :label="$t('stopCauseWorkbench.stationCount')">
              {{ (batchDiscoverProgress.summary ?? batchDiscoverProgress.Summary)?.stationCount
                ?? (batchDiscoverProgress.summary ?? batchDiscoverProgress.Summary)?.StationCount ?? '-' }}
            </el-descriptions-item>
            <el-descriptions-item :label="$t('stopCauseWorkbench.anchorBindings')">
              {{ (batchDiscoverProgress.summary ?? batchDiscoverProgress.Summary)?.anchorBindingCount
                ?? (batchDiscoverProgress.summary ?? batchDiscoverProgress.Summary)?.AnchorBindingCount ?? '-' }}
            </el-descriptions-item>
          </el-descriptions>
        </template>

        <div v-if="batchDiscoverRunning" style="margin-top: 12px; font-size: 12px; color: #c0c4cc;">
          {{ $t('stopCauseWorkbench.backgroundTaskTip') }}
        </div>
      </div>
    </el-dialog>
  </div>
</template>

<style scoped>
.stop-cause-workbench {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.control-card,
.panel-grid,
.acceptance-grid {
  width: 100%;
}

.card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.dialog-tip {
  color: var(--el-text-color-secondary);
  font-size: 13px;
}

.control-form {
  margin-bottom: 12px;
}

.action-row {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
}

.metric-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
  gap: 12px;
  margin-top: 16px;
}

.form-tip {
  margin-left: 8px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.writer-hint {
  color: var(--el-text-color-secondary);
  font-size: 13px;
}

.metric-item {
  padding: 14px 16px;
  border-radius: 10px;
  background: linear-gradient(135deg, #f5f7fa 0%, #eef3f8 100%);
  border: 1px solid var(--el-border-color-lighter);
}

.metric-label {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.metric-value {
  margin-top: 6px;
  color: var(--el-text-color-primary);
  font-size: 26px;
  font-weight: 600;
  line-height: 1;
}

.panel-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(360px, 1fr));
  gap: 16px;
}

.acceptance-grid {
  display: grid;
  gap: 12px;
}

.message-list {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.secondary-text {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.coverage-report {
  padding: 8px 10px;
  border-radius: 8px;
  background: var(--el-fill-color-light);
}

.raw-snippet {
  max-height: 132px;
  overflow: auto;
  white-space: pre-wrap;
  word-break: break-word;
  font-family: Consolas, 'Courier New', monospace;
  font-size: 12px;
  line-height: 1.45;
}

.raw-snippet.compact {
  max-height: 72px;
}

.raw-snippet.monitoring-snapshot {
  max-height: 360px;
}

.pagination-row {
  display: flex;
  justify-content: flex-end;
  margin-top: 16px;
}

.mt-4 {
  margin-top: 16px;
}

:deep(.el-form-item) {
  margin-bottom: 8px;
}

@media (max-width: 768px) {
  .panel-grid {
    grid-template-columns: 1fr;
  }

  .action-row {
    flex-direction: column;
    align-items: stretch;
  }
}
</style>