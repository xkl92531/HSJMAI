<route lang="yaml">
meta:
  title: 工位Q点监控
</route>

<script setup lang="ts">
import * as signalR from '@microsoft/signalr'
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import ProjectAPI from '@/api/modules/PLC/project'
import StopCauseDiagnosisAPI from '@/api/modules/PLC/stopCauseDiagnosis'
import dayjs from '@/utils/dayjs'
import messages from './monitoring.lang'

type GenericRecord = Record<string, any>

type StoryItem = {
  id: string
  kind: 'change' | 'bundle' | 'event'
  eventId?: string
  kindLabel: string
  title: string
  summary: string
  time: string
  timeText: string
  agoText: string
  intensity: number
  tags: string[]
}

type LearningSample = {
  id: string
  title: string
  subtitle: string
  insight: string
  cues: string[]
  tone: 'accent' | 'warning' | 'info'
}

type LivePulseItem = {
  id: string
  title: string
  summary: string
  stationCode: string
  time: string
  tone: 'success' | 'warning' | 'danger' | 'info'
}

type AcceptanceStepStatus = 'idle' | 'running' | 'success' | 'warning' | 'error'

type AcceptanceStep = {
  key: string
  title: string
  status: AcceptanceStepStatus
  detail: string
}

type MonitoringSignal = GenericRecord & {
  signalKey: string
  variableName: string
  currentValue: any
  previousValue: any
  lastChangeTime: string
  quality: string
  groupName: string
  signalType: string
  anchorType: string
  isCore: boolean
  semanticLabel: string
}

type SignalLeader = MonitoringSignal & {
  changeCount: number
  changed: boolean
  score: number
}

const route = useRoute()
const router = useRouter()

const { t, mergeLocaleMessage } = useI18n()

// 合并页面级多语言包
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const projectId = computed(() => String(route.query.id ?? '').trim())
const initialStationCode = computed(() => String(route.query.stationCode ?? '').trim())

const loading = ref(false)
const projectLoading = ref(false)
const projectCatalogLoading = ref(false)
const projectInfo = ref<GenericRecord>({})
const projectCatalog = ref<GenericRecord[]>([])
const monitoringStations = ref<GenericRecord[]>([])
const monitoringOverview = ref<GenericRecord | null>(null)
const monitoringSignals = ref<MonitoringSignal[]>([])
const monitoringChanges = ref<GenericRecord[]>([])
const monitoringBundles = ref<GenericRecord[]>([])
const monitoringEvents = ref<GenericRecord[]>([])
const monitoringSnapshot = ref<GenericRecord | null>(null)
const monitoringRunResult = ref<GenericRecord | null>(null)
const monitoringHistory = ref<GenericRecord[]>([])
const monitoringSampleLibrary = ref<GenericRecord[]>([])
const monitoringPatternTemplates = ref<GenericRecord[]>([])
const livePulseFeed = ref<LivePulseItem[]>([])
const liveConnectionStatus = ref<'Disconnected' | 'Connecting' | 'Connected' | 'Reconnecting' | 'Error'>('Disconnected')
const selectedMonitoringEventId = ref('')
const acceptanceRunning = ref(false)
const liveReadResult = ref<GenericRecord | null>(null)
const liveReadLoading = ref(false)
const batchSeedLoading = ref(false)
const batchSeedDialogVisible = ref(false)
const batchSeedProgress = ref<GenericRecord | null>(null)
const batchSeedTaskId = ref('')
let batchSeedTimer: number | null = null
const heroExpanded = ref(false)
const workerPaused = ref(false)
const workerEnabled = ref(false)
const workerControlLoading = ref(false)
const detailSectionsVisible = ref(false)

const dashboardForm = ref({
  stationCode: initialStationCode.value,
  stationMode: '',
  runtimeNodeName: '',
  persistHistory: true,
  persistSnapshot: true,
  persistEvents: true,
  rebuildPatternsAfterCycle: false,
  patternSampleLimit: 60,
  signalLimit: 120,
  timelineLimit: 60,
  signalKeyword: '',
  signalView: 'all',
  autoRefreshEnabled: false,
  autoRefreshSeconds: 15,
})

const projectSelector = ref({
  keyword: '',
  page: 1,
  limit: 12,
  total: 0,
})

const recentProjectCatalog = ref<GenericRecord[]>([])

const recentProjectStorageKey = 'plc-monitoring-recent-projects'

let autoRefreshTimer: number | null = null
let realtimeConnection: signalR.HubConnection | null = null
let subscribedProjectId = ''
let subscribedStationCode = ''

const createAcceptanceSteps = (): AcceptanceStep[] => ([
  { key: 'seed-signals', title: t('plc_monitoring.stepSeedSignals'), status: 'idle', detail: t('plc_monitoring.stepSeedSignalsDetail') },
  { key: 'run-cycle', title: t('plc_monitoring.stepRunCycle'), status: 'idle', detail: t('plc_monitoring.stepRunCycleDetail') },
  { key: 'rebuild-patterns', title: t('plc_monitoring.rebuildPatterns'), status: 'idle', detail: t('plc_monitoring.stepRebuildPatternsDetail') },
  { key: 'diagnose-pending', title: t('plc_monitoring.stepDiagnosePending'), status: 'idle', detail: t('plc_monitoring.stepDiagnosePendingDetail') },
  { key: 'verify-result', title: t('plc_monitoring.stepVerifyResult'), status: 'idle', detail: t('plc_monitoring.stepVerifyResultDetail') },
])

const acceptanceFlow = ref({
  lastRunAt: '',
  summary: t('plc_monitoring.acceptanceNotRun'),
  finalStatus: 'idle' as AcceptanceStepStatus,
  steps: createAcceptanceSteps(),
})

const ensureArray = <T = GenericRecord>(value: any): T[] => Array.isArray(value) ? value : []

const normalizeStringArray = (value: any): string[] => ensureArray(value)
  .map(item => String(item ?? '').trim())
  .filter(Boolean)

const parseJsonObject = (value: any): GenericRecord | null => {
  if (!value) {
    return null
  }

  if (typeof value === 'object') {
    return value as GenericRecord
  }

  if (typeof value !== 'string') {
    return null
  }

  try {
    const parsed = JSON.parse(value)
    return parsed && typeof parsed === 'object' ? parsed : null
  }
  catch {
    return null
  }
}

const normalizeMonitoringExpectedNextBundle = (value: any): GenericRecord | null => {
  const item = parseJsonObject(value)
  if (!item) {
    return null
  }

  return {
    bundleSignature: item.bundleSignature ?? item.BundleSignature ?? '',
    count: Number(item.count ?? item.Count ?? 0),
    probability: Number(item.probability ?? item.Probability ?? 0),
    avgGapMs: Number(item.avgGapMs ?? item.AvgGapMs ?? 0),
    minGapMs: Number(item.minGapMs ?? item.MinGapMs ?? 0),
    maxGapMs: Number(item.maxGapMs ?? item.MaxGapMs ?? 0),
  }
}

const normalizeMonitoringMissingBundle = (value: any): GenericRecord | null => {
  const item = parseJsonObject(value)
  if (!item) {
    return null
  }

  return {
    schemaVersion: item.schemaVersion ?? item.SchemaVersion ?? '',
    bundleSignature: item.bundleSignature ?? item.BundleSignature ?? '',
    signalKeys: normalizeStringArray(item.signalKeys ?? item.SignalKeys),
    inferredFromTemplateId: item.inferredFromTemplateId ?? item.InferredFromTemplateId ?? null,
    inferredAt: item.inferredAt ?? item.InferredAt ?? '',
  }
}

const normalizeMonitoringDiagnosisTarget = (value: any): GenericRecord | null => {
  const item = parseJsonObject(value)
  if (!item) {
    return null
  }

  return {
    signalKey: item.signalKey ?? item.SignalKey ?? '',
    anchorKey: item.anchorKey ?? item.AnchorKey ?? '',
    variableName: item.variableName ?? item.VariableName ?? '',
    logicUnitId: item.logicUnitId ?? item.LogicUnitId ?? null,
    resolutionSource: item.resolutionSource ?? item.ResolutionSource ?? '',
  }
}

const normalizeMonitoringDiagnosisTemplate = (value: any): GenericRecord | null => {
  const item = parseJsonObject(value)
  if (!item) {
    return null
  }

  return {
    id: item.id ?? item.Id ?? null,
    templateName: item.templateName ?? item.TemplateName ?? '',
    templateVersion: item.templateVersion ?? item.TemplateVersion ?? null,
    confidence: item.confidence ?? item.Confidence ?? null,
    expectedBundleSignature: item.expectedBundleSignature ?? item.ExpectedBundleSignature ?? item.bundleSignature ?? item.BundleSignature ?? '',
    probability: item.probability ?? item.Probability ?? null,
    count: item.count ?? item.Count ?? null,
  }
}

const normalizeMonitoringDiagnosisAst = (value: any): GenericRecord | null => {
  const item = parseJsonObject(value)
  if (!item) {
    return null
  }

  return {
    logicUnitId: item.logicUnitId ?? item.LogicUnitId ?? null,
    blockId: item.blockId ?? item.BlockId ?? null,
    blockName: item.blockName ?? item.BlockName ?? '',
    variableName: item.variableName ?? item.VariableName ?? '',
    evaluatedValue: item.evaluatedValue ?? item.EvaluatedValue ?? null,
    evaluationStatus: item.evaluationStatus ?? item.EvaluationStatus ?? '',
    missingSymbols: normalizeStringArray(item.missingSymbols ?? item.MissingSymbols),
    messages: normalizeStringArray(item.messages ?? item.Messages),
    failurePath: ensureArray(item.failurePath ?? item.FailurePath),
  }
}

const normalizeMonitoringDiagnosisResult = (value: any): GenericRecord | null => {
  const item = parseJsonObject(value)
  if (!item) {
    return null
  }

  return {
    schemaVersion: item.schemaVersion ?? item.SchemaVersion ?? '',
    status: item.status ?? item.Status ?? '',
    reason: item.reason ?? item.Reason ?? '',
    eventType: item.eventType ?? item.EventType ?? '',
    eventId: item.eventId ?? item.EventId ?? null,
    lastBundleSignature: item.lastBundleSignature ?? item.LastBundleSignature ?? '',
    expectedSignalKeys: normalizeStringArray(item.expectedSignalKeys ?? item.ExpectedSignalKeys),
    fallbackSignalKey: item.fallbackSignalKey ?? item.FallbackSignalKey ?? '',
    availableBindings: item.availableBindings ?? item.AvailableBindings ?? null,
    template: normalizeMonitoringDiagnosisTemplate(item.template ?? item.Template),
    target: normalizeMonitoringDiagnosisTarget(item.target ?? item.Target),
    ast: normalizeMonitoringDiagnosisAst(item.ast ?? item.Ast),
    eventContext: parseJsonObject(item.eventContext ?? item.EventContext),
    diagnosedAt: item.diagnosedAt ?? item.DiagnosedAt ?? '',
    error: item.error ?? item.Error ?? '',
  }
}

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

const normalizeMonitoringSignal = (item: GenericRecord): MonitoringSignal => ({
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
  lastBundleSignature: item.lastBundleSignature ?? item.LastBundleSignature ?? '',
  lastChangedSignal: item.lastChangedSignal ?? item.LastChangedSignal ?? '',
  targetAnchorKey: item.targetAnchorKey ?? item.TargetAnchorKey ?? '',
  diagnosisStatus: item.diagnosisStatus ?? item.DiagnosisStatus ?? '',
  sourceRuntimeNode: item.sourceRuntimeNode ?? item.SourceRuntimeNode ?? '',
  expectedNextBundleJson: item.expectedNextBundleJson ?? item.ExpectedNextBundleJson ?? '',
  missingBundleJson: item.missingBundleJson ?? item.MissingBundleJson ?? '',
  diagnosisResultJson: item.diagnosisResultJson ?? item.DiagnosisResultJson ?? '',
  expectedNextBundle: normalizeMonitoringExpectedNextBundle(item.expectedNextBundle ?? item.ExpectedNextBundle ?? item.expectedNextBundleJson ?? item.ExpectedNextBundleJson),
  missingBundle: normalizeMonitoringMissingBundle(item.missingBundle ?? item.MissingBundle ?? item.missingBundleJson ?? item.MissingBundleJson),
  diagnosisResult: normalizeMonitoringDiagnosisResult(item.diagnosisResult ?? item.DiagnosisResult ?? item.diagnosisResultJson ?? item.DiagnosisResultJson),
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

const normalizeMonitoringHistory = (item: GenericRecord): GenericRecord => ({
  ...item,
  id: item.id ?? item.Id,
  projectId: item.projectId ?? item.ProjectId,
  stationCode: item.stationCode ?? item.StationCode ?? '',
  signalKey: item.signalKey ?? item.SignalKey ?? '',
  variableName: item.variableName ?? item.VariableName ?? '',
  oldValue: item.oldValue ?? item.OldValue ?? '',
  newValue: item.newValue ?? item.NewValue ?? '',
  changedAt: item.changedAt ?? item.ChangedAt ?? '',
  stationMode: item.stationMode ?? item.StationMode ?? '',
  quality: item.quality ?? item.Quality ?? '',
  bundleId: item.bundleId ?? item.BundleId ?? '',
  snapshotId: item.snapshotId ?? item.SnapshotId ?? '',
  eventId: item.eventId ?? item.EventId,
  signalType: item.signalType ?? item.SignalType ?? '',
  groupName: item.groupName ?? item.GroupName ?? '',
})

const normalizeMonitoringSampleLibrary = (item: GenericRecord): GenericRecord => ({
  ...item,
  sampleKey: item.sampleKey ?? item.SampleKey ?? '',
  sampleType: item.sampleType ?? item.SampleType ?? '',
  projectId: item.projectId ?? item.ProjectId,
  stationCode: item.stationCode ?? item.StationCode ?? '',
  bundleId: item.bundleId ?? item.BundleId ?? '',
  snapshotId: item.snapshotId ?? item.SnapshotId ?? '',
  eventId: item.eventId ?? item.EventId,
  stationMode: item.stationMode ?? item.StationMode ?? '',
  startTime: item.startTime ?? item.StartTime ?? '',
  endTime: item.endTime ?? item.EndTime ?? '',
  changeCount: item.changeCount ?? item.ChangeCount ?? 0,
  signalCount: item.signalCount ?? item.SignalCount ?? 0,
  signalKeys: normalizeStringArray(item.signalKeys ?? item.SignalKeys),
  groupNames: normalizeStringArray(item.groupNames ?? item.GroupNames),
  signalTypes: normalizeStringArray(item.signalTypes ?? item.SignalTypes),
  sampleLabel: item.sampleLabel ?? item.SampleLabel ?? '',
  sampleSummary: item.sampleSummary ?? item.SampleSummary ?? '',
  eventType: item.eventType ?? item.EventType ?? '',
  eventLevel: item.eventLevel ?? item.EventLevel ?? '',
  latestTriggeredAt: item.latestTriggeredAt ?? item.LatestTriggeredAt ?? '',
  diagnosisStatus: item.diagnosisStatus ?? item.DiagnosisStatus ?? '',
  lastBundleSignature: item.lastBundleSignature ?? item.LastBundleSignature ?? '',
})

const normalizeMonitoringPatternTemplate = (item: GenericRecord): GenericRecord => ({
  ...item,
  id: item.id ?? item.Id,
  projectId: item.projectId ?? item.ProjectId,
  stationCode: item.stationCode ?? item.StationCode ?? '',
  stationMode: item.stationMode ?? item.StationMode ?? '',
  patternType: item.patternType ?? item.PatternType ?? '',
  templateName: item.templateName ?? item.TemplateName ?? '',
  templateVersion: item.templateVersion ?? item.TemplateVersion ?? 0,
  supportCount: item.supportCount ?? item.SupportCount ?? 0,
  confidence: Number(item.confidence ?? item.Confidence ?? 0),
  lastBundleSignature: item.lastBundleSignature ?? item.LastBundleSignature ?? '',
  bundleSequenceJson: item.bundleSequenceJson ?? item.BundleSequenceJson ?? '',
  nextExpectedMapJson: item.nextExpectedMapJson ?? item.NextExpectedMapJson ?? '',
  timeWindowStatsJson: item.timeWindowStatsJson ?? item.TimeWindowStatsJson ?? '',
  active: item.active ?? item.Active ?? false,
  lastRebuildTime: item.lastRebuildTime ?? item.LastRebuildTime ?? '',
  sourceSampleCount: item.sourceSampleCount ?? item.SourceSampleCount ?? 0,
  remark: item.remark ?? item.Remark ?? '',
})

const normalizeProjectCatalogItem = (item: GenericRecord): GenericRecord => ({
  ...item,
  id: item.id ?? item.Id ?? '',
  projectName: item.projectName ?? item.ProjectName ?? '',
  brand: item.brand ?? item.Brand ?? '',
  product: item.product ?? item.Product ?? '',
  productVersion: item.productVersion ?? item.ProductVersion ?? '',
  stationId: item.stationId ?? item.StationId ?? '',
  createTime: item.createTime ?? item.CreateTime ?? '',
})

const extractApiData = (response: any) => {
  const payload = response?.data ?? response?.backResult ?? response
  if (!payload || typeof payload !== 'object') {
    return payload
  }
  return payload.data ?? payload.Data ?? payload
}

const clearMonitoringDetails = () => {
  monitoringOverview.value = null
  monitoringSignals.value = []
  monitoringChanges.value = []
  monitoringBundles.value = []
  monitoringEvents.value = []
  selectedMonitoringEventId.value = ''
  monitoringSnapshot.value = null
  monitoringHistory.value = []
  monitoringSampleLibrary.value = []
  monitoringPatternTemplates.value = []
}

const resetDashboard = () => {
  projectInfo.value = {}
  monitoringStations.value = []
  monitoringRunResult.value = null
  clearMonitoringDetails()
}

const resolveStationCode = () => String(dashboardForm.value.stationCode || '').trim()

const syncRouteQuery = async (patch: Record<string, any> = {}) => {
  const nextQuery: Record<string, any> = {
    ...route.query,
    id: projectId.value || undefined,
    stationCode: resolveStationCode() || undefined,
    ...patch,
  }

  Object.keys(nextQuery).forEach((key) => {
    if (nextQuery[key] === '' || nextQuery[key] === undefined || nextQuery[key] === null) {
      delete nextQuery[key]
    }
  })

  await router.replace({
    path: '/plc/monitoring',
    query: nextQuery,
  })
}

const formatDateTime = (value: string | null | undefined) => {
  if (!value) {
    return '-'
  }
  const parsed = dayjs(value)
  return parsed.isValid() ? parsed.format('YYYY-MM-DD HH:mm:ss') : String(value)
}

const formatAgo = (value: string | null | undefined) => {
  if (!value) {
    return t('plc_monitoring.noTimestamp')
  }
  const parsed = dayjs(value)
  if (!parsed.isValid()) {
    return String(value)
  }
  const seconds = Math.max(0, dayjs().diff(parsed, 'second'))
  if (seconds < 60) {
    return t('plc_monitoring.secondsAgo', { n: seconds })
  }
  if (seconds < 3600) {
    return t('plc_monitoring.minutesAgo', { n: Math.floor(seconds / 60) })
  }
  if (seconds < 86400) {
    return t('plc_monitoring.hoursAgo', { n: Math.floor(seconds / 3600) })
  }
  return t('plc_monitoring.daysAgo', { n: Math.floor(seconds / 86400) })
}

const formatDuration = (seconds: number | null | undefined) => {
  const total = Number(seconds ?? 0)
  if (!total || total <= 0) {
    return t('plc_monitoring.durationSeconds', { n: 0 })
  }
  if (total < 60) {
    return t('plc_monitoring.durationSeconds', { n: total })
  }
  if (total < 3600) {
    return t('plc_monitoring.durationMinutesSeconds', { m: Math.floor(total / 60), s: total % 60 })
  }
  return t('plc_monitoring.durationHoursMinutes', { h: Math.floor(total / 3600), m: Math.floor((total % 3600) / 60) })
}

const formatValue = (value: any) => {
  if (value === undefined || value === null || value === '') {
    return '-'
  }
  if (typeof value === 'boolean') {
    return value ? 'true' : 'false'
  }
  if (typeof value === 'object') {
    try {
      return JSON.stringify(value)
    }
    catch {
      return String(value)
    }
  }
  return String(value)
}

const formatPercent = (value: any) => {
  const numeric = Number(value)
  if (!Number.isFinite(numeric)) {
    return '-'
  }
  const percent = numeric <= 1 ? numeric * 100 : numeric
  return `${percent.toFixed(percent >= 10 ? 1 : 2)}%`
}

const stringifyPretty = (value: any) => {
  if (value === undefined || value === null || value === '') {
    return '-'
  }

  if (typeof value === 'string') {
    const parsed = parseJsonObject(value)
    if (!parsed) {
      return value
    }
    return JSON.stringify(parsed, null, 2)
  }

  try {
    return JSON.stringify(value, null, 2)
  }
  catch {
    return String(value)
  }
}

const acceptanceStatusTagType = (status: AcceptanceStepStatus) => {
  if (status === 'success') {
    return 'success'
  }
  if (status === 'warning' || status === 'running') {
    return 'warning'
  }
  if (status === 'error') {
    return 'danger'
  }
  return 'info'
}

const diagnosisStatusTagType = (status: string) => {
  const normalized = String(status || '').trim()
  if (normalized === 'Diagnosed') {
    return 'success'
  }
  if (normalized === 'DiagnosedPartial') {
    return 'success'
  }
  if (normalized === 'Pending' || normalized === 'NoTemplate' || normalized === 'NoSignalMapping' || normalized === 'NoAst' || normalized === 'NoExpectedBundle') {
    return 'warning'
  }
  if (normalized === 'Error') {
    return 'danger'
  }
  return 'info'
}

const resolveDiagnosisBucketKey = (event: GenericRecord) => {
  const status = String(event?.diagnosisStatus ?? '').trim()
  if (status) {
    return status
  }

  const reason = String(event?.diagnosisResult?.reason ?? '').trim()
  return reason || 'Unknown'
}

const replaceAcceptanceSteps = () => {
  acceptanceFlow.value.steps = createAcceptanceSteps()
}

const updateAcceptanceStep = (key: string, status: AcceptanceStepStatus, detail: string) => {
  acceptanceFlow.value.steps = acceptanceFlow.value.steps.map(step => step.key === key
    ? { ...step, status, detail }
    : step)
}

const extractActionPayload = (response: any) => {
  const payload = extractApiData(response)
  if (payload?.BackResult) {
    return payload.BackResult
  }
  return payload
}

const clamp = (value: number, min: number, max: number) => Math.min(max, Math.max(min, value))

const pushLivePulse = (item: LivePulseItem) => {
  livePulseFeed.value = [item, ...livePulseFeed.value].slice(0, 18)
}

const getSignalRBaseUrl = () => {
  return import.meta.env.DEV
    ? import.meta.env.VITE_APP_API_BASEURL
    : (window as any).apiConfig?.apiUrl || ''
}

const buildMonitoringBaseQuery = () => ({
  projectId: projectId.value,
  stationCode: resolveStationCode(),
})

const buildMonitoringSignalQuery = () => ({
  ...buildMonitoringBaseQuery(),
  limit: dashboardForm.value.signalLimit,
})

const buildMonitoringTimelineQuery = () => ({
  ...buildMonitoringBaseQuery(),
  limit: dashboardForm.value.timelineLimit,
})

const buildMonitoringHistoryQuery = () => ({
  ...buildMonitoringBaseQuery(),
  limit: Math.max(120, dashboardForm.value.timelineLimit * 4),
})

const buildMonitoringSampleLibraryQuery = () => ({
  ...buildMonitoringBaseQuery(),
  limit: Math.max(20, Math.floor(dashboardForm.value.timelineLimit / 2)),
})

const buildMonitoringPatternTemplateQuery = () => ({
  ...buildMonitoringBaseQuery(),
  stationMode: dashboardForm.value.stationMode || undefined,
  limit: Math.max(12, Math.floor(dashboardForm.value.patternSampleLimit / 2)),
})

const buildPatternTemplateRebuildRequest = () => ({
  ...buildMonitoringBaseQuery(),
  stationMode: dashboardForm.value.stationMode || undefined,
  limit: dashboardForm.value.patternSampleLimit,
  patternType: 'ExpectedNextBundle',
  replaceExisting: true,
  minSupportCount: 1,
})

const buildDiagnosePendingRequest = (eventId?: string | number | null, forceReprocess = false) => ({
  ...buildMonitoringBaseQuery(),
  eventId: eventId ?? undefined,
  forceReprocess,
  limit: 5,
})

const buildRunCycleRequest = (overrides: GenericRecord = {}) => ({
  projectId: projectId.value,
  stationCode: resolveStationCode(),
  stationMode: dashboardForm.value.stationMode || undefined,
  runtimeNodeName: dashboardForm.value.runtimeNodeName || undefined,
  persistHistory: dashboardForm.value.persistHistory,
  persistSnapshot: dashboardForm.value.persistSnapshot,
  persistEvents: dashboardForm.value.persistEvents,
  rebuildPatternsAfterCycle: dashboardForm.value.rebuildPatternsAfterCycle,
  patternSampleLimit: dashboardForm.value.patternSampleLimit,
  jobSource: 'MonitoringPage',
  snapshotRecentBundleCount: dashboardForm.value.timelineLimit,
  ...overrides,
})

const loadRecentProjects = () => {
  try {
    const raw = window.localStorage.getItem(recentProjectStorageKey)
    const payload = raw ? JSON.parse(raw) : []
    recentProjectCatalog.value = ensureArray(payload).map(normalizeProjectCatalogItem).slice(0, 6)
  }
  catch {
    recentProjectCatalog.value = []
  }
}

const saveRecentProject = (item: GenericRecord) => {
  const normalized = normalizeProjectCatalogItem(item)
  const merged = [normalized, ...recentProjectCatalog.value.filter(entry => String(entry.id) !== String(normalized.id))]
    .slice(0, 6)
  recentProjectCatalog.value = merged

  try {
    window.localStorage.setItem(recentProjectStorageKey, JSON.stringify(merged))
  }
  catch {
  }
}

const searchProjectCatalog = async () => {
  projectSelector.value.page = 1
  await loadProjectCatalog()
}

const handleProjectCatalogPageChange = async (page: number) => {
  projectSelector.value.page = page
  await loadProjectCatalog()
}

const handleProjectCatalogSizeChange = async (limit: number) => {
  projectSelector.value.limit = limit
  projectSelector.value.page = 1
  await loadProjectCatalog()
}

const loadProjectCatalog = async () => {
  projectCatalogLoading.value = true
  try {
    const response = await ProjectAPI.list({
      page: projectSelector.value.page,
      limit: projectSelector.value.limit,
      ProjectName: projectSelector.value.keyword || undefined,
    }) as any

    projectCatalog.value = ensureArray(response?.data).map(normalizeProjectCatalogItem)
    projectSelector.value.total = Number(response?.total ?? projectCatalog.value.length ?? 0)
  }
  catch (error: any) {
    ElMessage.error(error.message || t('plc_monitoring.projectListLoadFailed'))
  }
  finally {
    projectCatalogLoading.value = false
  }
}

const loadProjectInfo = async () => {
  if (!projectId.value) {
    projectInfo.value = {}
    return
  }

  projectLoading.value = true
  try {
    projectInfo.value = await ProjectAPI.detail(projectId.value) as any
  }
  catch (error: any) {
    ElMessage.error(error.message || t('plc_monitoring.projectDetailLoadFailed'))
  }
  finally {
    projectLoading.value = false
  }
}

const loadMonitoringStations = async () => {
  if (!projectId.value) {
    monitoringStations.value = []
    return
  }

  const data = extractApiData(await StopCauseDiagnosisAPI.getMonitoringStations({ projectId: projectId.value }))
  monitoringStations.value = ensureArray(data).map(normalizeMonitoringStation)

  const currentStation = resolveStationCode()
  const hasCurrent = currentStation && monitoringStations.value.some(item => item.stationCode === currentStation)
  if (!hasCurrent) {
    dashboardForm.value.stationCode = monitoringStations.value[0]?.stationCode || currentStation
  }
}

const loadMonitoringDetails = async () => {
  const stationCode = resolveStationCode()
  if (!projectId.value || !stationCode) {
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
  if (selectedMonitoringEventId.value && monitoringEvents.value.some(item => String(item.id ?? '') === selectedMonitoringEventId.value)) {
    selectedMonitoringEventId.value = String(selectedMonitoringEventId.value)
  }
  else {
    selectedMonitoringEventId.value = String(monitoringEvents.value[0]?.id ?? '')
  }
  monitoringSnapshot.value = normalizeMonitoringSnapshot(extractApiData(snapshotResponse))
}

const loadPersistedLearningData = async () => {
  const stationCode = resolveStationCode()
  if (!projectId.value || !stationCode) {
    monitoringHistory.value = []
    monitoringSampleLibrary.value = []
    monitoringPatternTemplates.value = []
    return
  }

  const [historyResponse, sampleLibraryResponse, patternTemplateResponse] = await Promise.all([
    StopCauseDiagnosisAPI.getMonitoringHistory(buildMonitoringHistoryQuery()),
    StopCauseDiagnosisAPI.getMonitoringSampleLibrary(buildMonitoringSampleLibraryQuery()),
    StopCauseDiagnosisAPI.getMonitoringPatternTemplates(buildMonitoringPatternTemplateQuery()),
  ])

  monitoringHistory.value = ensureArray(extractApiData(historyResponse)).map(normalizeMonitoringHistory)
  monitoringSampleLibrary.value = ensureArray(extractApiData(sampleLibraryResponse)).map(normalizeMonitoringSampleLibrary)
  monitoringPatternTemplates.value = ensureArray(extractApiData(patternTemplateResponse)).map(normalizeMonitoringPatternTemplate)
}

const loadCurrentStationData = async () => {
  await Promise.all([
    loadMonitoringDetails(),
    loadPersistedLearningData(),
  ])
}

const refreshCurrentStationData = async (syncRoute = true) => {
  if (!projectId.value) {
    clearMonitoringDetails()
    return
  }

  loading.value = true
  try {
    await loadCurrentStationData()
    if (syncRoute) {
      await syncRouteQuery()
    }
  }
  catch (error: any) {
    ElMessage.error(error.message || t('plc_monitoring.stationDataRefreshFailed'))
  }
  finally {
    loading.value = false
  }
}

const refreshStationListAndCurrent = async (syncRoute = true) => {
  if (!projectId.value) {
    resetDashboard()
    return
  }

  loading.value = true
  try {
    await loadMonitoringStations()
    await loadCurrentStationData()
    if (syncRoute) {
      await syncRouteQuery()
    }
  }
  catch (error: any) {
    ElMessage.error(error.message || t('plc_monitoring.stationMonitoringRefreshFailed'))
  }
  finally {
    loading.value = false
  }
}

const refreshDashboard = async (includeProject = false, includeStations = true) => {
  if (!projectId.value) {
    resetDashboard()
    return
  }

  loading.value = true
  try {
    if (includeProject) {
      await loadProjectInfo()
    }
    if (includeStations) {
      await loadMonitoringStations()
    }
    await loadCurrentStationData()
    await syncRouteQuery()
  }
  catch (error: any) {
    ElMessage.error(error.message || t('plc_monitoring.pageRefreshFailed'))
  }
  finally {
    loading.value = false
  }
}

const loadStationsOnly = async () => {
  try {
    await loadMonitoringStations()
  }
  catch {
  }
}

const registerRealtimeHandlers = () => {
  if (!realtimeConnection) {
    return
  }

  realtimeConnection.on('StationShadowCycleCompleted', (payload: GenericRecord) => {
    const stationCode = String(payload?.stationCode ?? payload?.StationCode ?? '').trim()
    const sampleTime = String(payload?.sampleTime ?? payload?.SampleTime ?? '')
    const changedSignalCount = Number(payload?.changedSignalCount ?? payload?.ChangedSignalCount ?? 0)
    pushLivePulse({
      id: `done:${stationCode}:${sampleTime || Date.now()}`,
      title: t('plc_monitoring.runCycleDone', { station: stationCode || t('plc_monitoring.unknownStation') }),
      summary: changedSignalCount > 0
        ? t('plc_monitoring.detectedChanges', { n: changedSignalCount })
        : t('plc_monitoring.noNewChanges'),
      stationCode,
      time: sampleTime,
      tone: changedSignalCount > 0 ? 'success' : 'info',
    })

    if (String(payload?.projectId ?? payload?.ProjectId ?? '') !== projectId.value) {
      return
    }

    monitoringRunResult.value = payload ?? null
    if (stationCode && stationCode === resolveStationCode()) {
      void refreshStationListAndCurrent(false)
    }
    else {
      void loadStationsOnly()
    }
  })

  realtimeConnection.on('StationShadowCycleFailed', (payload: GenericRecord) => {
    const stationCode = String(payload?.stationCode ?? payload?.StationCode ?? '').trim()
    const occurredAt = String(payload?.occurredAt ?? payload?.OccurredAt ?? new Date().toISOString())
    const errorMessage = String(payload?.errorMessage ?? payload?.ErrorMessage ?? t('plc_monitoring.runFailed'))

    pushLivePulse({
      id: `fail:${stationCode}:${occurredAt}`,
      title: t('plc_monitoring.runCycleFailedEvent', { station: stationCode || t('plc_monitoring.unknownStation') }),
      summary: errorMessage,
      stationCode,
      time: occurredAt,
      tone: 'danger',
    })

    if (String(payload?.projectId ?? payload?.ProjectId ?? '') === projectId.value) {
      ElMessage.warning(errorMessage)
      void loadStationsOnly()
    }
  })
}

const syncRealtimeSubscriptions = async () => {
  if (!realtimeConnection || realtimeConnection.state !== signalR.HubConnectionState.Connected || !projectId.value) {
    return
  }

  if (subscribedProjectId && subscribedProjectId !== projectId.value) {
    await realtimeConnection.invoke('UnsubscribeProject', Number(subscribedProjectId))
    subscribedProjectId = ''
  }

  if (!subscribedProjectId) {
    await realtimeConnection.invoke('SubscribeProject', Number(projectId.value))
    subscribedProjectId = projectId.value
  }

  const stationCode = resolveStationCode()
  if (subscribedStationCode && subscribedStationCode !== stationCode) {
    await realtimeConnection.invoke('UnsubscribeStation', Number(projectId.value), subscribedStationCode)
    subscribedStationCode = ''
  }

  if (stationCode && !subscribedStationCode) {
    await realtimeConnection.invoke('SubscribeStation', Number(projectId.value), stationCode)
    subscribedStationCode = stationCode
  }
}

const disconnectRealtime = async () => {
  if (realtimeConnection) {
    await realtimeConnection.stop()
    realtimeConnection = null
  }
  liveConnectionStatus.value = 'Disconnected'
  subscribedProjectId = ''
  subscribedStationCode = ''
}

const connectRealtime = async () => {
  if (!projectId.value) {
    return
  }

  const apiUrl = getSignalRBaseUrl()
  if (!apiUrl) {
    liveConnectionStatus.value = 'Error'
    return
  }

  if (realtimeConnection?.state === signalR.HubConnectionState.Connected) {
    await syncRealtimeSubscriptions()
    return
  }

  if (realtimeConnection) {
    await disconnectRealtime()
  }

  liveConnectionStatus.value = 'Connecting'
  realtimeConnection = new signalR.HubConnectionBuilder()
    .withUrl(`${apiUrl}/hubs/station-shadow-monitoring`, {
      withCredentials: false,
      skipNegotiation: false,
    })
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(signalR.LogLevel.Information)
    .build()

  realtimeConnection.onreconnecting(() => {
    liveConnectionStatus.value = 'Reconnecting'
  })

  realtimeConnection.onreconnected(async () => {
    liveConnectionStatus.value = 'Connected'
    subscribedProjectId = ''
    subscribedStationCode = ''
    await syncRealtimeSubscriptions()
  })

  realtimeConnection.onclose(() => {
    liveConnectionStatus.value = 'Disconnected'
    subscribedProjectId = ''
    subscribedStationCode = ''
  })

  registerRealtimeHandlers()

  try {
    await realtimeConnection.start()
    liveConnectionStatus.value = 'Connected'
    await syncRealtimeSubscriptions()
  }
  catch {
    liveConnectionStatus.value = 'Error'
  }
}

const applyStation = async (stationCode: string) => {
  dashboardForm.value.stationCode = stationCode
  await refreshCurrentStationData()
}

const runMonitoringCycle = async () => {
  if (!projectId.value) {
    ElMessage.warning(t('plc_monitoring.specifyProjectFirst'))
    return
  }
  if (!resolveStationCode()) {
    ElMessage.warning(t('plc_monitoring.selectStationFirst'))
    return
  }

  loading.value = true
  try {
    monitoringRunResult.value = extractApiData(await StopCauseDiagnosisAPI.runMonitoringCycle(buildRunCycleRequest())) || null
    ElMessage.success(t('plc_monitoring.sampleCompleted', { station: resolveStationCode() }))
    await refreshStationListAndCurrent(false)
  }
  catch (error: any) {
    ElMessage.error(error.message || t('plc_monitoring.sampleExecutionFailed'))
  }
  finally {
    loading.value = false
  }
}

const liveReadSignals = async () => {
  if (!projectId.value) {
    ElMessage.warning(t('plc_monitoring.specifyProjectFirst'))
    return
  }
  if (!resolveStationCode()) {
    ElMessage.warning(t('plc_monitoring.selectStationFirst'))
    return
  }

  liveReadLoading.value = true
  try {
    const payload = extractActionPayload(await StopCauseDiagnosisAPI.liveReadMonitoringSignals({
      projectId: projectId.value,
      stationCode: resolveStationCode(),
    })) || null
    liveReadResult.value = payload
    const goodCount = Number(payload?.goodCount ?? payload?.GoodCount ?? 0)
    const totalCount = Number(payload?.totalCount ?? payload?.TotalCount ?? 0)
    ElMessage.success(t('plc_monitoring.liveReadDone', { good: goodCount, total: totalCount }))
  }
  catch (error: any) {
    ElMessage.error(error.message || t('plc_monitoring.liveReadFailed'))
  }
  finally {
    liveReadLoading.value = false
  }
}

const batchSeedAllStations = async () => {
  if (!projectId.value) {
    ElMessage.warning(t('plc_monitoring.specifyProjectFirst'))
    return
  }
  try {
    await ElMessageBox.confirm(
      t('plc_monitoring.batchSeedConfirmText'),
      t('plc_monitoring.rebuildAllSignals'),
      { confirmButtonText: t('plc_monitoring.startRebuild'), cancelButtonText: t('common.cancel'), type: 'warning' },
    )
  }
  catch {
    return
  }

  batchSeedLoading.value = true
  batchSeedProgress.value = null
  batchSeedDialogVisible.value = true

  try {
    const response = await StopCauseDiagnosisAPI.batchSeedMonitoringSignals({
      projectId: projectId.value,
      replaceExisting: true,
    }) as any

    const payload = response?.backResult ?? response?.BackResult ?? response
    const taskId = payload?.taskId ?? payload?.TaskId
    if (!taskId) {
      ElMessage.error(payload?.errorMessage ?? payload?.ErrorMessage ?? t('plc_monitoring.startBatchSeedFailed'))
      batchSeedLoading.value = false
      return
    }

    batchSeedTaskId.value = taskId
    batchSeedProgress.value = {
      taskId,
      status: 'Running',
      currentStep: 0,
      totalSteps: 2,
      stepName: t('plc_monitoring.initializing'),
      message: t('plc_monitoring.taskSubmitted'),
      percentage: 0,
    }
    startBatchSeedPolling()
  }
  catch (error: any) {
    ElMessage.error(error.message || t('plc_monitoring.startBatchSeedFailed'))
    batchSeedLoading.value = false
  }
}

const startBatchSeedPolling = () => {
  stopBatchSeedPolling()
  void pollBatchSeedOnce()
  batchSeedTimer = window.setInterval(() => {
    void pollBatchSeedOnce()
  }, 1500)
}

const stopBatchSeedPolling = () => {
  if (batchSeedTimer !== null) {
    window.clearInterval(batchSeedTimer)
    batchSeedTimer = null
  }
}

const pollBatchSeedOnce = async () => {
  if (!batchSeedTaskId.value) return
  try {
    const response = await StopCauseDiagnosisAPI.getFullAnalysisProgress({
      taskId: batchSeedTaskId.value,
    }) as any
    batchSeedProgress.value = response ?? null

    const status = String(response?.status ?? response?.Status ?? '').toLowerCase()
    if (status === 'completed') {
      stopBatchSeedPolling()
      batchSeedLoading.value = false
      const summary = response?.summary ?? response?.Summary
      const stationCount = summary?.stationCount ?? summary?.StationCount ?? '?'
      const signalCount = summary?.monitoringSignalCount ?? summary?.MonitoringSignalCount ?? '?'
      ElMessage.success(t('plc_monitoring.batchSeedCompleted', { stations: stationCount, signals: signalCount }))
      await refreshDashboard(false, true)
    }
    else if (status === 'failed') {
      stopBatchSeedPolling()
      batchSeedLoading.value = false
      ElMessage.error(response?.errorMessage ?? response?.ErrorMessage ?? t('plc_monitoring.batchSeedFailed'))
    }
  }
  catch {
    // 轮询中网络错误不中断
  }
}

const loadWorkerStatus = async () => {
  try {
    const res = await StopCauseDiagnosisAPI.getWorkerStatus() as any
    const d = res?.data ?? res
    workerEnabled.value = d?.Enabled ?? d?.enabled ?? false
    workerPaused.value = d?.Paused ?? d?.paused ?? false
  }
  catch {}
}

const toggleWorkerPause = async () => {
  workerControlLoading.value = true
  try {
    if (workerPaused.value) {
      await StopCauseDiagnosisAPI.resumeWorker()
      workerPaused.value = false
      ElMessage.success(t('plc_monitoring.workerResumed'))
    }
    else {
      await StopCauseDiagnosisAPI.pauseWorker()
      workerPaused.value = true
      ElMessage.success(t('plc_monitoring.workerPausedMsg'))
    }
  }
  catch (e: any) {
    ElMessage.error(t('plc_monitoring.operationFailedWithReason', { reason: e?.message ?? String(e) }))
  }
  finally {
    workerControlLoading.value = false
  }
}

const rebuildMonitoringPatternTemplates = async () => {
  if (!projectId.value) {
    ElMessage.warning(t('plc_monitoring.specifyProjectFirst'))
    return
  }
  if (!resolveStationCode()) {
    ElMessage.warning(t('plc_monitoring.selectStationFirst'))
    return
  }

  loading.value = true
  try {
    const response = await StopCauseDiagnosisAPI.rebuildMonitoringPatternTemplates(buildPatternTemplateRebuildRequest()) as any
    const payload = response?.data?.BackResult ?? response?.BackResult ?? response?.data ?? null
    const generatedCount = Number(payload?.generatedTemplateCount ?? payload?.GeneratedTemplateCount ?? 0)
    const warningMessage = ensureArray(payload?.warnings ?? payload?.Warnings)
      .map(item => String(item ?? '').trim())
      .filter(Boolean)
      .join('；')

    await loadPersistedLearningData()
    if (generatedCount > 0) {
      ElMessage.success(t('plc_monitoring.rebuildCompleted', { n: generatedCount }))
    }
    else if (warningMessage) {
      ElMessage.warning(warningMessage)
    }
    else {
      ElMessage.warning(t('plc_monitoring.noNewTemplates'))
    }
  }
  catch (error: any) {
    ElMessage.error(error.message || t('plc_monitoring.rebuildFailed'))
  }
  finally {
    loading.value = false
  }
}

const runMonitoringAcceptance = async () => {
  if (!projectId.value) {
    ElMessage.warning(t('plc_monitoring.specifyProjectFirst'))
    return
  }
  if (!resolveStationCode()) {
    ElMessage.warning(t('plc_monitoring.selectStationFirst'))
    return
  }

  acceptanceRunning.value = true
  acceptanceFlow.value.lastRunAt = dayjs().format('YYYY-MM-DD HH:mm:ss')
  acceptanceFlow.value.summary = t('plc_monitoring.acceptanceRunningSummary')
  acceptanceFlow.value.finalStatus = 'running'
  replaceAcceptanceSteps()

  try {
    // ── Step 0: seed-signals (检查/播种监控信号) ──
    try {
      updateAcceptanceStep('seed-signals', 'running', t('plc_monitoring.seedingRunning'))
      const seedPayload = extractActionPayload(await StopCauseDiagnosisAPI.seedMonitoringSignals({
        projectId: projectId.value,
        stationCode: resolveStationCode(),
        replaceExisting: true,
      })) || null
      const insertedCount = Number(seedPayload?.insertedCount ?? seedPayload?.InsertedCount ?? 0)
      const existingCount = Number(seedPayload?.existingSignalCount ?? seedPayload?.ExistingSignalCount ?? 0)
      const anchorCount = Number(seedPayload?.anchorBindingCount ?? seedPayload?.AnchorBindingCount ?? 0)
      const configCreated = seedPayload?.configCreated ?? seedPayload?.ConfigCreated ?? false
      const seedWarnings = ensureArray(seedPayload?.warnings ?? seedPayload?.Warnings).map(String).filter(Boolean)
      const configNote = configCreated ? t('plc_monitoring.configCreatedNote') : ''
      updateAcceptanceStep(
        'seed-signals',
        insertedCount > 0 || existingCount > 0 ? 'success' : 'warning',
        insertedCount > 0
          ? t('plc_monitoring.seedRebuilt', { anchors: anchorCount, inserted: insertedCount, existing: existingCount, note: configNote })
          : existingCount > 0
            ? t('plc_monitoring.seedClearedOnly', { existing: existingCount, note: configNote })
            : (seedWarnings[0] || t('plc_monitoring.seedFailedHint')),
      )
    }
    catch (err: any) {
      updateAcceptanceStep('seed-signals', 'error', err.message || t('plc_monitoring.seedFailed'))
    }

    // ── Step 1: run-cycle ──
    let runCycleEventId = ''
    let changedSignalCount = 0
    try {
      updateAcceptanceStep('run-cycle', 'running', t('plc_monitoring.runCycleStepRunning'))
      const runCyclePayload = extractActionPayload(await StopCauseDiagnosisAPI.runMonitoringCycle(buildRunCycleRequest({
        rebuildPatternsAfterCycle: false,
        diagnosePendingAfterCycle: false,
        jobSource: 'MonitoringAcceptance',
      }))) || null
      monitoringRunResult.value = runCyclePayload
      const runStatus = String(runCyclePayload?.status ?? runCyclePayload?.Status ?? '').trim()
      runCycleEventId = String(runCyclePayload?.eventId ?? runCyclePayload?.EventId ?? '').trim()
      changedSignalCount = Number(runCyclePayload?.changedSignalCount ?? runCyclePayload?.ChangedSignalCount ?? 0)
      const signalCount = Number(runCyclePayload?.signalCount ?? runCyclePayload?.SignalCount ?? 0)
      const runWarnings = (runCyclePayload?.warnings ?? runCyclePayload?.Warnings ?? []) as string[]

      if (runStatus === 'ConfigMissing' || runStatus === 'Disabled' || runStatus === 'NoSignals') {
        const statusMsg = runStatus === 'ConfigMissing' ? t('plc_monitoring.configMissing')
          : runStatus === 'Disabled' ? t('plc_monitoring.configDisabledMsg')
            : t('plc_monitoring.noSignalsMsg')
        updateAcceptanceStep('run-cycle', 'warning', statusMsg + t('plc_monitoring.statusSuffix', { status: runStatus }))
      }
      else {
        updateAcceptanceStep(
          'run-cycle',
          'success',
          t('plc_monitoring.sampleDoneSummary', { signals: signalCount, changes: changedSignalCount })
            + (runCycleEventId ? t('plc_monitoring.eventSegment', { id: runCycleEventId }) : t('plc_monitoring.noEventSegment'))
            + (runWarnings.length ? `；${runWarnings[0]}` : ''),
        )
      }
    }
    catch (err: any) {
      updateAcceptanceStep('run-cycle', 'error', err.message || t('plc_monitoring.runCycleFailedStep'))
    }

    // ── Step 2: rebuild-patterns ──
    let generatedTemplateCount = 0
    try {
      updateAcceptanceStep('rebuild-patterns', 'running', t('plc_monitoring.rebuildStepRunning'))
      const rebuildPayload = extractActionPayload(await StopCauseDiagnosisAPI.rebuildMonitoringPatternTemplates(buildPatternTemplateRebuildRequest())) || null
      generatedTemplateCount = Number(rebuildPayload?.generatedTemplateCount ?? rebuildPayload?.GeneratedTemplateCount ?? 0)
      const rebuildWarnings = ensureArray(rebuildPayload?.warnings ?? rebuildPayload?.Warnings)
        .map(item => String(item ?? '').trim())
        .filter(Boolean)
      updateAcceptanceStep(
        'rebuild-patterns',
        generatedTemplateCount > 0 ? 'success' : 'warning',
        generatedTemplateCount > 0
          ? t('plc_monitoring.rebuildCompletedSentence', { n: generatedTemplateCount })
          : (rebuildWarnings[0] || t('plc_monitoring.rebuildNoNew')),
      )
    }
    catch (err: any) {
      updateAcceptanceStep('rebuild-patterns', 'error', err.message || t('plc_monitoring.rebuildStepFailed'))
    }

    // ── Step 3: diagnose-pending ──
    let firstDiagnosedEventId = ''
    try {
      updateAcceptanceStep('diagnose-pending', 'running', t('plc_monitoring.diagnoseStepRunning'))
      const diagnosePayload = extractActionPayload(await StopCauseDiagnosisAPI.diagnosePendingMonitoringEvents(buildDiagnosePendingRequest(runCycleEventId || undefined, false))) || null
      const processedCount = Number(diagnosePayload?.processedCount ?? diagnosePayload?.ProcessedCount ?? 0)
      const successCount = Number(diagnosePayload?.successCount ?? diagnosePayload?.SuccessCount ?? 0)
      const diagnoseWarnings = ensureArray(diagnosePayload?.warnings ?? diagnosePayload?.Warnings)
        .map(item => String(item ?? '').trim())
        .filter(Boolean)
      const diagnoseItems = ensureArray(diagnosePayload?.items ?? diagnosePayload?.Items)
      firstDiagnosedEventId = String(diagnoseItems[0]?.eventId ?? diagnoseItems[0]?.EventId ?? '').trim()
      updateAcceptanceStep(
        'diagnose-pending',
        processedCount > 0 && successCount > 0 ? 'success' : 'warning',
        processedCount > 0
          ? t('plc_monitoring.diagnoseDone', { processed: processedCount, successCount }) + (diagnoseWarnings[0] ? ` ${diagnoseWarnings[0]}` : '')
          : (diagnoseWarnings[0] || t('plc_monitoring.diagnoseNone')),
      )
    }
    catch (err: any) {
      updateAcceptanceStep('diagnose-pending', 'error', err.message || t('plc_monitoring.diagnoseStepFailed'))
    }

    // ── Step 4: verify-result ──
    updateAcceptanceStep('verify-result', 'running', t('plc_monitoring.verifyStepRunning'))
    const targetEventId = runCycleEventId || firstDiagnosedEventId
    if (targetEventId) {
      selectedMonitoringEventId.value = targetEventId
    }
    await refreshStationListAndCurrent(false)
    if (targetEventId) {
      selectMonitoringEvent(targetEventId)
    }

    const verifiedEvent = targetEventId
      ? monitoringEvents.value.find(item => String(item.id ?? '') === targetEventId) ?? monitoringLatestEvent.value
      : monitoringLatestEvent.value
    const verifiedStatus = String(verifiedEvent?.diagnosisStatus ?? '').trim()
    const hasStructuredDiagnosis = !!verifiedEvent?.diagnosisResult
    const verificationStatus: AcceptanceStepStatus = hasStructuredDiagnosis
      ? 'success'
      : verifiedStatus && verifiedStatus !== 'Pending'
        ? 'warning'
        : 'warning'
    updateAcceptanceStep(
      'verify-result',
      verificationStatus,
      verifiedEvent
        ? `${t('plc_monitoring.verifyEventStatus', { status: verifiedStatus || t('plc_monitoring.unmarked') })}，${hasStructuredDiagnosis ? t('plc_monitoring.diagnosisWritten') : t('plc_monitoring.diagnosisNotWritten')}`
        : t('plc_monitoring.verifyNoEvent'),
    )

    acceptanceFlow.value.finalStatus = acceptanceFlow.value.steps.some(step => step.status === 'error')
      ? 'error'
      : acceptanceFlow.value.steps.some(step => step.status === 'warning')
        ? 'warning'
        : 'success'
    acceptanceFlow.value.summary = verifiedEvent
      ? t('plc_monitoring.acceptanceDoneWithEvent', { event: verifiedEvent.id || '-', status: verifiedStatus || t('plc_monitoring.unmarked') })
      : t('plc_monitoring.acceptanceDoneNoEvent')

    ElMessage.success(t('plc_monitoring.acceptanceCompleted'))
  }
  catch (error: any) {
    acceptanceFlow.value.finalStatus = 'error'
    acceptanceFlow.value.summary = error.message || t('plc_monitoring.acceptanceFailed')

    const runningStep = acceptanceFlow.value.steps.find(step => step.status === 'running')
    if (runningStep) {
      updateAcceptanceStep(runningStep.key, 'error', error.message || t('plc_monitoring.executionFailed'))
    }

    ElMessage.error(error.message || t('plc_monitoring.acceptanceFailed'))
  }
  finally {
    acceptanceRunning.value = false
  }
}

const stopAutoRefresh = () => {
  if (autoRefreshTimer !== null) {
    window.clearInterval(autoRefreshTimer)
    autoRefreshTimer = null
  }
}

const startAutoRefresh = () => {
  stopAutoRefresh()
  if (!dashboardForm.value.autoRefreshEnabled || !projectId.value) {
    return
  }

  autoRefreshTimer = window.setInterval(() => {
    if (!loading.value) {
      void refreshCurrentStationData(false)
    }
  }, Math.max(5, Number(dashboardForm.value.autoRefreshSeconds || 15)) * 1000)
}

const goProjectDetail = () => {
  if (!projectId.value) {
    router.back()
    return
  }

  router.push({
    path: '/plc/detail',
    query: {
      id: projectId.value,
    },
  })
}

const openProjectMonitoring = async (item: GenericRecord) => {
  const nextId = String(item.id ?? '').trim()
  if (!nextId) {
    ElMessage.warning(t('plc_monitoring.projectMissingId'))
    return
  }

  saveRecentProject(item)

  await router.replace({
    path: '/plc/monitoring',
    query: {
      id: nextId,
    },
  })
}

const selectedStation = computed(() => {
  const stationCode = resolveStationCode()
  return monitoringStations.value.find(item => item.stationCode === stationCode) || null
})

const monitoringOverviewState = computed(() => monitoringOverview.value?.state ?? monitoringOverview.value?.State ?? null)
const monitoringRuntimeLease = computed(() => monitoringOverview.value?.runtimeLease ?? monitoringOverview.value?.RuntimeLease ?? null)
const selectedMonitoringEvent = computed(() => {
  const selectedId = selectedMonitoringEventId.value
  if (selectedId) {
    const matched = monitoringEvents.value.find(item => String(item.id ?? '') === selectedId)
    if (matched) {
      return matched
    }
  }

  const event = monitoringOverview.value?.latestEvent ?? monitoringOverview.value?.LatestEvent ?? monitoringEvents.value[0] ?? null
  return event ? normalizeMonitoringEvent(event) : null
})
const monitoringLatestEvent = computed(() => selectedMonitoringEvent.value)
const monitoringLatestDiagnosis = computed(() => selectedMonitoringEvent.value?.diagnosisResult ?? null)
const monitoringLatestDiagnosisTemplate = computed(() => monitoringLatestDiagnosis.value?.template ?? null)
const monitoringLatestDiagnosisTarget = computed(() => monitoringLatestDiagnosis.value?.target ?? null)
const monitoringLatestDiagnosisAst = computed(() => monitoringLatestDiagnosis.value?.ast ?? null)
const monitoringLatestMissingBundle = computed(() => selectedMonitoringEvent.value?.missingBundle ?? null)
const monitoringLatestExpectedBundle = computed(() => selectedMonitoringEvent.value?.expectedNextBundle ?? null)
const monitoringLatestMissingSignalKeys = computed(() => {
  const diagnosisKeys = normalizeStringArray(monitoringLatestDiagnosis.value?.expectedSignalKeys)
  if (diagnosisKeys.length > 0) {
    return diagnosisKeys
  }
  return normalizeStringArray(monitoringLatestMissingBundle.value?.signalKeys)
})
const monitoringLatestDiagnosisText = computed(() => {
  if (monitoringLatestDiagnosis.value) {
    return stringifyPretty(monitoringLatestDiagnosis.value)
  }
  return stringifyPretty(selectedMonitoringEvent.value?.diagnosisResultJson)
})
const monitoringEventHighlights = computed(() => monitoringEvents.value.slice(0, 8))

const selectMonitoringEvent = (eventId: string | number | null | undefined) => {
  const normalizedId = String(eventId ?? '').trim()
  if (!normalizedId) {
    return
  }
  selectedMonitoringEventId.value = normalizedId
}
const liveConnectionTagType = computed(() => {
  if (liveConnectionStatus.value === 'Connected') {
    return 'success'
  }
  if (liveConnectionStatus.value === 'Reconnecting' || liveConnectionStatus.value === 'Connecting') {
    return 'warning'
  }
  if (liveConnectionStatus.value === 'Error') {
    return 'danger'
  }
  return 'info'
})

const normalizedRunResult = computed(() => {
  if (!monitoringRunResult.value) {
    return null
  }

  return {
    stationCode: monitoringRunResult.value.stationCode ?? monitoringRunResult.value.StationCode ?? resolveStationCode(),
    status: monitoringRunResult.value.status ?? monitoringRunResult.value.Status ?? '-',
    sampleTime: monitoringRunResult.value.sampleTime ?? monitoringRunResult.value.SampleTime ?? '-',
    changedSignalCount: monitoringRunResult.value.changedSignalCount ?? monitoringRunResult.value.ChangedSignalCount ?? 0,
    bundleId: monitoringRunResult.value.bundleId ?? monitoringRunResult.value.BundleId ?? '-',
    snapshotId: monitoringRunResult.value.snapshotId ?? monitoringRunResult.value.SnapshotId ?? '-',
    isSilent: monitoringRunResult.value.isSilent ?? monitoringRunResult.value.IsSilent ?? false,
    missingSymbolCount: monitoringRunResult.value.missingSymbolCount ?? monitoringRunResult.value.MissingSymbolCount ?? 0,
    warnings: ensureArray(monitoringRunResult.value.warnings ?? monitoringRunResult.value.Warnings)
      .map(item => String(item ?? '').trim())
      .filter(Boolean),
  }
})

const lastSampleAgeSeconds = computed(() => {
  const sampleTime = monitoringOverviewState.value?.lastSampleTime ?? monitoringOverviewState.value?.LastSampleTime ?? selectedStation.value?.lastSampleTime
  if (!sampleTime) {
    return Number.POSITIVE_INFINITY
  }
  const parsed = dayjs(sampleTime)
  if (!parsed.isValid()) {
    return Number.POSITIVE_INFINITY
  }
  return Math.max(0, dayjs().diff(parsed, 'second'))
})

const healthScore = computed(() => {
  let score = 100

  if (!selectedStation.value?.enabled) {
    score -= 12
  }
  if (selectedStation.value?.isSilent || monitoringOverviewState.value?.isSilent || monitoringOverviewState.value?.IsSilent) {
    score -= 34
  }

  const runtimeStatus = String(monitoringRuntimeLease.value?.status ?? monitoringRuntimeLease.value?.Status ?? selectedStation.value?.runtimeStatus ?? '').toLowerCase()
  if (!runtimeStatus || (runtimeStatus !== 'running' && runtimeStatus !== 'active' && runtimeStatus !== 'healthy')) {
    score -= 18
  }

  if (lastSampleAgeSeconds.value > 300) {
    score -= 20
  }
  else if (lastSampleAgeSeconds.value > 120) {
    score -= 10
  }

  score -= Math.min(12, Math.max(0, Number(selectedStation.value?.silentSeconds ?? 0) / 60))
  return clamp(Math.round(score), 8, 100)
})

const healthSummary = computed(() => {
  if (healthScore.value >= 85) {
    return {
      label: t('plc_monitoring.healthStable'),
      tone: 'excellent',
      description: t('plc_monitoring.healthStableDesc'),
    }
  }
  if (healthScore.value >= 60) {
    return {
      label: t('plc_monitoring.healthWatch'),
      tone: 'watch',
      description: t('plc_monitoring.healthWatchDesc'),
    }
  }
  return {
    label: t('plc_monitoring.healthRisk'),
    tone: 'risk',
    description: t('plc_monitoring.healthRiskDesc'),
  }
})

const recentBundleSource = computed(() => {
  const snapshotBundles = ensureArray(monitoringSnapshot.value?.recentBundles)
  return snapshotBundles.length > 0 ? snapshotBundles : monitoringBundles.value
})

const filteredSignals = computed(() => {
  const keyword = dashboardForm.value.signalKeyword.trim().toLowerCase()
  return monitoringSignals.value.filter((item) => {
    const changed = formatValue(item.currentValue) !== formatValue(item.previousValue)
    if (dashboardForm.value.signalView === 'changed' && !changed) {
      return false
    }
    if (dashboardForm.value.signalView === 'core' && !item.isCore) {
      return false
    }
    if (!keyword) {
      return true
    }
    return [item.signalKey, item.variableName, item.groupName, item.semanticLabel, item.signalType]
      .some(value => String(value ?? '').toLowerCase().includes(keyword))
  })
})

const signalLeaders = computed<SignalLeader[]>(() => {
  const counts = new Map<string, number>()
  monitoringChanges.value.forEach((item) => {
    counts.set(item.signalKey, (counts.get(item.signalKey) ?? 0) + 1)
  })

  return monitoringSignals.value
    .map((signal) => {
      const changeCount = counts.get(signal.signalKey) ?? 0
      const changed = formatValue(signal.currentValue) !== formatValue(signal.previousValue)
      const lastChangeMinutes = signal.lastChangeTime && dayjs(signal.lastChangeTime).isValid()
        ? Math.max(0, dayjs().diff(dayjs(signal.lastChangeTime), 'minute'))
        : 999
      const recencyBonus = clamp(100 - lastChangeMinutes * 4, 0, 100)
      const score = clamp(changeCount * 24 + recencyBonus + (changed ? 18 : 0) + (signal.isCore ? 12 : 0), 0, 100)
      return {
        ...signal,
        changeCount,
        changed,
        score,
      }
    })
    .sort((left, right) => right.score - left.score)
    .slice(0, 8)
})

const groupHotspots = computed(() => {
  const groups = new Map<string, { count: number, changedSignals: Set<string>, lastChangedAt: string }>()
  monitoringChanges.value.forEach((item) => {
    const key = item.groupName || item.signalType || t('plc_monitoring.ungrouped')
    const current = groups.get(key) ?? { count: 0, changedSignals: new Set<string>(), lastChangedAt: '' }
    current.count += 1
    current.changedSignals.add(item.signalKey)
    if (!current.lastChangedAt || dayjs(item.changedAt).isAfter(dayjs(current.lastChangedAt))) {
      current.lastChangedAt = item.changedAt
    }
    groups.set(key, current)
  })

  return [...groups.entries()]
    .map(([name, value]) => ({
      name,
      count: value.count,
      signalCount: value.changedSignals.size,
      lastChangedAt: value.lastChangedAt,
    }))
    .sort((left, right) => right.count - left.count)
    .slice(0, 6)
})

const storyItems = computed<StoryItem[]>(() => {
  const items: StoryItem[] = []

  monitoringEvents.value.forEach((item) => {
    const diagnosis = item.diagnosisResult ?? null
    const target = diagnosis?.target ?? null
    items.push({
      id: `event:${item.id ?? item.triggeredAt}`,
      eventId: String(item.id ?? ''),
      kind: 'event',
      kindLabel: t('plc_monitoring.kindEvent'),
      title: `${item.eventType || t('plc_monitoring.unknownEvent')} · ${item.eventLevel || t('plc_monitoring.unleveled')}`,
      summary: [
        item.diagnosisStatus,
        diagnosis?.reason,
        target?.anchorKey || item.targetAnchorKey,
        item.lastChangedSignal,
        item.sourceRuntimeNode,
      ].filter(Boolean).join(' / ') || t('plc_monitoring.eventNoContext'),
      time: item.triggeredAt,
      timeText: formatDateTime(item.triggeredAt),
      agoText: formatAgo(item.triggeredAt),
      intensity: clamp(88 - items.length * 4, 24, 92),
      tags: [
        item.stationMode || t('plc_monitoring.modeUnknown'),
        item.lastBundleId || t('plc_monitoring.noBundle'),
        diagnosis?.target?.resolutionSource,
      ].filter(Boolean),
    })
  })

  recentBundleSource.value.forEach((item) => {
    const time = item.endTime || item.startTime
    items.push({
      id: `bundle:${item.bundleId}`,
      kind: 'bundle',
      kindLabel: t('plc_monitoring.kindBundle'),
      title: item.bundleLabel || item.bundleSignature || item.bundleId || t('plc_monitoring.unnamedBundle'),
      summary: t('plc_monitoring.bundleSummary', { signals: item.signalKeys.length, mode: item.stationMode || t('plc_monitoring.unmarked'), count: item.concurrencyCount || item.signalKeys.length || 0 }),
      time,
      timeText: formatDateTime(time),
      agoText: formatAgo(time),
      intensity: clamp(72 + item.signalKeys.length * 3, 30, 100),
      tags: item.signalKeys.slice(0, 4),
    })
  })

  monitoringChanges.value.forEach((item) => {
    items.push({
      id: `change:${item.changeId || `${item.signalKey}:${item.changedAt}`}`,
      kind: 'change',
      kindLabel: t('plc_monitoring.kindChange'),
      title: item.signalKey || t('plc_monitoring.unknownSignal'),
      summary: `${formatValue(item.oldValue)} -> ${formatValue(item.newValue)}${item.groupName ? t('plc_monitoring.groupSegment', { name: item.groupName }) : ''}`,
      time: item.changedAt,
      timeText: formatDateTime(item.changedAt),
      agoText: formatAgo(item.changedAt),
      intensity: clamp(68, 20, 100),
      tags: [item.signalType || t('plc_monitoring.unclassified'), item.quality || t('plc_monitoring.noQualityMark')].filter(Boolean),
    })
  })

  return items
    .sort((left, right) => dayjs(right.time).valueOf() - dayjs(left.time).valueOf())
    .slice(0, Math.max(12, dashboardForm.value.timelineLimit))
    .map((item, index) => ({
      ...item,
      intensity: clamp(item.intensity - index * 2, 16, 100),
    }))
})

const learningSamples = computed<LearningSample[]>(() => {
  const samples: LearningSample[] = []

  recentBundleSource.value.slice(0, 4).forEach((bundle, index) => {
    samples.push({
      id: `bundle-sample:${bundle.bundleId || index}`,
      title: bundle.bundleLabel || bundle.bundleSignature || t('plc_monitoring.actionSampleN', { n: index + 1 }),
      subtitle: `${formatDateTime(bundle.endTime || bundle.startTime)} · ${t('plc_monitoring.qPointCount', { n: bundle.signalKeys.length })}`,
      insight: bundle.signalKeys.length >= 3
        ? t('plc_monitoring.insightMultiSignal')
        : t('plc_monitoring.insightLightSample'),
      cues: bundle.signalKeys.slice(0, 6),
      tone: index === 0 ? 'accent' : 'info',
    })
  })

  groupHotspots.value.slice(0, 2).forEach((group, index) => {
    samples.push({
      id: `group-sample:${group.name}`,
      title: t('plc_monitoring.hotspotGroup', { name: group.name }),
      subtitle: t('plc_monitoring.hotspotSummary', { count: group.count, signals: group.signalCount }),
      insight: t('plc_monitoring.insightHotspot'),
      cues: [formatDateTime(group.lastChangedAt), t('plc_monitoring.signalCount', { n: group.signalCount })],
      tone: index === 0 ? 'warning' : 'info',
    })
  })

  signalLeaders.value.slice(0, 2).forEach((signal, index) => {
    samples.push({
      id: `signal-sample:${signal.signalKey}`,
      title: signal.signalKey,
      subtitle: t('plc_monitoring.leaderSubtitle', { count: signal.changeCount, type: signal.isCore ? t('plc_monitoring.coreSignal') : t('plc_monitoring.contextSignal') }),
      insight: signal.changed
        ? t('plc_monitoring.insightSignalEdge')
        : t('plc_monitoring.insightSignalRecurring'),
      cues: [signal.groupName || t('plc_monitoring.ungrouped'), signal.semanticLabel || signal.signalType || t('plc_monitoring.unmarked')],
      tone: index === 0 ? 'accent' : 'info',
    })
  })

  return samples.slice(0, 6)
})

const learnedInsights = computed(() => {
  const insights: string[] = []
  const repeatedBundles = new Map<string, number>()

  recentBundleSource.value.forEach((item) => {
    const key = item.bundleLabel || item.bundleSignature || ''
    if (key) {
      repeatedBundles.set(key, (repeatedBundles.get(key) ?? 0) + 1)
    }
  })

  const topRepeatedBundle = [...repeatedBundles.entries()].sort((left, right) => right[1] - left[1])[0]
  if (topRepeatedBundle && topRepeatedBundle[1] >= 2) {
    insights.push(t('plc_monitoring.insightRepeatedBundle', { name: topRepeatedBundle[0], count: topRepeatedBundle[1] }))
  }

  if (groupHotspots.value[0]) {
    insights.push(t('plc_monitoring.insightTopGroup', { name: groupHotspots.value[0].name, time: formatDateTime(groupHotspots.value[0].lastChangedAt) }))
  }

  if (signalLeaders.value[0]) {
    insights.push(t('plc_monitoring.insightTopSignal', { name: signalLeaders.value[0].signalKey, score: signalLeaders.value[0].score }))
  }

  if (selectedStation.value?.isSilent || monitoringOverviewState.value?.isSilent || monitoringOverviewState.value?.IsSilent) {
    insights.push(t('plc_monitoring.insightSilent'))
  }

  if (insights.length === 0) {
    insights.push(t('plc_monitoring.insightFewSamples'))
  }

  return insights
})

const persistedSampleHighlights = computed(() => monitoringSampleLibrary.value.slice(0, 6))

const patternTemplateHighlights = computed(() => monitoringPatternTemplates.value.slice(0, 6))

const livePulsePreview = computed(() => livePulseFeed.value.slice(0, 8))

const heroMetrics = computed(() => ([
  {
    label: t('plc_monitoring.metricHealthScore'),
    value: healthScore.value,
    helper: healthSummary.value.label,
  },
  {
    label: t('plc_monitoring.changedSignals'),
    value: new Set(monitoringChanges.value.map(item => item.signalKey)).size,
    helper: t('plc_monitoring.changeRecords', { n: monitoringChanges.value.length }),
  },
  {
    label: t('plc_monitoring.metricActionSamples'),
    value: learningSamples.value.length,
    helper: t('plc_monitoring.metricActionSamplesHelper'),
  },
  {
    label: t('plc_monitoring.metricPatternTemplates'),
    value: monitoringPatternTemplates.value.length,
    helper: monitoringPatternTemplates.value.length > 0
      ? t('plc_monitoring.templateConfidenceHelper', { confidence: Math.round((Number(monitoringPatternTemplates.value[0]?.confidence || 0) * 100)), support: monitoringPatternTemplates.value[0]?.supportCount || 0 })
      : t('plc_monitoring.noTemplatesYet'),
  },
  {
    label: t('plc_monitoring.metricDiagnosedEvents'),
    value: monitoringEvents.value.filter(item => item.diagnosisStatus === 'Diagnosed' || item.diagnosisStatus === 'DiagnosedPartial').length,
    helper: monitoringEvents.value.find(item => item.diagnosisStatus === 'Diagnosed' || item.diagnosisStatus === 'DiagnosedPartial')?.targetAnchorKey || t('plc_monitoring.noDiagnosedEvents'),
  },
  {
    label: t('plc_monitoring.metricPendingEvents'),
    value: monitoringEvents.value.filter(item => item.diagnosisStatus === 'Pending').length,
    helper: monitoringEvents.value.find(item => item.diagnosisStatus === 'Pending')?.lastChangedSignal || t('plc_monitoring.noPendingEvents'),
  },
]))

const diagnosisStatusBoard = computed(() => {
  const definitions = [
    { key: 'Diagnosed', label: t('plc_monitoring.statusDiagnosed'), description: t('plc_monitoring.statusDiagnosedDesc'), tone: 'success' },
    { key: 'DiagnosedPartial', label: t('plc_monitoring.statusDiagnosedPartial'), description: t('plc_monitoring.statusDiagnosedPartialDesc'), tone: 'success' },
    { key: 'Pending', label: t('plc_monitoring.statusPending'), description: t('plc_monitoring.statusPendingDesc'), tone: 'warning' },
    { key: 'NoTemplate', label: t('plc_monitoring.statusNoTemplate'), description: t('plc_monitoring.statusNoTemplateDesc'), tone: 'warning' },
    { key: 'NoSignalMapping', label: t('plc_monitoring.statusNoSignalMapping'), description: t('plc_monitoring.statusNoSignalMappingDesc'), tone: 'warning' },
    { key: 'NoAst', label: t('plc_monitoring.statusNoAst'), description: t('plc_monitoring.statusNoAstDesc'), tone: 'warning' },
    { key: 'Error', label: t('plc_monitoring.statusError'), description: t('plc_monitoring.statusErrorDesc'), tone: 'danger' },
  ]

  return definitions.map((definition) => {
    const matchedEvents = monitoringEvents.value
      .filter(item => resolveDiagnosisBucketKey(item) === definition.key)
      .sort((left, right) => dayjs(right.triggeredAt).valueOf() - dayjs(left.triggeredAt).valueOf())

    return {
      ...definition,
      count: matchedEvents.length,
      latestTime: matchedEvents[0]?.triggeredAt || '',
      recentEventIds: matchedEvents.slice(0, 3).map(item => String(item.id ?? '')).filter(Boolean),
      recentAnchor: matchedEvents[0]?.targetAnchorKey || matchedEvents[0]?.diagnosisResult?.target?.anchorKey || '',
    }
  })
})

const activeDiagnosisStatusBoard = computed(() => diagnosisStatusBoard.value.filter(item => item.count > 0))

const monitoringSnapshotText = computed(() => {
  if (!monitoringSnapshot.value) {
    return '-'
  }

  return stringifyPretty(monitoringSnapshot.value)
})

watch(() => route.query.stationCode, (value) => {
  const stationCode = String(value ?? '').trim()
  if (stationCode && stationCode !== dashboardForm.value.stationCode) {
    dashboardForm.value.stationCode = stationCode
    void refreshCurrentStationData(false)
  }
})

watch(() => dashboardForm.value.stationCode, async (current, previous) => {
  if (current === previous || !projectId.value) {
    return
  }

  if (realtimeConnection?.state === signalR.HubConnectionState.Connected) {
    if (previous) {
      await realtimeConnection.invoke('UnsubscribeStation', Number(projectId.value), previous)
    }
    subscribedStationCode = ''
    if (current) {
      await realtimeConnection.invoke('SubscribeStation', Number(projectId.value), current)
      subscribedStationCode = current
    }
  }
})

watch([() => dashboardForm.value.autoRefreshEnabled, () => dashboardForm.value.autoRefreshSeconds, projectId], () => {
  startAutoRefresh()
})

watch(projectId, async (current, previous) => {
  if (current === previous) {
    return
  }

  if (!current) {
    await disconnectRealtime()
    resetDashboard()
    await loadProjectCatalog()
    return
  }

  dashboardForm.value.stationCode = initialStationCode.value
  resetDashboard()
  await refreshDashboard(true, true)
  await connectRealtime()
})

onMounted(async () => {
  loadRecentProjects()
  loadWorkerStatus()

  if (!projectId.value) {
    await loadProjectCatalog()
    return
  }

  await refreshDashboard(true, true)
  await connectRealtime()
})

onUnmounted(() => {
  stopAutoRefresh()
  stopBatchSeedPolling()
  void disconnectRealtime()
})
</script>

<template>
  <div class="monitoring-page">
    <FaPageHeader :title="projectInfo.projectName ? `${projectInfo.projectName} · ${$t('plc_monitoring.pageTitle')}` : $t('plc_monitoring.pageTitle')">
      <template #description>
        <div class="header-description">
          <span>{{ $t('plc_monitoring.headerDescription') }}</span>
          <span v-if="projectId">{{ $t('plc_monitoring.projectIdLabel') }}: {{ projectId }}</span>
        </div>
      </template>

      <div class="header-actions">
        <el-button @click="router.back()">
          <FaIcon name="i-ep:arrow-left" class="mr-1" />{{ $t('plc_monitoring.backBtn') }}
        </el-button>
        <el-button type="primary" plain @click="goProjectDetail">
          <FaIcon name="i-ep:document" class="mr-1" />{{ $t('plc_monitoring.projectDetailBtn') }}
        </el-button>
        <el-button type="success" :loading="loading || projectLoading" @click="refreshDashboard(true, true)">
          <FaIcon name="i-ep:refresh" class="mr-1" />{{ $t('plc_monitoring.refreshConsoleBtn') }}
        </el-button>
      </div>
    </FaPageHeader>

    <FaPageMain v-loading="loading || projectLoading" class="monitoring-page-main">
      <section v-if="!projectId" class="project-picker-shell">
        <div class="project-picker-hero">
          <div>
            <div class="project-picker-hero__eyebrow">MONITORING ENTRY</div>
            <h2>{{ $t('plc_monitoring.pickerTitle') }}</h2>
            <p>{{ $t('plc_monitoring.pickerSubtitle') }}</p>
          </div>
          <el-button type="primary" plain :loading="projectCatalogLoading" @click="loadProjectCatalog">
            <FaIcon name="i-ep:refresh" class="mr-1" />{{ $t('plc_monitoring.refreshProjectsBtn') }}
          </el-button>
        </div>

        <el-card shadow="never" class="project-picker-card">
          <template #header>
            <div class="section-header">
              <div>
                <span>{{ $t('plc_monitoring.projectSelectorLabel') }}</span>
                <p class="section-subtitle">{{ $t('plc_monitoring.projectSelectorSubtitle') }}</p>
              </div>
              <el-tag type="info" size="small">{{ $t('plc_monitoring.projectCount', { n: projectSelector.total }) }}</el-tag>
            </div>
          </template>

          <div v-if="recentProjectCatalog.length > 0" class="recent-project-shell">
            <div class="recent-project-shell__header">
              <span>{{ $t('plc_monitoring.recentAccess') }}</span>
              <small>{{ $t('plc_monitoring.recentAccessHint') }}</small>
            </div>
            <div class="recent-project-list">
              <button
                v-for="item in recentProjectCatalog"
                :key="`recent:${item.id}`"
                type="button"
                class="recent-project-chip"
                @click="openProjectMonitoring(item)"
              >
                <strong>{{ item.projectName || $t('plc_monitoring.projectFallback', { id: item.id }) }}</strong>
                <span>ID: {{ item.id }}</span>
              </button>
            </div>
          </div>

          <div class="project-picker-toolbar">
            <el-input
              v-model="projectSelector.keyword"
              clearable
              :placeholder="$t('plc_monitoring.projectKeywordPlaceholder')"
              @keyup.enter="searchProjectCatalog"
            />
            <el-button type="primary" :loading="projectCatalogLoading" @click="searchProjectCatalog">
              <FaIcon name="i-ep:search" class="mr-1" />{{ $t('plc_monitoring.searchProjectBtn') }}
            </el-button>
          </div>

          <div v-if="projectCatalog.length > 0" v-loading="projectCatalogLoading" class="project-picker-grid">
            <article v-for="item in projectCatalog" :key="item.id" class="project-picker-item">
              <div class="project-picker-item__header">
                <div>
                  <h3>{{ item.projectName || $t('plc_monitoring.projectFallback', { id: item.id }) }}</h3>
                  <span>ID: {{ item.id }}</span>
                </div>
                <el-tag type="success" size="small" round>{{ item.brand || 'PLC' }}</el-tag>
              </div>
              <p>{{ item.product || $t('plc_monitoring.noProduct') }} · {{ item.productVersion || $t('plc_monitoring.noVersion') }}</p>
              <div class="project-picker-item__meta">
                <span>{{ $t('plc_monitoring.stationIdLabel') }}: {{ item.stationId || '-' }}</span>
                <span>{{ formatDateTime(item.createTime) }}</span>
              </div>
              <div class="project-picker-item__actions">
                <el-button type="primary" @click="openProjectMonitoring(item)">{{ $t('plc_monitoring.enterMonitoringBtn') }}</el-button>
                <el-button text @click="router.push({ path: '/plc/detail', query: { id: item.id } })">{{ $t('plc_monitoring.projectDetailBtn') }}</el-button>
              </div>
            </article>
          </div>
          <el-empty v-else :description="projectCatalogLoading ? $t('plc_monitoring.loadingProjects') : $t('plc_monitoring.noProjectsFound')" />

          <div class="project-picker-pagination" v-if="projectSelector.total > projectSelector.limit">
            <el-pagination
              v-model:current-page="projectSelector.page"
              v-model:page-size="projectSelector.limit"
              :total="projectSelector.total"
              :page-sizes="[6, 12, 24, 48]"
              background
              layout="total, sizes, prev, pager, next"
              @current-change="handleProjectCatalogPageChange"
              @size-change="handleProjectCatalogSizeChange"
            />
          </div>
        </el-card>
      </section>

      <template v-else>
        <!-- ========== 紧凑状态栏（始终可见） ========== -->
        <section class="compact-status-bar">
          <div class="compact-status-bar__main">
            <div class="compact-status-bar__station">
              <strong>{{ resolveStationCode() || $t('plc_monitoring.noStationSelected') }}</strong>
              <el-tag :type="healthSummary.tone === 'excellent' ? 'success' : healthSummary.tone === 'watch' ? 'warning' : 'danger'" size="small" effect="dark" round>
                {{ healthSummary.label }}
              </el-tag>
              <el-tag :type="liveConnectionTagType" size="small" effect="plain" round>
                SignalR · {{ liveConnectionStatus }}
              </el-tag>
            </div>
            <div class="compact-status-bar__metrics">
              <span><b>{{ formatAgo(monitoringOverviewState?.lastSampleTime || monitoringOverviewState?.LastSampleTime || selectedStation?.lastSampleTime) }}</b> {{ $t('plc_monitoring.sampleLabel') }}</span>
              <span>{{ selectedStation?.isSilent || monitoringOverviewState?.isSilent || monitoringOverviewState?.IsSilent ? $t('plc_monitoring.silentState') : $t('plc_monitoring.activeState') }}</span>
              <span>{{ monitoringRuntimeLease?.status || monitoringRuntimeLease?.Status || selectedStation?.runtimeStatus || '-' }}</span>
              <span>{{ $t('plc_monitoring.samplesLabel') }} {{ monitoringSampleLibrary.length }} · {{ $t('plc_monitoring.historyLabel') }} {{ monitoringHistory.length }}</span>
            </div>
          </div>
          <el-button text @click="heroExpanded = !heroExpanded">
            {{ heroExpanded ? $t('plc_monitoring.collapseDetails') : $t('plc_monitoring.expandDashboard') }}
            <FaIcon :name="heroExpanded ? 'i-ep:arrow-up' : 'i-ep:arrow-down'" class="ml-1" />
          </el-button>
        </section>

        <!-- ========== 完整仪表盘（可折叠） ========== -->
        <section v-show="heroExpanded" class="hero-shell">
          <div class="hero-panel">
            <div class="hero-panel__backdrop" />
            <div class="hero-panel__content">
              <div class="hero-eyebrow">Q-POINT SHADOW MONITOR</div>
              <div class="hero-title-row">
                <div>
                  <h1>{{ resolveStationCode() || $t('plc_monitoring.pickStationHint') }}</h1>
                  <p>
                    {{ healthSummary.description }}
                  </p>
                </div>
                <div class="hero-status-stack">
                  <el-tag :type="healthSummary.tone === 'excellent' ? 'success' : healthSummary.tone === 'watch' ? 'warning' : 'danger'" effect="dark" round>
                    {{ healthSummary.label }}
                  </el-tag>
                  <el-tag :type="liveConnectionTagType" effect="plain" round>
                    SignalR · {{ liveConnectionStatus }}
                  </el-tag>
                </div>
              </div>

              <div class="hero-meta-grid">
                <div class="hero-meta-card">
                  <span class="hero-meta-card__label">{{ $t('plc_monitoring.latestSample') }}</span>
                  <strong>{{ formatDateTime(monitoringOverviewState?.lastSampleTime || monitoringOverviewState?.LastSampleTime || selectedStation?.lastSampleTime) }}</strong>
                  <small>{{ formatAgo(monitoringOverviewState?.lastSampleTime || monitoringOverviewState?.LastSampleTime || selectedStation?.lastSampleTime) }}</small>
                </div>
                <div class="hero-meta-card">
                  <span class="hero-meta-card__label">{{ $t('plc_monitoring.silentStatus') }}</span>
                  <strong>{{ selectedStation?.isSilent || monitoringOverviewState?.isSilent || monitoringOverviewState?.IsSilent ? $t('plc_monitoring.silentState') : $t('plc_monitoring.activeState') }}</strong>
                  <small>{{ formatDuration(selectedStation?.silentSeconds || monitoringOverviewState?.silentSeconds || monitoringOverviewState?.SilentSeconds) }}</small>
                </div>
                <div class="hero-meta-card">
                  <span class="hero-meta-card__label">Runtime</span>
                  <strong>{{ monitoringRuntimeLease?.status || monitoringRuntimeLease?.Status || selectedStation?.runtimeStatus || '-' }}</strong>
                  <small>{{ monitoringRuntimeLease?.nodeName || monitoringRuntimeLease?.NodeName || selectedStation?.runtimeNodeName || $t('plc_monitoring.unregisteredNode') }}</small>
                </div>
                <div class="hero-meta-card">
                  <span class="hero-meta-card__label">{{ $t('plc_monitoring.persistedSamples') }}</span>
                  <strong>{{ monitoringSampleLibrary.length }}</strong>
                  <small>{{ $t('plc_monitoring.historyReplayCount', { n: monitoringHistory.length }) }}</small>
                </div>
              </div>
            </div>
          </div>

          <el-card shadow="never" class="hero-control-card">
            <template #header>
              <div class="section-header">
                <span>{{ $t('plc_monitoring.samplingConsole') }}</span>
                <el-tag type="info" size="small">{{ $t('plc_monitoring.samplingConsoleTag') }}</el-tag>
              </div>
            </template>

            <el-form label-position="top" class="hero-control-form">
              <el-form-item :label="$t('plc_monitoring.monitorStation')">
                <el-select
                  v-model="dashboardForm.stationCode"
                  filterable
                  allow-create
                  default-first-option
                  :placeholder="$t('plc_monitoring.stationCodePlaceholder')"
                    :no-data-text="$t('plc_monitoring.noStationDataText')"
                  @change="applyStation(String($event || ''))"
                >
                  <el-option
                    v-for="station in monitoringStations"
                    :key="station.stationCode"
                    :label="`${station.stationCode} · ${station.status || $t('plc_monitoring.unnamedStatus')}`"
                    :value="station.stationCode"
                  />
                </el-select>
              </el-form-item>

              <div class="hero-control-grid">
                <el-form-item :label="$t('plc_monitoring.stationModeLabel')">
                  <el-input v-model="dashboardForm.stationMode" :placeholder="$t('plc_monitoring.optionalAutoHint')" clearable />
                </el-form-item>
                <el-form-item :label="$t('plc_monitoring.runtimeNodeLabel')">
                  <el-input v-model="dashboardForm.runtimeNodeName" :placeholder="$t('plc_monitoring.optional')" clearable />
                </el-form-item>
                <el-form-item :label="$t('plc_monitoring.signalLimitLabel')">
                  <el-input-number v-model="dashboardForm.signalLimit" :min="20" :max="500" :step="20" />
                </el-form-item>
                <el-form-item :label="$t('plc_monitoring.timelineDepthLabel')">
                  <el-input-number v-model="dashboardForm.timelineLimit" :min="10" :max="200" :step="10" />
                </el-form-item>
                <el-form-item :label="$t('plc_monitoring.autoRefreshLabel')">
                  <div class="switch-row">
                    <el-switch v-model="dashboardForm.autoRefreshEnabled" />
                    <el-input-number v-model="dashboardForm.autoRefreshSeconds" :min="5" :max="120" :step="5" />
                  </div>
                </el-form-item>
                <el-form-item :label="$t('plc_monitoring.mirrorFilterLabel')">
                  <el-segmented
                    v-model="dashboardForm.signalView"
                    :options="[
                      { label: $t('plc_monitoring.viewAll'), value: 'all' },
                      { label: $t('plc_monitoring.viewChanged'), value: 'changed' },
                      { label: $t('plc_monitoring.viewCore'), value: 'core' },
                    ]"
                  />
                </el-form-item>
              </div>

              <div class="switch-grid">
                <div class="switch-tile">
                  <span>{{ $t('plc_monitoring.persistHistoryLabel') }}</span>
                  <el-switch v-model="dashboardForm.persistHistory" />
                </div>
                <div class="switch-tile">
                  <span>{{ $t('plc_monitoring.persistSnapshotLabel') }}</span>
                  <el-switch v-model="dashboardForm.persistSnapshot" />
                </div>
                <div class="switch-tile">
                  <span>{{ $t('plc_monitoring.persistEventsLabel') }}</span>
                  <el-switch v-model="dashboardForm.persistEvents" />
                </div>
                  <div class="switch-tile">
                    <span>{{ $t('plc_monitoring.autoLearnLabel') }}</span>
                    <el-switch v-model="dashboardForm.rebuildPatternsAfterCycle" />
                  </div>
              </div>

                <div class="hero-control-grid hero-control-grid--compact">
                  <el-form-item :label="$t('plc_monitoring.patternSampleWindowLabel')">
                    <el-input-number v-model="dashboardForm.patternSampleLimit" :min="20" :max="300" :step="10" />
                  </el-form-item>
                </div>

              <div class="hero-actions">
                <el-button type="primary" :loading="loading" @click="runMonitoringCycle">
                  <FaIcon name="i-ep:video-play" class="mr-1" />{{ $t('plc_monitoring.runCycleBtn') }}
                </el-button>
                <el-button type="success" plain :loading="acceptanceRunning" @click="runMonitoringAcceptance">
                  <FaIcon name="i-ep:select" class="mr-1" />{{ $t('plc_monitoring.acceptanceBtn') }}
                </el-button>
                <el-button type="warning" :loading="batchSeedLoading" @click="batchSeedAllStations">
                  <FaIcon name="i-ep:refresh" class="mr-1" />{{ $t('plc_monitoring.rebuildAllSignals') }}
                </el-button>
                  <el-button type="warning" plain :loading="loading" @click="rebuildMonitoringPatternTemplates">
                    <FaIcon name="i-ep:cpu" class="mr-1" />{{ $t('plc_monitoring.rebuildPatterns') }}
                  </el-button>
                <el-button :loading="loading" @click="refreshDashboard(false, true)">
                  <FaIcon name="i-ep:refresh-right" class="mr-1" />{{ $t('plc_monitoring.refreshDataOnlyBtn') }}
                </el-button>
                <el-button type="info" plain :loading="liveReadLoading" @click="liveReadSignals">
                  <FaIcon name="i-ep:view" class="mr-1" />{{ $t('plc_monitoring.liveReadBtn') }}
                </el-button>
                <el-button
                  v-if="workerEnabled"
                  :type="workerPaused ? 'success' : 'danger'"
                  plain
                  :loading="workerControlLoading"
                  @click="toggleWorkerPause"
                >
                  <FaIcon :name="workerPaused ? 'i-ep:video-play' : 'i-ep:video-pause'" class="mr-1" />
                  {{ workerPaused ? $t('plc_monitoring.resumeWorker') : $t('plc_monitoring.pauseWorker') }}
                </el-button>
              </div>

              <div class="acceptance-panel">
                <div class="acceptance-panel__head">
                  <div>
                    <div class="run-result__title">{{ $t('plc_monitoring.acceptancePanelTitle') }}</div>
                    <p class="section-subtitle acceptance-panel__subtitle">{{ $t('plc_monitoring.acceptancePanelSubtitle') }}</p>
                  </div>
                  <div class="acceptance-panel__tags">
                    <el-tag size="small" :type="acceptanceStatusTagType(acceptanceFlow.finalStatus)">{{ acceptanceFlow.finalStatus === 'success' ? $t('plc_monitoring.finalDone') : acceptanceFlow.finalStatus === 'warning' ? $t('plc_monitoring.tagWarning') : acceptanceFlow.finalStatus === 'error' ? $t('plc_monitoring.failedText') : acceptanceRunning ? $t('plc_monitoring.runningText') : $t('plc_monitoring.pendingText') }}</el-tag>
                    <el-tag v-if="acceptanceFlow.lastRunAt" size="small" type="info">{{ acceptanceFlow.lastRunAt }}</el-tag>
                  </div>
                </div>

                <div class="acceptance-step-list">
                  <article v-for="step in acceptanceFlow.steps" :key="step.key" class="acceptance-step-row" :class="`acceptance-step-row--${step.status}`">
                    <div class="acceptance-step-row__main">
                      <strong>{{ step.title }}</strong>
                      <span>{{ step.detail }}</span>
                    </div>
                    <el-tag size="small" :type="acceptanceStatusTagType(step.status)">
                      {{ step.status === 'success' ? $t('plc_monitoring.stepDone') : step.status === 'warning' ? $t('plc_monitoring.stepWarningTag') : step.status === 'error' ? $t('plc_monitoring.failedText') : step.status === 'running' ? $t('plc_monitoring.runningText') : $t('plc_monitoring.pendingText') }}
                    </el-tag>
                  </article>
                </div>

                <div class="acceptance-panel__summary">{{ acceptanceFlow.summary }}</div>
              </div>

              <!-- ========== Q点实时值面板 ========== -->
              <div v-if="liveReadResult" class="acceptance-panel live-read-panel">
                <div class="acceptance-panel__head">
                  <div>
                    <div class="run-result__title">{{ $t('plc_monitoring.liveReadPanelTitle') }}</div>
                    <p class="section-subtitle acceptance-panel__subtitle">
                      {{ $t('plc_monitoring.deviceLabel') }}: {{ liveReadResult.deviceName || liveReadResult.DeviceName || '-' }}
                      &nbsp;|&nbsp;{{ $t('plc_monitoring.fallbackDeviceLabel') }}: {{ liveReadResult.fallbackDeviceName || liveReadResult.FallbackDeviceName || '-' }}
                      &nbsp;|&nbsp;{{ $t('plc_monitoring.readTimeLabel') }}: {{ liveReadResult.readTime || liveReadResult.ReadTime || '-' }}
                    </p>
                  </div>
                  <div class="acceptance-panel__tags">
                    <el-tag size="small" type="success">Good {{ liveReadResult.goodCount ?? liveReadResult.GoodCount ?? 0 }}</el-tag>
                    <el-tag size="small" type="danger">Bad {{ liveReadResult.badCount ?? liveReadResult.BadCount ?? 0 }}</el-tag>
                    <el-tag size="small" type="info">Total {{ liveReadResult.totalCount ?? liveReadResult.TotalCount ?? 0 }}</el-tag>
                  </div>
                </div>
                <div v-if="(liveReadResult.warnings ?? liveReadResult.Warnings ?? []).length > 0" class="live-read-warnings">
                  <el-alert v-for="(w, wi) in (liveReadResult.warnings ?? liveReadResult.Warnings ?? [])" :key="wi" type="warning" :closable="false" show-icon>{{ w }}</el-alert>
                </div>
                <el-table :data="liveReadResult.signals ?? liveReadResult.Signals ?? []" border stripe size="small" max-height="480" class="live-read-table">
                  <el-table-column label="#" type="index" width="50" />
                  <el-table-column label="SignalKey" prop="signalKey" min-width="180" show-overflow-tooltip>
                    <template #default="{ row }">{{ row.signalKey ?? row.SignalKey ?? '' }}</template>
                  </el-table-column>
                  <el-table-column :label="$t('plc_monitoring.variableNameColumn')" prop="variableName" min-width="220" show-overflow-tooltip>
                    <template #default="{ row }">{{ row.variableName ?? row.VariableName ?? '' }}</template>
                  </el-table-column>
                  <el-table-column :label="$t('plc_monitoring.anchorTypeColumn')" prop="anchorType" width="130">
                    <template #default="{ row }">{{ row.anchorType ?? row.AnchorType ?? '' }}</template>
                  </el-table-column>
                  <el-table-column :label="$t('plc_monitoring.liveValueColumn')" prop="value" width="120">
                    <template #default="{ row }">
                      <span :class="{ 'live-read-value--true': row.value === true || row.value === 1, 'live-read-value--false': row.value === false || row.value === 0 }">
                        {{ row.value ?? row.Value ?? '-' }}
                      </span>
                    </template>
                  </el-table-column>
                  <el-table-column :label="$t('plc_monitoring.qualityColumn')" prop="quality" width="100">
                    <template #default="{ row }">
                      <el-tag :type="(row.isGood ?? row.IsGood) ? 'success' : 'danger'" size="small">{{ row.quality ?? row.Quality ?? '-' }}</el-tag>
                    </template>
                  </el-table-column>
                </el-table>
              </div>

              <div class="acceptance-panel diagnosis-board-panel">
                <div class="acceptance-panel__head">
                  <div>
                    <div class="run-result__title">{{ $t('plc_monitoring.diagnosisBoardTitle') }}</div>
                    <p class="section-subtitle acceptance-panel__subtitle">{{ $t('plc_monitoring.diagnosisBoardSubtitle') }}</p>
                  </div>
                  <div class="acceptance-panel__tags">
                    <el-tag size="small" type="info">{{ $t('plc_monitoring.eventCount', { n: monitoringEvents.length }) }}</el-tag>
                  </div>
                </div>

                <div v-if="activeDiagnosisStatusBoard.length > 0" class="diagnosis-board-grid">
                  <article v-for="bucket in activeDiagnosisStatusBoard" :key="bucket.key" class="diagnosis-board-card" :class="`diagnosis-board-card--${bucket.tone}`">
                    <div class="diagnosis-board-card__head">
                      <div>
                        <strong>{{ bucket.label }}</strong>
                        <p>{{ bucket.description }}</p>
                      </div>
                      <el-tag size="small" :type="diagnosisStatusTagType(bucket.key)">{{ bucket.count }}</el-tag>
                    </div>
                    <div class="diagnosis-board-card__meta">
                      <span>{{ $t('plc_monitoring.latestTimeLabel') }}</span>
                      <strong>{{ formatDateTime(bucket.latestTime) }}</strong>
                    </div>
                    <div class="diagnosis-board-card__meta">
                      <span>{{ $t('plc_monitoring.latestAnchorLabel') }}</span>
                      <strong>{{ bucket.recentAnchor || '-' }}</strong>
                    </div>
                    <div v-if="bucket.recentEventIds.length > 0" class="diagnosis-board-card__events">
                      <button
                        v-for="eventId in bucket.recentEventIds"
                        :key="eventId"
                        type="button"
                        class="diagnosis-board-card__event-pill"
                        @click="selectMonitoringEvent(eventId)"
                      >
                        #{{ eventId }}
                      </button>
                    </div>
                  </article>
                </div>
                <el-empty v-else :description="$t('plc_monitoring.noDiagnosisEvents')" />
              </div>
            </el-form>
          </el-card>
        </section>

        <section class="metric-strip">
          <article v-for="item in heroMetrics" :key="item.label" class="metric-tile">
            <span class="metric-tile__label">{{ item.label }}</span>
            <strong class="metric-tile__value">{{ item.value }}</strong>
            <small class="metric-tile__helper">{{ item.helper }}</small>
          </article>
        </section>

        <section class="dashboard-grid">
          <div class="dashboard-main">
            <!-- ========== 左栏摘要：展开/收起详情切换 ========== -->
            <div class="detail-toggle-bar">
              <el-button text type="primary" @click="detailSectionsVisible = !detailSectionsVisible">
                <FaIcon :name="detailSectionsVisible ? 'i-ep:arrow-up' : 'i-ep:arrow-down'" class="mr-1" />
                {{ detailSectionsVisible ? $t('plc_monitoring.collapseDetailsSections') : $t('plc_monitoring.expandDetailsSections') }}
              </el-button>
              <span class="detail-toggle-bar__hint">{{ $t('plc_monitoring.storyLineCount', { n: storyItems.length }) }} · {{ $t('plc_monitoring.sampleCountShort', { n: learningSamples.length }) }} · {{ $t('plc_monitoring.signalCountShort', { n: monitoringSignals.length }) }}</span>
            </div>

            <template v-if="detailSectionsVisible">
            <el-card shadow="never" class="story-card">
              <template #header>
                <div class="section-header">
                  <div>
                    <span>{{ $t('plc_monitoring.storyCardTitle') }}</span>
                    <p class="section-subtitle">{{ $t('plc_monitoring.storyCardSubtitle') }}</p>
                  </div>
                  <el-tag type="info" size="small">{{ $t('plc_monitoring.countItems', { n: storyItems.length }) }}</el-tag>
                </div>
              </template>

              <div v-if="storyItems.length > 0" class="story-list">
                <article
                  v-for="item in storyItems"
                  :key="item.id"
                  class="story-item"
                  :class="[
                    `story-item--${item.kind}`,
                    item.kind === 'event' && item.eventId && String(item.eventId) === selectedMonitoringEventId ? 'story-item--selected' : '',
                    item.kind === 'event' ? 'story-item--interactive' : '',
                  ]"
                  @click="item.kind === 'event' ? selectMonitoringEvent(item.eventId) : undefined"
                >
                  <div class="story-item__rail" />
                  <div class="story-item__timestamp">
                    <strong>{{ item.timeText }}</strong>
                    <span>{{ item.agoText }}</span>
                  </div>
                  <div class="story-item__content">
                    <div class="story-item__head">
                      <span>{{ item.title }}</span>
                      <el-tag :type="item.kind === 'event' ? 'danger' : item.kind === 'bundle' ? 'warning' : 'success'" size="small" round>
                        {{ item.kindLabel }}
                      </el-tag>
                    </div>
                    <p>{{ item.summary }}</p>
                    <div class="story-item__tags">
                      <span v-for="tag in item.tags" :key="tag" class="story-pill">{{ tag }}</span>
                    </div>
                    <div class="story-item__pulse">
                      <span :style="{ width: `${item.intensity}%` }" />
                    </div>
                  </div>
                </article>
              </div>
              <el-empty v-else :description="$t('plc_monitoring.noTimelineSamples')" />
            </el-card>

            <div class="split-grid">
              <el-card shadow="never" class="sample-card">
                <template #header>
                  <div class="section-header">
                    <div>
                      <span>{{ $t('plc_monitoring.learningSamplesTitle') }}</span>
                      <p class="section-subtitle">{{ $t('plc_monitoring.learningSamplesSubtitle') }}</p>
                    </div>
                    <el-tag type="success" size="small">{{ $t('plc_monitoring.countPieces', { n: learningSamples.length }) }}</el-tag>
                  </div>
                </template>

                <div class="sample-grid" v-if="learningSamples.length > 0">
                  <article v-for="sample in learningSamples" :key="sample.id" class="sample-tile" :class="`sample-tile--${sample.tone}`">
                    <div class="sample-tile__header">
                      <h3>{{ sample.title }}</h3>
                      <span>{{ sample.subtitle }}</span>
                    </div>
                    <p>{{ sample.insight }}</p>
                    <div class="sample-tile__cues">
                      <span v-for="cue in sample.cues" :key="cue">{{ cue }}</span>
                    </div>
                  </article>
                </div>
                <el-empty v-else :description="$t('plc_monitoring.noSamplesYet')" />

                <div class="insight-stack">
                  <div v-for="insight in learnedInsights" :key="insight" class="insight-row">
                    <FaIcon name="i-ep:opportunity" class="insight-row__icon" />
                    <span>{{ insight }}</span>
                  </div>
                </div>
              </el-card>

              <el-card shadow="never" class="leader-card">
                <template #header>
                  <div class="section-header">
                    <div>
                      <span>{{ $t('plc_monitoring.leadersTitle') }}</span>
                      <p class="section-subtitle">{{ $t('plc_monitoring.leadersSubtitle') }}</p>
                    </div>
                  </div>
                </template>

                <div class="leader-stack">
                  <div v-for="signal in signalLeaders" :key="signal.signalKey" class="leader-row">
                    <div class="leader-row__main">
                      <strong>{{ signal.signalKey }}</strong>
                      <span>{{ signal.groupName || signal.signalType || $t('plc_monitoring.ungrouped') }}</span>
                    </div>
                    <div class="leader-row__meta">
                      <span>{{ $t('plc_monitoring.changeCountTimes', { n: signal.changeCount }) }}</span>
                      <span>{{ signal.isCore ? $t('plc_monitoring.coreShort') : $t('plc_monitoring.contextShort') }}</span>
                    </div>
                    <div class="leader-row__bar">
                      <span :style="{ width: `${signal.score}%` }" />
                    </div>
                  </div>
                </div>

                <div class="hotspot-stack" v-if="groupHotspots.length > 0">
                  <div v-for="group in groupHotspots" :key="group.name" class="hotspot-row">
                    <div>
                      <strong>{{ group.name }}</strong>
                      <span>{{ formatDateTime(group.lastChangedAt) }}</span>
                    </div>
                    <el-tag type="warning" size="small">{{ $t('plc_monitoring.timesCount', { n: group.count }) }}</el-tag>
                  </div>
                </div>
              </el-card>
            </div>

            <div class="split-grid persisted-grid">
              <el-card shadow="never" class="sample-card">
                <template #header>
                  <div class="section-header">
                    <div>
                      <span>{{ $t('plc_monitoring.persistedLibraryTitle') }}</span>
                      <p class="section-subtitle">{{ $t('plc_monitoring.persistedLibrarySubtitle') }}</p>
                    </div>
                    <el-tag type="warning" size="small">{{ $t('plc_monitoring.countItems', { n: monitoringSampleLibrary.length }) }}</el-tag>
                  </div>
                </template>

                <div v-if="persistedSampleHighlights.length > 0" class="sample-grid persisted-sample-grid">
                  <article v-for="sample in persistedSampleHighlights" :key="sample.sampleKey" class="sample-tile sample-tile--warning persisted-sample-tile">
                    <div class="sample-tile__header">
                      <h3>{{ sample.sampleLabel || sample.sampleKey || $t('plc_monitoring.unnamedSample') }}</h3>
                      <span>{{ sample.sampleType || 'HistorySample' }} · {{ sample.stationMode || $t('plc_monitoring.unmarkedMode') }}</span>
                    </div>
                    <p>{{ sample.sampleSummary || $t('plc_monitoring.sampleSummaryFallback') }}</p>
                    <div class="sample-meta-grid">
                      <div>
                        <span>{{ $t('plc_monitoring.changeCountLabel') }}</span>
                        <strong>{{ sample.changeCount }}</strong>
                      </div>
                      <div>
                        <span>{{ $t('plc_monitoring.signalCountLabel') }}</span>
                        <strong>{{ sample.signalCount }}</strong>
                      </div>
                      <div>
                        <span>{{ $t('plc_monitoring.eventLevelLabel') }}</span>
                        <strong>{{ sample.eventLevel || '-' }}</strong>
                      </div>
                      <div>
                        <span>{{ $t('plc_monitoring.latestTriggerLabel') }}</span>
                        <strong>{{ formatDateTime(sample.latestTriggeredAt || sample.endTime) }}</strong>
                      </div>
                    </div>
                    <div class="sample-tile__cues">
                      <span v-for="cue in sample.signalKeys.slice(0, 6)" :key="cue">{{ cue }}</span>
                    </div>
                  </article>
                </div>
                <el-empty v-else :description="$t('plc_monitoring.noPersistedSamples')" />
              </el-card>

              <el-card shadow="never" class="sample-card">
                <template #header>
                  <div class="section-header">
                    <div>
                      <span>{{ $t('plc_monitoring.patternLibraryTitle') }}</span>
                      <p class="section-subtitle">{{ $t('plc_monitoring.patternLibrarySubtitle') }}</p>
                    </div>
                    <el-tag type="success" size="small">{{ $t('plc_monitoring.countItems', { n: monitoringPatternTemplates.length }) }}</el-tag>
                  </div>
                </template>

                <div v-if="patternTemplateHighlights.length > 0" class="sample-grid persisted-sample-grid">
                  <article v-for="template in patternTemplateHighlights" :key="template.id || template.templateName" class="sample-tile sample-tile--accent persisted-sample-tile">
                    <div class="sample-tile__header">
                      <h3>{{ template.stationCode || $t('plc_monitoring.unknownStation') }} / {{ template.stationMode || 'Default' }} · v{{ template.templateVersion || 1 }}</h3>
                      <span>{{ template.patternType || 'ExpectedNextBundle' }}</span>
                    </div>
                    <div class="sample-meta-grid">
                      <div>
                        <span>{{ $t('plc_monitoring.supportCountLabel') }}</span>
                        <strong>{{ template.supportCount || 0 }}</strong>
                      </div>
                      <div>
                        <span>{{ $t('plc_monitoring.confidenceLabel') }}</span>
                        <strong>{{ Math.round((Number(template.confidence || 0) * 100)) }}%</strong>
                      </div>
                      <div>
                        <span>{{ $t('plc_monitoring.signalCountLabel') }}</span>
                        <strong>{{ (template.lastBundleSignature || '').split('|').filter(Boolean).length }}</strong>
                      </div>
                      <div>
                        <span>{{ $t('plc_monitoring.rebuildTimeLabel') }}</span>
                        <strong>{{ formatDateTime(template.lastRebuildTime) }}</strong>
                      </div>
                    </div>
                    <p class="pattern-template-remark">{{ template.remark || $t('plc_monitoring.templateRemarkFallback') }}</p>
                    <el-collapse class="pattern-template-collapse">
                      <el-collapse-item :title="$t('plc_monitoring.viewSignature')" name="sig">
                        <div class="pattern-template-signature">
                          <span v-for="sig in (template.lastBundleSignature || '').split('|').filter(Boolean)" :key="sig" class="story-pill">{{ sig }}</span>
                        </div>
                      </el-collapse-item>
                    </el-collapse>
                  </article>
                </div>
                <el-empty v-else :description="$t('plc_monitoring.noPatternTemplates')" />
              </el-card>

              <el-card shadow="never" class="leader-card">
                <template #header>
                  <div class="section-header">
                    <div>
                      <span>{{ $t('plc_monitoring.historyReplayTitle') }}</span>
                      <p class="section-subtitle">{{ $t('plc_monitoring.historyReplaySubtitle') }}</p>
                    </div>
                    <el-tag type="info" size="small">{{ $t('plc_monitoring.countItems', { n: monitoringHistory.length }) }}</el-tag>
                  </div>
                </template>

                <div v-if="monitoringHistory.length > 0" class="history-stack">
                  <article v-for="item in monitoringHistory.slice(0, 10)" :key="`${item.id}:${item.changedAt}`" class="history-row">
                    <div class="history-row__head">
                      <strong>{{ item.signalKey || item.variableName || $t('plc_monitoring.unknownSignal') }}</strong>
                      <span>{{ formatDateTime(item.changedAt) }}</span>
                    </div>
                    <p>{{ formatValue(item.oldValue) }} -> {{ formatValue(item.newValue) }}</p>
                    <div class="history-row__meta">
                      <span>{{ item.groupName || item.signalType || $t('plc_monitoring.ungrouped') }}</span>
                      <span>{{ item.stationMode || $t('plc_monitoring.unmarkedMode') }}</span>
                      <span>{{ item.quality || $t('plc_monitoring.noQualityMark') }}</span>
                    </div>
                  </article>
                </div>
                <el-empty v-else :description="$t('plc_monitoring.noHistoryChanges')" />
              </el-card>
            </div>

            <el-card shadow="never" class="mirror-card">
              <template #header>
                <div class="section-header">
                  <div>
                    <span>{{ $t('plc_monitoring.mirrorTitle') }}</span>
                    <p class="section-subtitle">{{ $t('plc_monitoring.mirrorSubtitle') }}</p>
                  </div>
                  <el-tag type="info" size="small">{{ filteredSignals.length }} / {{ monitoringSignals.length }}</el-tag>
                </div>
              </template>

              <div class="mirror-toolbar">
                <el-input
                  v-model="dashboardForm.signalKeyword"
                  :placeholder="$t('plc_monitoring.mirrorSearchPlaceholder')"
                  clearable
                  class="mirror-toolbar__search"
                />
              </div>

              <el-table :data="filteredSignals" stripe max-height="520">
                <el-table-column prop="signalKey" label="SignalKey" min-width="220" show-overflow-tooltip />
                <el-table-column prop="variableName" :label="$t('plc_monitoring.variableNameLabel')" min-width="220" show-overflow-tooltip />
                <el-table-column :label="$t('plc_monitoring.valueShiftColumn')" width="190">
                  <template #default="{ row }">
                    <div class="value-shift">
                      <span>{{ formatValue(row.previousValue) }}</span>
                      <FaIcon name="i-ep:right" />
                      <strong>{{ formatValue(row.currentValue) }}</strong>
                    </div>
                  </template>
                </el-table-column>
                <el-table-column prop="semanticLabel" :label="$t('plc_monitoring.semanticColumn')" width="160" show-overflow-tooltip />
                <el-table-column prop="groupName" :label="$t('plc_monitoring.groupColumn')" width="150" show-overflow-tooltip />
                <el-table-column :label="$t('plc_monitoring.typeColumn')" width="140">
                  <template #default="{ row }">
                    <el-tag :type="row.isCore ? 'success' : 'info'" size="small">{{ row.signalType || (row.isCore ? 'Core' : 'Context') }}</el-tag>
                  </template>
                </el-table-column>
                <el-table-column :label="$t('plc_monitoring.latestChangeColumn')" min-width="180">
                  <template #default="{ row }">
                    <div class="change-time-cell">
                      <strong>{{ formatDateTime(row.lastChangeTime) }}</strong>
                      <span>{{ formatAgo(row.lastChangeTime) }}</span>
                    </div>
                  </template>
                </el-table-column>
                <el-table-column prop="quality" :label="$t('plc_monitoring.qualityColumn')" width="110" />
              </el-table>
            </el-card>
            </template>
          </div>

          <div class="dashboard-side">
            <el-card shadow="never" class="station-card">
              <template #header>
                <div class="section-header">
                  <div>
                    <span>{{ $t('plc_monitoring.stationRadarTitle') }}</span>
                    <p class="section-subtitle">{{ $t('plc_monitoring.stationRadarSubtitle') }}</p>
                  </div>
                  <div style="display: flex; align-items: center; gap: 8px;">
                    <el-tag type="info" size="small">{{ $t('plc_monitoring.countPieces', { n: monitoringStations.length }) }}</el-tag>
                  </div>
                </div>
              </template>

              <div class="station-stack" v-if="monitoringStations.length > 0">
                <button
                  v-for="station in monitoringStations"
                  :key="station.stationCode"
                  type="button"
                  class="station-tile"
                  :class="{ 'is-active': station.stationCode === resolveStationCode() }"
                  @click="applyStation(station.stationCode)"
                >
                  <div class="station-tile__head">
                    <strong>{{ station.stationCode }}</strong>
                    <el-tag :type="station.isSilent ? 'danger' : 'success'" size="small" round>
                      {{ station.isSilent ? $t('plc_monitoring.silentShort') : $t('plc_monitoring.activeShort') }}
                    </el-tag>
                  </div>
                  <div class="station-tile__meta">
                    <span>{{ station.status || $t('plc_monitoring.unmarkedStatus') }}</span>
                    <span>{{ station.stationMode || $t('plc_monitoring.unmarkedMode') }}</span>
                  </div>
                  <div class="station-tile__foot">
                    <span>{{ $t('plc_monitoring.coreCount', { n: station.coreSignalCount }) }}</span>
                    <span>{{ formatAgo(station.lastSampleTime) }}</span>
                  </div>
                </button>
              </div>
              <el-empty v-else :description="$t('plc_monitoring.noStationConfigs')" />
            </el-card>

            <el-card v-show="detailSectionsVisible" shadow="never" class="runtime-card live-card">
              <template #header>
                <div class="section-header">
                  <div>
                    <span>{{ $t('plc_monitoring.livePulseTitle') }}</span>
                    <p class="section-subtitle">{{ $t('plc_monitoring.livePulseSubtitle') }}</p>
                  </div>
                  <el-tag :type="liveConnectionTagType" size="small">{{ liveConnectionStatus }}</el-tag>
                </div>
              </template>

              <div v-if="livePulsePreview.length > 0" class="live-pulse-stack">
                <article v-for="pulse in livePulsePreview" :key="pulse.id" class="live-pulse-row" :class="`live-pulse-row--${pulse.tone}`">
                  <div class="live-pulse-row__head">
                    <strong>{{ pulse.title }}</strong>
                    <span>{{ formatDateTime(pulse.time) }}</span>
                  </div>
                  <p>{{ pulse.summary }}</p>
                  <div class="live-pulse-row__meta">
                    <span>{{ pulse.stationCode || $t('plc_monitoring.unknownStation') }}</span>
                    <span>{{ formatAgo(pulse.time) }}</span>
                  </div>
                </article>
              </div>
              <el-empty v-else :description="$t('plc_monitoring.noLivePulse')" />
            </el-card>

            <el-card shadow="never" class="runtime-card">
              <template #header>
                <div class="section-header">
                  <div>
                    <span>{{ $t('plc_monitoring.runtimeSummaryTitle') }}</span>
                    <p class="section-subtitle">{{ $t('plc_monitoring.runtimeSummarySubtitle') }}</p>
                  </div>
                </div>
              </template>

              <el-descriptions :column="1" border>
                <el-descriptions-item :label="$t('plc_monitoring.currentStationLabel')">{{ resolveStationCode() || '-' }}</el-descriptions-item>
                <el-descriptions-item :label="$t('plc_monitoring.stationStatusLabel')">{{ monitoringOverviewState?.status || monitoringOverviewState?.Status || selectedStation?.status || '-' }}</el-descriptions-item>
                <el-descriptions-item :label="$t('plc_monitoring.stationModeLabel')">{{ monitoringOverviewState?.stationMode || monitoringOverviewState?.StationMode || selectedStation?.stationMode || '-' }}</el-descriptions-item>
                <el-descriptions-item :label="$t('plc_monitoring.silentDurationLabel')">{{ formatDuration(monitoringOverviewState?.silentSeconds || monitoringOverviewState?.SilentSeconds || selectedStation?.silentSeconds) }}</el-descriptions-item>
                <el-descriptions-item :label="$t('plc_monitoring.runtimeStatusLabel')">{{ monitoringRuntimeLease?.status || monitoringRuntimeLease?.Status || selectedStation?.runtimeStatus || '-' }}</el-descriptions-item>
                <el-descriptions-item :label="$t('plc_monitoring.runtimeNodeLabel')">{{ monitoringRuntimeLease?.nodeName || monitoringRuntimeLease?.NodeName || selectedStation?.runtimeNodeName || '-' }}</el-descriptions-item>
                <el-descriptions-item :label="$t('plc_monitoring.currentEventLabel')">{{ monitoringLatestEvent?.eventType || '-' }}</el-descriptions-item>
                <el-descriptions-item :label="$t('plc_monitoring.eventTimeLabel')">{{ formatDateTime(monitoringLatestEvent?.triggeredAt) }}</el-descriptions-item>
              </el-descriptions>

              <div v-if="monitoringEventHighlights.length > 0" class="event-selector-stack">
                <button
                  v-for="eventItem in monitoringEventHighlights"
                  :key="eventItem.id"
                  type="button"
                  class="event-selector-row"
                  :class="{ 'is-active': String(eventItem.id ?? '') === selectedMonitoringEventId }"
                  @click="selectMonitoringEvent(eventItem.id)"
                >
                  <div class="event-selector-row__head">
                    <strong>{{ eventItem.eventType || $t('plc_monitoring.unknownEvent') }}</strong>
                    <el-tag size="small" :type="eventItem.diagnosisStatus === 'Diagnosed' ? 'success' : eventItem.diagnosisStatus === 'Pending' ? 'warning' : 'info'">
                      {{ eventItem.diagnosisStatus || $t('plc_monitoring.undiagnosed') }}
                    </el-tag>
                  </div>
                  <div class="event-selector-row__meta">
                    <span>{{ formatDateTime(eventItem.triggeredAt) }}</span>
                    <span>{{ eventItem.targetAnchorKey || eventItem.lastChangedSignal || '-' }}</span>
                  </div>
                </button>
              </div>

              <div v-if="monitoringLatestEvent" class="diagnosis-panel">
                <div class="diagnosis-panel__head">
                  <div>
                    <div class="run-result__title">{{ $t('plc_monitoring.diagnosisPanelTitle') }}</div>
                    <p class="section-subtitle diagnosis-panel__subtitle">{{ $t('plc_monitoring.diagnosisPanelSubtitle') }}</p>
                  </div>
                  <div class="diagnosis-panel__tags">
                    <el-tag size="small" :type="monitoringLatestEvent.diagnosisStatus === 'Diagnosed' ? 'success' : monitoringLatestEvent.diagnosisStatus === 'Pending' ? 'warning' : 'info'">
                      {{ monitoringLatestEvent.diagnosisStatus || $t('plc_monitoring.undiagnosed') }}
                    </el-tag>
                    <el-tag v-if="monitoringLatestDiagnosis?.schemaVersion" size="small" type="info">{{ monitoringLatestDiagnosis.schemaVersion }}</el-tag>
                    <el-tag v-if="monitoringLatestDiagnosisTarget?.resolutionSource" size="small" type="warning">{{ monitoringLatestDiagnosisTarget.resolutionSource }}</el-tag>
                  </div>
                </div>

                <el-alert
                  v-if="monitoringLatestDiagnosis?.reason || monitoringLatestDiagnosis?.error"
                  :title="monitoringLatestDiagnosis?.reason || $t('plc_monitoring.diagnosisErrorTitle')"
                  :description="monitoringLatestDiagnosis?.error || $t('plc_monitoring.diagnosisDefaultDesc')"
                  :type="monitoringLatestDiagnosis?.error ? 'error' : 'warning'"
                  :closable="false"
                  class="diagnosis-panel__alert"
                />

                <div class="diagnosis-grid">
                  <div>
                    <span>{{ $t('plc_monitoring.templateLabel') }}</span>
                    <strong>{{ monitoringLatestDiagnosisTemplate?.templateName || '-' }}</strong>
                  </div>
                  <div>
                    <span>{{ $t('plc_monitoring.templateConfidenceLabel') }}</span>
                    <strong>{{ monitoringLatestDiagnosisTemplate?.confidence != null ? formatPercent(monitoringLatestDiagnosisTemplate.confidence) : '-' }}</strong>
                  </div>
                  <div>
                    <span>{{ $t('plc_monitoring.expectedBundleLabel') }}</span>
                    <strong>{{ monitoringLatestDiagnosisTemplate?.expectedBundleSignature || monitoringLatestExpectedBundle?.bundleSignature || '-' }}</strong>
                  </div>
                  <div>
                    <span>{{ $t('plc_monitoring.missingSignalsLabel') }}</span>
                    <strong>{{ monitoringLatestMissingSignalKeys.length }}</strong>
                  </div>
                  <div>
                    <span>Target Anchor</span>
                    <strong>{{ monitoringLatestDiagnosisTarget?.anchorKey || monitoringLatestEvent.targetAnchorKey || '-' }}</strong>
                  </div>
                  <div>
                    <span>Target LogicUnit</span>
                    <strong>{{ monitoringLatestDiagnosisTarget?.logicUnitId ?? monitoringLatestDiagnosisAst?.logicUnitId ?? '-' }}</strong>
                  </div>
                  <div>
                    <span>Target Variable</span>
                    <strong>{{ monitoringLatestDiagnosisTarget?.variableName || monitoringLatestDiagnosisAst?.variableName || '-' }}</strong>
                  </div>
                  <div>
                    <span>{{ $t('plc_monitoring.astStatusLabel') }}</span>
                    <strong>{{ monitoringLatestDiagnosisAst?.evaluationStatus || '-' }}</strong>
                  </div>
                  <div>
                    <span>{{ $t('plc_monitoring.astResultLabel') }}</span>
                    <strong>{{ monitoringLatestDiagnosisAst?.evaluatedValue === true ? 'true' : monitoringLatestDiagnosisAst?.evaluatedValue === false ? 'false' : '-' }}</strong>
                  </div>
                  <div>
                    <span>{{ $t('plc_monitoring.diagnosisTimeLabel') }}</span>
                    <strong>{{ formatDateTime(monitoringLatestDiagnosis?.diagnosedAt) }}</strong>
                  </div>
                </div>

                <div v-if="monitoringLatestMissingSignalKeys.length > 0" class="diagnosis-pill-list">
                  <span v-for="signalKey in monitoringLatestMissingSignalKeys" :key="signalKey" class="story-pill">{{ signalKey }}</span>
                </div>

                <div v-if="(monitoringLatestDiagnosisAst?.messages?.length || 0) > 0" class="diagnosis-note-list">
                  <div v-for="message in monitoringLatestDiagnosisAst?.messages || []" :key="message" class="diagnosis-note-row">
                    <FaIcon name="i-ep:warning-filled" class="diagnosis-note-row__icon" />
                    <span>{{ message }}</span>
                  </div>
                </div>

                <el-collapse class="snapshot-collapse">
                  <el-collapse-item :title="$t('plc_monitoring.expandDiagnosisJson')" name="diagnosis-json">
                    <div class="raw-json">{{ monitoringLatestDiagnosisText }}</div>
                  </el-collapse-item>
                </el-collapse>
              </div>

              <div class="run-result" v-if="normalizedRunResult">
                <div class="run-result__title">{{ $t('plc_monitoring.lastRunCycleTitle') }}</div>
                <div class="run-result__grid">
                  <div>
                    <span>{{ $t('plc_monitoring.statusLabel') }}</span>
                    <strong>{{ normalizedRunResult.status }}</strong>
                  </div>
                  <div>
                    <span>{{ $t('plc_monitoring.changedSignals') }}</span>
                    <strong>{{ normalizedRunResult.changedSignalCount }}</strong>
                  </div>
                  <div>
                    <span>{{ $t('plc_monitoring.silentLabel') }}</span>
                    <strong>{{ normalizedRunResult.isSilent ? $t('common.yes') : $t('common.no') }}</strong>
                  </div>
                  <div>
                    <span>{{ $t('plc_monitoring.missingSymbolsLabel') }}</span>
                    <strong>{{ normalizedRunResult.missingSymbolCount }}</strong>
                  </div>
                </div>
                <div v-if="normalizedRunResult.warnings.length > 0" class="run-result__warnings">
                  <span v-for="warning in normalizedRunResult.warnings" :key="warning">{{ warning }}</span>
                </div>
              </div>
            </el-card>

            <el-card v-show="detailSectionsVisible" shadow="never" class="bundle-card">
              <template #header>
                <div class="section-header">
                  <div>
                    <span>{{ $t('plc_monitoring.bundleBandTitle') }}</span>
                    <p class="section-subtitle">{{ $t('plc_monitoring.bundleBandSubtitle') }}</p>
                  </div>
                </div>
              </template>

              <div class="bundle-stack" v-if="recentBundleSource.length > 0">
                <article v-for="bundle in recentBundleSource.slice(0, 6)" :key="bundle.bundleId" class="bundle-row">
                  <div class="bundle-row__head">
                    <strong>{{ bundle.bundleLabel || bundle.bundleSignature || bundle.bundleId }}</strong>
                    <span>{{ formatDateTime(bundle.endTime || bundle.startTime) }}</span>
                  </div>
                  <p>{{ bundle.stationMode || $t('plc_monitoring.unmarkedMode') }} · {{ $t('plc_monitoring.concurrentChanges', { n: bundle.concurrencyCount || bundle.signalKeys.length }) }}</p>
                  <div class="bundle-row__signals">
                    <span v-for="signalKey in bundle.signalKeys.slice(0, 5)" :key="signalKey">{{ signalKey }}</span>
                  </div>
                </article>
              </div>
              <el-empty v-else :description="$t('plc_monitoring.noBundleSamples')" />
            </el-card>

            <el-card v-show="detailSectionsVisible" shadow="never" class="snapshot-card">
              <template #header>
                <div class="section-header">
                  <div>
                    <span>{{ $t('plc_monitoring.snapshotViewTitle') }}</span>
                    <p class="section-subtitle">{{ $t('plc_monitoring.snapshotViewSubtitle') }}</p>
                  </div>
                </div>
              </template>

              <el-descriptions :column="1" border v-if="monitoringSnapshot">
                <el-descriptions-item label="SnapshotId">{{ monitoringSnapshot.snapshotId || '-' }}</el-descriptions-item>
                <el-descriptions-item label="SnapshotTime">{{ formatDateTime(monitoringSnapshot.snapshotTime) }}</el-descriptions-item>
                <el-descriptions-item label="TriggerType">{{ monitoringSnapshot.triggerType || '-' }}</el-descriptions-item>
                <el-descriptions-item label="RequestedSymbols">{{ monitoringSnapshot.requestedSymbols?.length || 0 }}</el-descriptions-item>
              </el-descriptions>
              <el-empty v-else :description="$t('plc_monitoring.noSnapshotCache')" />

              <el-collapse class="snapshot-collapse">
                <el-collapse-item :title="$t('plc_monitoring.expandSnapshotJson')" name="snapshot-json">
                  <div class="raw-json">{{ monitoringSnapshotText }}</div>
                </el-collapse-item>
              </el-collapse>
            </el-card>
          </div>
        </section>
      </template>
    </FaPageMain>

  <!-- 批量监控信号播种进度弹窗 -->
  <el-dialog
    v-model="batchSeedDialogVisible"
    :title="$t('plc_monitoring.rebuildAllSignals')"
    width="520px"
    :close-on-click-modal="false"
    :close-on-press-escape="!batchSeedLoading"
    :show-close="!batchSeedLoading"
    @closed="stopBatchSeedPolling"
  >
    <div v-if="batchSeedProgress" style="padding: 8px 0;">
      <div style="margin-bottom: 12px; font-size: 14px; color: #606266;">
        {{ batchSeedProgress.stepName ?? batchSeedProgress.StepName ?? $t('plc_monitoring.initializing') }}
      </div>
      <el-progress
        :percentage="batchSeedProgress.percentage ?? batchSeedProgress.Percentage ?? 0"
        :status="
          (batchSeedProgress.status ?? batchSeedProgress.Status) === 'Completed' ? 'success'
          : (batchSeedProgress.status ?? batchSeedProgress.Status) === 'Failed' ? 'exception'
          : undefined
        "
        :stroke-width="20"
        :text-inside="true"
        style="margin-bottom: 12px;"
      />
      <div style="font-size: 13px; color: #909399;">
        {{ batchSeedProgress.message ?? batchSeedProgress.Message ?? '' }}
      </div>

      <el-alert
        v-if="(batchSeedProgress.status ?? batchSeedProgress.Status) === 'Failed'"
        type="error"
        :title="batchSeedProgress.errorMessage ?? batchSeedProgress.ErrorMessage ?? $t('plc_monitoring.batchSeedErrorTitle')"
        :closable="false"
        style="margin-top: 12px;"
      />

      <template v-if="(batchSeedProgress.status ?? batchSeedProgress.Status) === 'Completed'">
        <el-divider />
        <el-descriptions :column="2" border size="small">
          <el-descriptions-item :label="$t('plc_monitoring.stationCountLabel')">
            {{ (batchSeedProgress.summary ?? batchSeedProgress.Summary)?.stationCount
              ?? (batchSeedProgress.summary ?? batchSeedProgress.Summary)?.StationCount ?? '-' }}
          </el-descriptions-item>
          <el-descriptions-item :label="$t('plc_monitoring.newSignalsLabel')">
            {{ (batchSeedProgress.summary ?? batchSeedProgress.Summary)?.monitoringSignalCount
              ?? (batchSeedProgress.summary ?? batchSeedProgress.Summary)?.MonitoringSignalCount ?? '-' }}
          </el-descriptions-item>
        </el-descriptions>
      </template>

      <div v-if="batchSeedLoading" style="margin-top: 12px; font-size: 12px; color: #c0c4cc;">
        {{ $t('plc_monitoring.backgroundTaskHint') }}
      </div>
    </div>
  </el-dialog>
  </div>
</template>

<style scoped>
.monitoring-container {
  display: flex;
  flex-direction: column;
  gap: 20px;
}

.header-description {
  display: flex;
  flex-wrap: wrap;
  gap: 8px 18px;
}

.header-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
}

.project-picker-shell {
  display: flex;
  flex-direction: column;
  gap: 18px;
}

.project-picker-hero {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
  padding: 28px;
  border-radius: 24px;
  background:
    radial-gradient(circle at left top, rgba(14, 165, 233, 0.18), transparent 34%),
    radial-gradient(circle at right bottom, rgba(251, 191, 36, 0.18), transparent 28%),
    linear-gradient(135deg, #f8fafc 0%, #eff6ff 45%, #fff7ed 100%);
  border: 1px solid rgba(148, 163, 184, 0.2);
}

.project-picker-hero__eyebrow {
  font-size: 12px;
  letter-spacing: 0.2em;
  color: #0369a1;
  text-transform: uppercase;
}

.project-picker-hero h2 {
  margin: 12px 0 10px;
  font-size: 30px;
  line-height: 1.2;
  color: #0f172a;
}

.project-picker-hero p {
  margin: 0;
  max-width: 720px;
  line-height: 1.75;
  color: var(--el-text-color-secondary);
}

.project-picker-card {
  border-radius: 22px;
  border: 1px solid rgba(148, 163, 184, 0.18);
}

.recent-project-shell {
  margin-bottom: 16px;
  padding: 16px 18px;
  border-radius: 18px;
  background: linear-gradient(180deg, #fff 0%, #f8fafc 100%);
  border: 1px solid rgba(148, 163, 184, 0.16);
}

.recent-project-shell__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 12px;
}

.recent-project-shell__header span {
  font-size: 14px;
  font-weight: 600;
  color: #0f172a;
}

.recent-project-shell__header small {
  color: var(--el-text-color-secondary);
}

.recent-project-list {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
}

.recent-project-chip {
  display: inline-flex;
  flex-direction: column;
  gap: 4px;
  min-width: 180px;
  padding: 12px 14px;
  border: 1px solid rgba(14, 165, 233, 0.18);
  border-radius: 16px;
  background: linear-gradient(180deg, #eff6ff 0%, #fff 100%);
  text-align: left;
  transition: transform 0.18s ease, border-color 0.18s ease, box-shadow 0.18s ease;
}

.recent-project-chip:hover {
  transform: translateY(-1px);
  border-color: rgba(14, 165, 233, 0.44);
  box-shadow: 0 10px 24px rgba(14, 165, 233, 0.12);
}

.recent-project-chip strong {
  color: #0f172a;
}

.recent-project-chip span {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.project-picker-toolbar {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 12px;
  margin-bottom: 16px;
}

.project-picker-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 14px;
}

.project-picker-item {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 18px;
  border-radius: 20px;
  background: linear-gradient(180deg, #fff 0%, #f8fafc 100%);
  border: 1px solid rgba(148, 163, 184, 0.16);
  box-shadow: 0 14px 30px rgba(15, 23, 42, 0.05);
}

.project-picker-item__header,
.project-picker-item__meta,
.project-picker-item__actions {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.project-picker-item__header h3 {
  margin: 0;
  font-size: 16px;
  color: #0f172a;
}

.project-picker-item__header span,
.project-picker-item__meta span {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.project-picker-item p {
  margin: 0;
  line-height: 1.7;
  color: var(--el-text-color-regular);
}

.project-picker-pagination {
  display: flex;
  justify-content: flex-end;
  margin-top: 18px;
}

.compact-status-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 12px 20px;
  border-radius: 16px;
  background: linear-gradient(135deg, #0f172a 0%, #1e293b 50%, #15304d 100%);
  color: #ecfeff;
  margin-bottom: 14px;
}

.compact-status-bar__main {
  display: flex;
  align-items: center;
  gap: 24px;
  flex-wrap: wrap;
}

.compact-status-bar__station {
  display: flex;
  align-items: center;
  gap: 10px;
}

.compact-status-bar__station strong {
  font-size: 20px;
  font-weight: 700;
}

.compact-status-bar__metrics {
  display: flex;
  align-items: center;
  gap: 16px;
  font-size: 13px;
  color: rgba(236, 254, 255, 0.78);
}

.compact-status-bar__metrics b {
  color: #ecfeff;
}

.compact-status-bar .el-button {
  color: rgba(236, 254, 255, 0.85);
  flex-shrink: 0;
}

.compact-status-bar .el-button:hover {
  color: #ecfeff;
}

.detail-toggle-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 8px 14px;
  border-radius: 12px;
  background: rgba(148, 163, 184, 0.08);
  border: 1px dashed rgba(148, 163, 184, 0.22);
}

.detail-toggle-bar__hint {
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.hero-shell {
  display: grid;
  grid-template-columns: minmax(0, 1.6fr) minmax(360px, 0.9fr);
  gap: 18px;
}

.hero-panel {
  position: relative;
  overflow: hidden;
  border-radius: 24px;
  min-height: 290px;
  background:
    radial-gradient(circle at top left, rgba(251, 191, 36, 0.22), transparent 36%),
    radial-gradient(circle at right 18% bottom 22%, rgba(14, 165, 233, 0.22), transparent 28%),
    linear-gradient(140deg, #0f172a 0%, #1e293b 48%, #15304d 100%);
  box-shadow: 0 24px 60px rgba(15, 23, 42, 0.22);
}

.hero-panel__backdrop {
  position: absolute;
  inset: 0;
  background-image:
    linear-gradient(rgba(255, 255, 255, 0.05) 1px, transparent 1px),
    linear-gradient(90deg, rgba(255, 255, 255, 0.05) 1px, transparent 1px);
  background-size: 36px 36px;
  mask-image: linear-gradient(180deg, rgba(0, 0, 0, 0.78), transparent 100%);
}

.hero-panel__content {
  position: relative;
  display: flex;
  flex-direction: column;
  justify-content: space-between;
  height: 100%;
  padding: 28px;
  color: #ecfeff;
}

.hero-eyebrow {
  font-size: 12px;
  letter-spacing: 0.26em;
  text-transform: uppercase;
  color: rgba(236, 254, 255, 0.72);
}

.hero-title-row {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
  margin-top: 18px;
}

.hero-title-row h1 {
  margin: 0;
  font-size: 38px;
  line-height: 1.08;
  font-weight: 700;
}

.hero-title-row p {
  margin: 10px 0 0;
  max-width: 620px;
  font-size: 14px;
  line-height: 1.75;
  color: rgba(236, 254, 255, 0.8);
}

.hero-status-stack {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 10px;
}

.hero-meta-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 12px;
  margin-top: 26px;
}

.hero-meta-card {
  padding: 16px 18px;
  border: 1px solid rgba(255, 255, 255, 0.12);
  border-radius: 18px;
  background: rgba(255, 255, 255, 0.08);
  backdrop-filter: blur(12px);
}

.hero-meta-card__label {
  display: block;
  margin-bottom: 8px;
  font-size: 12px;
  color: rgba(236, 254, 255, 0.7);
}

.hero-meta-card strong,
.hero-meta-card small {
  display: block;
}

.hero-meta-card strong {
  font-size: 18px;
}

.hero-meta-card small {
  margin-top: 6px;
  color: rgba(236, 254, 255, 0.7);
}

.hero-control-card {
  border-radius: 24px;
  border: 1px solid rgba(15, 23, 42, 0.08);
  box-shadow: 0 18px 48px rgba(15, 23, 42, 0.08);
}

.section-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
}

.section-header span {
  font-size: 16px;
  font-weight: 600;
}

.section-subtitle {
  margin: 4px 0 0;
  color: var(--el-text-color-secondary);
  font-size: 13px;
  line-height: 1.6;
}

.hero-control-form :deep(.el-form-item) {
  margin-bottom: 14px;
}

.hero-control-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 0 12px;
}

.switch-row {
  display: flex;
  align-items: center;
  gap: 12px;
}

.switch-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 12px;
  margin-top: 4px;
}

.switch-tile {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 12px 14px;
  border-radius: 16px;
  background: linear-gradient(180deg, #f8fafc 0%, #eef4ff 100%);
  border: 1px solid rgba(148, 163, 184, 0.24);
}

.hero-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  margin-top: 14px;
}

.metric-strip {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 14px;
}

.metric-tile {
  padding: 18px 20px;
  border-radius: 20px;
  background:
    linear-gradient(180deg, rgba(255, 255, 255, 0.94), rgba(248, 250, 252, 0.96)),
    linear-gradient(140deg, rgba(14, 165, 233, 0.14), rgba(245, 158, 11, 0.08));
  border: 1px solid rgba(148, 163, 184, 0.18);
  box-shadow: 0 14px 30px rgba(15, 23, 42, 0.05);
}

.metric-tile__label,
.metric-tile__helper {
  display: block;
}

.metric-tile__label {
  font-size: 13px;
  color: var(--el-text-color-secondary);
}

.metric-tile__value {
  display: block;
  margin-top: 12px;
  font-size: 36px;
  line-height: 1;
  color: #0f172a;
}

.metric-tile__helper {
  margin-top: 10px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.dashboard-grid {
  display: grid;
  grid-template-columns: minmax(0, 1.7fr) minmax(320px, 0.92fr);
  gap: 18px;
}

.dashboard-main,
.dashboard-side {
  display: flex;
  flex-direction: column;
  gap: 18px;
}

.story-card,
.sample-card,
.leader-card,
.mirror-card,
.station-card,
.runtime-card,
.bundle-card,
.snapshot-card {
  border-radius: 22px;
  border: 1px solid rgba(148, 163, 184, 0.18);
}

.story-list {
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.story-item {
  display: grid;
  grid-template-columns: 10px minmax(160px, 0.35fr) minmax(0, 1fr);
  gap: 14px;
  padding: 16px;
  border-radius: 18px;
  background: #fcfdff;
  border: 1px solid rgba(148, 163, 184, 0.16);
}

.story-item--interactive {
  cursor: pointer;
  transition: transform 0.18s ease, border-color 0.18s ease, box-shadow 0.18s ease;
}

.story-item--interactive:hover,
.story-item--selected {
  transform: translateY(-1px);
  border-color: rgba(14, 165, 233, 0.4);
  box-shadow: 0 16px 30px rgba(14, 165, 233, 0.1);
}

.story-item__rail {
  border-radius: 999px;
  background: linear-gradient(180deg, #0ea5e9, #f59e0b);
}

.story-item--event .story-item__rail {
  background: linear-gradient(180deg, #ef4444, #f97316);
}

.story-item--bundle .story-item__rail {
  background: linear-gradient(180deg, #f59e0b, #eab308);
}

.story-item__timestamp {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.story-item__timestamp strong {
  font-size: 13px;
  line-height: 1.5;
}

.story-item__timestamp span {
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.story-item__content {
  min-width: 0;
}

.story-item__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.story-item__head span {
  font-weight: 600;
}

.story-item__content p {
  margin: 8px 0 10px;
  color: var(--el-text-color-regular);
  line-height: 1.7;
}

.story-item__tags {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.story-pill {
  display: inline-flex;
  align-items: center;
  padding: 6px 10px;
  border-radius: 999px;
  background: #eff6ff;
  color: #1d4ed8;
  font-size: 12px;
}

.story-item__pulse {
  margin-top: 12px;
  height: 6px;
  border-radius: 999px;
  background: rgba(148, 163, 184, 0.18);
  overflow: hidden;
}

.story-item__pulse span {
  display: block;
  height: 100%;
  border-radius: inherit;
  background: linear-gradient(90deg, #0ea5e9, #22c55e);
}

.split-grid {
  display: grid;
  grid-template-columns: minmax(0, 1.15fr) minmax(320px, 0.85fr);
  gap: 18px;
}

.sample-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px;
}

.persisted-sample-grid {
  grid-template-columns: 1fr;
}

.sample-tile {
  padding: 16px;
  border-radius: 18px;
  border: 1px solid rgba(148, 163, 184, 0.16);
  background: #fff;
}

.sample-tile--accent {
  background: linear-gradient(180deg, #eff6ff 0%, #f8fafc 100%);
}

.sample-tile--warning {
  background: linear-gradient(180deg, #fffbeb 0%, #fff7ed 100%);
}

.sample-tile__header h3 {
  margin: 0;
  font-size: 15px;
}

.sample-tile__header span {
  display: block;
  margin-top: 6px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.sample-tile p {
  margin: 12px 0;
  line-height: 1.7;
}

.sample-tile__cues {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.sample-tile__cues span {
  padding: 5px 9px;
  border-radius: 999px;
  background: rgba(15, 23, 42, 0.06);
  font-size: 12px;
}

.persisted-sample-tile {
  background: linear-gradient(180deg, #fff7ed 0%, #fff 100%);
}

.pattern-template-remark {
  margin: 4px 0 0;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.pattern-template-collapse {
  margin-top: 8px;
  border: none;
}

.pattern-template-collapse :deep(.el-collapse-item__header) {
  height: 28px;
  font-size: 12px;
  color: var(--el-color-primary);
  background: transparent;
  border: none;
}

.pattern-template-collapse :deep(.el-collapse-item__wrap) {
  border: none;
  background: transparent;
}

.pattern-template-collapse :deep(.el-collapse-item__content) {
  padding-bottom: 0;
}

.pattern-template-signature {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  max-height: 200px;
  overflow-y: auto;
}

.sample-meta-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px;
  margin: 12px 0;
}

.sample-meta-grid span,
.sample-meta-grid strong {
  display: block;
}

.sample-meta-grid span {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.sample-meta-grid strong {
  margin-top: 6px;
}

.insight-stack {
  display: flex;
  flex-direction: column;
  gap: 10px;
  margin-top: 16px;
}

.insight-row {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 12px 14px;
  border-radius: 16px;
  background: #0f172a;
  color: #f8fafc;
}

.insight-row__icon {
  margin-top: 2px;
  color: #fbbf24;
}

.leader-stack,
.hotspot-stack,
.bundle-stack,
.station-stack,
.history-stack,
.live-pulse-stack {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.leader-row,
.hotspot-row,
.bundle-row,
.history-row,
.live-pulse-row {
  padding: 14px 16px;
  border-radius: 16px;
  background: #fff;
  border: 1px solid rgba(148, 163, 184, 0.14);
}

.leader-row__main,
.leader-row__meta,
.bundle-row__head,
.hotspot-row,
.history-row__head,
.live-pulse-row__head,
.history-row__meta,
.live-pulse-row__meta {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.leader-row__main span,
.leader-row__meta span,
.bundle-row__head span,
.hotspot-row span,
.history-row__head span,
.history-row__meta span,
.live-pulse-row__head span,
.live-pulse-row__meta span,
.station-tile__meta span,
.station-tile__foot span {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.history-row p,
.live-pulse-row p {
  margin: 8px 0 10px;
  line-height: 1.7;
  color: var(--el-text-color-regular);
}

.live-pulse-row--success {
  background: linear-gradient(180deg, #ecfdf5 0%, #fff 100%);
}

.live-pulse-row--danger {
  background: linear-gradient(180deg, #fef2f2 0%, #fff 100%);
}

.live-pulse-row--info,
.live-pulse-row--warning {
  background: linear-gradient(180deg, #f8fafc 0%, #fff 100%);
}

.leader-row__bar {
  height: 6px;
  margin-top: 10px;
  border-radius: 999px;
  background: rgba(148, 163, 184, 0.18);
  overflow: hidden;
}

.leader-row__bar span {
  display: block;
  height: 100%;
  border-radius: inherit;
  background: linear-gradient(90deg, #1d4ed8, #0ea5e9);
}

.bundle-row p {
  margin: 8px 0 10px;
  color: var(--el-text-color-secondary);
}

.bundle-row__signals {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.bundle-row__signals span {
  padding: 5px 8px;
  border-radius: 999px;
  background: #eff6ff;
  color: #1d4ed8;
  font-size: 12px;
}

.mirror-toolbar {
  display: flex;
  justify-content: flex-end;
  margin-bottom: 14px;
}

.mirror-toolbar__search {
  max-width: 360px;
}

.value-shift,
.change-time-cell {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.value-shift {
  flex-direction: row;
  align-items: center;
  gap: 8px;
}

.change-time-cell span {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.station-tile {
  width: 100%;
  padding: 14px 16px;
  border-radius: 18px;
  border: 1px solid rgba(148, 163, 184, 0.18);
  background: linear-gradient(180deg, #fff 0%, #f8fafc 100%);
  text-align: left;
  transition: transform 0.18s ease, border-color 0.18s ease, box-shadow 0.18s ease;
}

.station-tile:hover,
.station-tile.is-active {
  transform: translateY(-1px);
  border-color: rgba(14, 165, 233, 0.42);
  box-shadow: 0 14px 28px rgba(14, 165, 233, 0.12);
}

.station-tile__head,
.station-tile__meta,
.station-tile__foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.station-tile__meta,
.station-tile__foot {
  margin-top: 10px;
}

.run-result {
  margin-top: 18px;
  padding: 14px;
  border-radius: 16px;
  background: linear-gradient(180deg, #eff6ff 0%, #f8fafc 100%);
}

.acceptance-panel {
  margin-top: 18px;
  padding: 16px;
  border-radius: 18px;
  border: 1px solid rgba(16, 185, 129, 0.18);
  background: linear-gradient(180deg, rgba(236, 253, 245, 0.92) 0%, rgba(255, 255, 255, 0.98) 100%);
}

.acceptance-panel__head,
.acceptance-panel__tags {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.acceptance-panel__tags {
  flex-wrap: wrap;
  justify-content: flex-end;
}

.acceptance-panel__subtitle {
  margin: 6px 0 0;
}

.acceptance-step-list {
  display: flex;
  flex-direction: column;
  gap: 10px;
  margin-top: 14px;
}

.acceptance-step-row {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  padding: 12px 14px;
  border-radius: 16px;
  border: 1px solid rgba(148, 163, 184, 0.16);
  background: #fff;
}

.acceptance-step-row--success {
  border-color: rgba(34, 197, 94, 0.24);
  background: linear-gradient(180deg, rgba(240, 253, 244, 0.92) 0%, #fff 100%);
}

.acceptance-step-row--warning,
.acceptance-step-row--running {
  border-color: rgba(245, 158, 11, 0.24);
  background: linear-gradient(180deg, rgba(255, 251, 235, 0.92) 0%, #fff 100%);
}

.acceptance-step-row--error {
  border-color: rgba(239, 68, 68, 0.24);
  background: linear-gradient(180deg, rgba(254, 242, 242, 0.92) 0%, #fff 100%);
}

.acceptance-step-row__main {
  min-width: 0;
}

.acceptance-step-row__main strong,
.acceptance-step-row__main span {
  display: block;
}

.acceptance-step-row__main span {
  margin-top: 6px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
  line-height: 1.6;
}

.acceptance-panel__summary {
  margin-top: 14px;
  padding: 12px 14px;
  border-radius: 14px;
  background: rgba(255, 255, 255, 0.9);
  color: var(--el-text-color-regular);
  line-height: 1.7;
  border: 1px solid rgba(148, 163, 184, 0.14);
}

.diagnosis-board-panel {
  border-color: rgba(245, 158, 11, 0.18);
  background: linear-gradient(180deg, rgba(255, 251, 235, 0.92) 0%, rgba(255, 255, 255, 0.98) 100%);
}

.live-read-panel {
  border-color: rgba(59, 130, 246, 0.18);
  background: linear-gradient(180deg, rgba(239, 246, 255, 0.92) 0%, rgba(255, 255, 255, 0.98) 100%);
}

.live-read-warnings {
  margin-bottom: 10px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.live-read-table {
  border-radius: 10px;
  overflow: hidden;
}

.live-read-value--true {
  color: #10b981;
  font-weight: 600;
}

.live-read-value--false {
  color: #ef4444;
  font-weight: 600;
}

.diagnosis-board-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 12px;
  margin-top: 14px;
}

.diagnosis-board-card {
  padding: 14px;
  border-radius: 18px;
  border: 1px solid rgba(148, 163, 184, 0.16);
  background: #fff;
}

.diagnosis-board-card--success {
  border-color: rgba(34, 197, 94, 0.24);
  background: linear-gradient(180deg, rgba(240, 253, 244, 0.92) 0%, #fff 100%);
}

.diagnosis-board-card--warning {
  border-color: rgba(245, 158, 11, 0.24);
  background: linear-gradient(180deg, rgba(255, 251, 235, 0.92) 0%, #fff 100%);
}

.diagnosis-board-card--danger {
  border-color: rgba(239, 68, 68, 0.24);
  background: linear-gradient(180deg, rgba(254, 242, 242, 0.92) 0%, #fff 100%);
}

.diagnosis-board-card__head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.diagnosis-board-card__head strong,
.diagnosis-board-card__head p {
  display: block;
}

.diagnosis-board-card__head p {
  margin: 6px 0 0;
  color: var(--el-text-color-secondary);
  font-size: 12px;
  line-height: 1.6;
}

.diagnosis-board-card__meta {
  margin-top: 12px;
}

.diagnosis-board-card__meta span,
.diagnosis-board-card__meta strong {
  display: block;
}

.diagnosis-board-card__meta span {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.diagnosis-board-card__meta strong {
  margin-top: 6px;
  word-break: break-word;
}

.diagnosis-board-card__events {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-top: 14px;
}

.diagnosis-board-card__event-pill {
  padding: 6px 10px;
  border-radius: 999px;
  border: 1px solid rgba(14, 165, 233, 0.18);
  background: #eff6ff;
  color: #1d4ed8;
  font-size: 12px;
  transition: transform 0.18s ease, box-shadow 0.18s ease;
}

.diagnosis-board-card__event-pill:hover {
  transform: translateY(-1px);
  box-shadow: 0 10px 18px rgba(14, 165, 233, 0.12);
}

.diagnosis-panel {
  margin-top: 18px;
  padding: 16px;
  border-radius: 18px;
  border: 1px solid rgba(14, 165, 233, 0.18);
  background: linear-gradient(180deg, rgba(255, 255, 255, 0.92) 0%, rgba(239, 246, 255, 0.96) 100%);
}

.event-selector-stack {
  display: flex;
  flex-direction: column;
  gap: 10px;
  margin-top: 18px;
}

.event-selector-row {
  width: 100%;
  padding: 12px 14px;
  border-radius: 16px;
  border: 1px solid rgba(148, 163, 184, 0.16);
  background: #fff;
  text-align: left;
  transition: transform 0.18s ease, border-color 0.18s ease, box-shadow 0.18s ease;
}

.event-selector-row:hover,
.event-selector-row.is-active {
  transform: translateY(-1px);
  border-color: rgba(14, 165, 233, 0.42);
  box-shadow: 0 14px 28px rgba(14, 165, 233, 0.12);
}

.event-selector-row__head,
.event-selector-row__meta {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.event-selector-row__meta {
  margin-top: 8px;
}

.event-selector-row__meta span {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.diagnosis-panel__head,
.diagnosis-panel__tags {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.diagnosis-panel__tags {
  flex-wrap: wrap;
  justify-content: flex-end;
}

.diagnosis-panel__subtitle {
  margin: 6px 0 0;
}

.diagnosis-panel__alert {
  margin-top: 14px;
}

.diagnosis-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px;
  margin-top: 14px;
}

.diagnosis-grid span,
.diagnosis-grid strong {
  display: block;
}

.diagnosis-grid span {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.diagnosis-grid strong {
  margin-top: 6px;
  word-break: break-word;
}

.diagnosis-pill-list {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-top: 14px;
}

.diagnosis-note-list {
  display: flex;
  flex-direction: column;
  gap: 10px;
  margin-top: 14px;
}

.diagnosis-note-row {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 10px 12px;
  border-radius: 14px;
  background: rgba(248, 250, 252, 0.92);
  border: 1px solid rgba(148, 163, 184, 0.16);
}

.diagnosis-note-row__icon {
  color: #f59e0b;
  margin-top: 2px;
}

.run-result__title {
  font-weight: 600;
}

.run-result__grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px;
  margin-top: 12px;
}

.run-result__grid span,
.run-result__grid strong {
  display: block;
}

.run-result__grid span {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.run-result__grid strong {
  margin-top: 6px;
}

.snapshot-collapse {
  margin-top: 16px;
}

.raw-json {
  max-height: 360px;
  overflow: auto;
  padding: 14px;
  border-radius: 16px;
  background: #0f172a;
  color: #e2e8f0;
  white-space: pre-wrap;
  word-break: break-word;
  font-family: Consolas, 'Courier New', monospace;
  font-size: 12px;
  line-height: 1.55;
}

@media (max-width: 1440px) {
  .project-picker-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .metric-strip {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .split-grid {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 1200px) {
  .hero-shell,
  .dashboard-grid {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 900px) {
  .project-picker-toolbar,
  .hero-control-grid,
  .switch-grid,
  .diagnosis-board-grid,
  .sample-grid,
  .sample-meta-grid,
  .diagnosis-grid,
  .run-result__grid,
  .metric-strip {
    grid-template-columns: 1fr;
  }

  .project-picker-grid {
    grid-template-columns: 1fr;
  }

  .story-item {
    grid-template-columns: 10px 1fr;
  }

  .story-item__timestamp {
    grid-column: 2;
  }

  .story-item__content {
    grid-column: 2;
  }

  .hero-title-row,
  .hero-status-stack,
  .project-picker-hero,
  .recent-project-shell__header,
  .project-picker-item__header,
  .project-picker-item__meta,
  .project-picker-item__actions,
  .section-header,
  .story-item__head,
  .leader-row__main,
  .leader-row__meta,
  .bundle-row__head,
  .hotspot-row,
  .history-row__head,
  .history-row__meta,
  .live-pulse-row__head,
  .live-pulse-row__meta,
  .station-tile__head,
  .station-tile__meta,
  .station-tile__foot,
  .acceptance-panel__head,
  .acceptance-panel__tags,
  .acceptance-step-row,
  .event-selector-row__head,
  .event-selector-row__meta,
  .diagnosis-panel__head,
  .diagnosis-panel__tags,
  .header-actions {
    flex-direction: column;
    align-items: flex-start;
  }
}
</style>