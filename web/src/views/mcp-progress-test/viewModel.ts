import type { McpDiagnosisClientErrorCode, McpDiagnosisToolName } from '../../utils/mcp/mcpDiagnosisClient'
import type { McpJsonRpcEnvelope, McpStreamEnvelope } from '../../utils/mcp/mcpStreamParser'
import { McpDiagnosisClientError } from '../../utils/mcp/mcpDiagnosisClient'
import pack from './viewModel.lang'

// —— 非 vue 模块的 i18n 桥（组件外无 useI18n 上下文）——
// 优先动态接入全局 i18n 实例（@/locales 具名导出 i18n，legacy:false 下 i18n.global.t 可用），
// 并把本模块语言包幂等合并进全局消息（页面 setup 的 mergeLocaleMessage 合并同一份数据，互不冲突）；
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
  let text = table.mcpProgressTest[key] ?? pack['zh-cn'].mcpProgressTest[key] ?? key
  if (params) {
    text = text.replace(/\{(\w+)\}/g, (match, name: string) => (
      params[name] === undefined ? match : String(params[name])
    ))
  }
  return text
}

function t(key: string, params?: I18nBridgeParams): string {
  if (globalTranslate) {
    return globalTranslate(`mcpProgressTest.${key}`, params)
  }
  return localTranslate(key, params)
}

export type McpPageStatus = 'idle' | 'running' | 'success' | 'business_error' | 'error' | 'cancelled'
export type McpProgressTab = 'stream' | 'timeline' | 'raw' | 'result'

export const DEFAULT_MCP_PROGRESS_TAB: McpProgressTab = 'stream'
// 模块级常量在动态 i18n 桥接通前求值：语言取自本地偏好探测，切换语言后需刷新页面才会更新
export const RESULT_STREAM_EMPTY_TEXT = t('resultStreamEmptyText')
// 服务端协议前缀（notifications/progress 消息由后端固定发中文「结果片段：」），不是用户可见文案，禁止本地化
const RESULT_CHUNK_PREFIX = '结果片段：'

export interface McpResultChunk {
  sequence: number
  content: string
  rawContent?: string
  progressTokenSuffix: string
  eventType?: string
  eventLabel?: string
  stationCode?: string
  llmReport?: McpLineDiagnosisLlmReport
}

export interface McpSecondaryCause {
  stationCode?: string
  description?: string
}

export interface McpLineDiagnosisLlmReport {
  success?: boolean
  rootCauseStation?: string
  rootCauseDescription?: string
  confidence?: number
  impactChain?: string
  secondaryCauses: McpSecondaryCause[]
  suggestedActions: string[]
  reasoning?: string
}

export interface McpTimelineEntry {
  sequence: number
  progress: number
  total: number | null
  percentage: number | null
  message: string
  progressTokenSuffix: string
  nestedIo: boolean
}

export interface McpBusinessFailure {
  message: string
}

export interface McpProgressPageState {
  toolName: McpDiagnosisToolName
  status: McpPageStatus
  hasProgress: boolean
  progress: number | null
  total: number | null
  progressTokenSuffix: string
  acceptingResultChunks: boolean
  resultChunks: McpResultChunk[]
  timeline: McpTimelineEntry[]
  rawBlocks: string[]
  finalResult?: unknown
  businessFailure?: McpBusinessFailure
}

export interface CreateMcpProgressStateOptions {
  toolName: McpDiagnosisToolName
  progressToken: string
  /** Accepted only so callers can prove credentials are deliberately not retained. */
  jwt?: string
}

export interface McpPageErrorPresentation {
  code: McpDiagnosisClientErrorCode | 'invalid_arguments' | 'unknown'
  message: string
}

interface ProgressParams {
  progress: number
  total: number | null
  message: string
  progressTokenSuffix: string
}

export interface AppendMcpEnvelopeOptions {
  expectedProgressToken?: string
}

const CLIENT_ERROR_MESSAGE_KEYS: Record<McpDiagnosisClientErrorCode, string> = {
  missing_token: 'errMissingToken',
  unauthorized: 'errUnauthorized',
  http_error: 'errHttpError',
  json_rpc_error: 'errJsonRpcError',
  missing_body: 'errMissingBody',
  invalid_response: 'errInvalidResponse',
  unsupported_content_type: 'errUnsupportedContentType',
  callback_error: 'errCallbackError',
  uuid_unavailable: 'errUuidUnavailable',
  aborted: 'errAborted',
  network_error: 'errNetworkError',
  missing_final_result: 'errMissingFinalResult',
}

function tokenSuffix(value: unknown) {
  return typeof value === 'string' ? value.slice(-6) : ''
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

function finiteNumber(value: unknown) {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

function readProgressParams(params: unknown): ProgressParams | undefined {
  if (!isRecord(params)) {
    return undefined
  }
  const progress = finiteNumber(params.progress)
  if (progress === undefined) {
    return undefined
  }
  const candidateTotal = finiteNumber(params.total)
  const total = candidateTotal !== undefined && candidateTotal > 0 ? candidateTotal : null
  return {
    progress,
    total,
    message: typeof params.message === 'string' ? params.message : '',
    progressTokenSuffix: tokenSuffix(params.progressToken),
  }
}

interface LineProgressEvent {
  value: Record<string, unknown>
  type: string
  stationCode?: string
}

function readLineProgressEvent(message: string): LineProgressEvent | undefined {
  let candidate: unknown
  try {
    candidate = JSON.parse(message)
  }
  catch {
    return undefined
  }
  if (!isRecord(candidate) || typeof candidate.type !== 'string' || !candidate.type.trim()) {
    return undefined
  }
  const stationCode = typeof candidate.stationCode === 'string' && candidate.stationCode.trim()
    ? candidate.stationCode
    : undefined
  return {
    value: candidate,
    type: candidate.type,
    stationCode,
  }
}

function readProperty(value: Record<string, unknown>, camel: string, pascal: string) {
  return value[camel] ?? value[pascal]
}

function readStringProperty(value: Record<string, unknown>, camel: string, pascal: string) {
  const candidate = readProperty(value, camel, pascal)
  return typeof candidate === 'string' && candidate.trim() ? candidate : undefined
}

function readNumberProperty(value: Record<string, unknown>, camel: string, pascal: string) {
  return finiteNumber(readProperty(value, camel, pascal))
}

function readLlmReport(value: unknown): McpLineDiagnosisLlmReport | undefined {
  if (!isRecord(value)) {
    return undefined
  }

  const secondaryCausesValue = readProperty(value, 'secondaryCauses', 'SecondaryCauses')
  const secondaryCauses = Array.isArray(secondaryCausesValue)
    ? secondaryCausesValue
        .filter(isRecord)
        .map(item => ({
          stationCode: readStringProperty(item, 'stationCode', 'StationCode'),
          description: readStringProperty(item, 'description', 'Description'),
        }))
    : []
  const suggestedActionsValue = readProperty(value, 'suggestedActions', 'SuggestedActions')
  const suggestedActions = Array.isArray(suggestedActionsValue)
    ? suggestedActionsValue.filter((item): item is string => typeof item === 'string' && Boolean(item.trim()))
    : []
  const successValue = readProperty(value, 'success', 'Success')

  return {
    success: typeof successValue === 'boolean' ? successValue : undefined,
    rootCauseStation: readStringProperty(value, 'rootCauseStation', 'RootCauseStation'),
    rootCauseDescription: readStringProperty(value, 'rootCauseDescription', 'RootCauseDescription'),
    confidence: readNumberProperty(value, 'confidence', 'Confidence'),
    impactChain: readStringProperty(value, 'impactChain', 'ImpactChain'),
    secondaryCauses,
    suggestedActions,
    reasoning: readStringProperty(value, 'reasoning', 'Reasoning'),
  }
}

const LINE_EVENT_LABEL_KEYS: Record<string, string> = {
  start: 'eventLabelStart',
  station_start: 'eventLabelStationStart',
  station_done: 'eventLabelStationDone',
  constraint_start: 'eventLabelConstraintStart',
  constraint_progress: 'eventLabelConstraintProgress',
  constraint_done: 'eventLabelConstraintDone',
  constraint_error: 'eventLabelConstraintError',
  llm_start: 'eventLabelLlmStart',
  agent_step: 'eventLabelAgentStep',
  llm_done: 'eventLabelLlmDone',
  complete: 'eventLabelComplete',
  error: 'eventLabelError',
}

function formatCount(value: Record<string, unknown>, camel: string, pascal: string) {
  const count = readNumberProperty(value, camel, pascal)
  return count === undefined ? undefined : Math.max(0, Math.trunc(count))
}

function formatConfidence(value: number | undefined) {
  if (value === undefined) {
    return undefined
  }
  const normalized = value <= 1 ? value * 100 : value
  return `${Math.min(100, Math.max(0, normalized)).toFixed(0)}%`
}

function lineEventPresentation(lineEvent: LineProgressEvent) {
  const value = lineEvent.value
  const message = readStringProperty(value, 'message', 'Message')
  const completed = formatCount(value, 'completedCount', 'CompletedCount')
  const total = formatCount(value, 'totalCount', 'TotalCount')
  const station = readProperty(value, 'station', 'Station')
  const stationRecord = isRecord(station) ? station : undefined
  const payloadValue = readProperty(value, 'payload', 'Payload')
  const payload = isRecord(payloadValue) ? payloadValue : undefined
  let llmReportValue = readProperty(value, 'llmReport', 'LlmReport')
  if (llmReportValue === undefined && lineEvent.type === 'complete' && payload) {
    llmReportValue = readProperty(payload, 'llmReport', 'LlmReport')
  }
  const llmReport = readLlmReport(llmReportValue)
  const stationCode = lineEvent.stationCode ?? (stationRecord
    ? readStringProperty(stationRecord, 'stationCode', 'StationCode')
    : undefined)
  const progressCount = completed !== undefined && total !== undefined
    ? t('progressCountSuffix', { completed, total })
    : ''

  switch (lineEvent.type) {
    case 'start': {
      const stationCodes = readProperty(value, 'stationCodes', 'StationCodes')
      const count = Array.isArray(stationCodes) ? stationCodes.length : total
      return {
        label: t(LINE_EVENT_LABEL_KEYS.start),
        content: count === undefined
          ? t('lineStartContent')
          : t('lineStartContentWithCount', { count }),
        llmReport,
      }
    }
    case 'station_start':
      return {
        label: t(LINE_EVENT_LABEL_KEYS.station_start),
        content: stationCode
          ? t('stationStartContentWithCode', { stationCode })
          : t('stationStartContent'),
        llmReport,
      }
    case 'station_done': {
      const status = stationRecord
        ? readStringProperty(stationRecord, 'overallStatus', 'OverallStatus')
        : undefined
      const strategy = stationRecord
        ? readStringProperty(stationRecord, 'diagnosisStrategy', 'DiagnosisStrategy')
        : undefined
      const summary = [status ? t('statusWithLabel', { status }) : '', strategy ? t('strategyWithLabel', { strategy }) : '']
        .filter(Boolean)
        .join(t('summaryJoiner'))
      return {
        label: t(LINE_EVENT_LABEL_KEYS.station_done),
        content: t('stationDoneContent', {
          stationCode: stationCode ?? '—',
          progress: progressCount,
          summary: summary ? t('summaryPrefix') + summary : '',
        }),
        llmReport,
      }
    }
    case 'constraint_start':
      return { label: t(LINE_EVENT_LABEL_KEYS.constraint_start), content: t('constraintStartContent'), llmReport }
    case 'constraint_progress':
      return { label: t(LINE_EVENT_LABEL_KEYS.constraint_progress), content: message ?? t('constraintAnalyzing'), llmReport }
    case 'constraint_done':
      return { label: t(LINE_EVENT_LABEL_KEYS.constraint_done), content: message ?? t(LINE_EVENT_LABEL_KEYS.constraint_done), llmReport }
    case 'constraint_error':
      return { label: t(LINE_EVENT_LABEL_KEYS.constraint_error), content: message ?? t('constraintIncomplete'), llmReport }
    case 'llm_start':
      return { label: t(LINE_EVENT_LABEL_KEYS.llm_start), content: t('llmStartContent'), llmReport }
    case 'agent_step': {
      const fragmentValue = readProperty(value, 'resultFragment', 'ResultFragment')
      const fragment = isRecord(fragmentValue) ? fragmentValue : undefined
      const iteration = fragment ? formatCount(fragment, 'iteration', 'Iteration') : undefined
      const phase = fragment ? readStringProperty(fragment, 'phase', 'Phase') : undefined
      return {
        label: t(LINE_EVENT_LABEL_KEYS.agent_step),
        content: phase === undefined
          ? (iteration === undefined
              ? t('agentStepAnalyzing')
              : t('agentStepRoundAnalyzing', { iteration }))
          : (iteration === undefined
              ? t('agentStepWithPhase', { phase })
              : t('agentStepRoundWithPhase', { iteration, phase })),
        llmReport,
      }
    }
    case 'llm_done':
      return {
        label: t(LINE_EVENT_LABEL_KEYS.llm_done),
        content: llmReport?.rootCauseStation
          ? t('llmDoneWithRootCause', {
              station: llmReport.rootCauseStation,
              confidence: llmReport.confidence === undefined
                ? ''
                : t('confidenceSuffix', { value: formatConfidence(llmReport.confidence) ?? '' }),
            })
          : t('llmDoneFallback'),
        llmReport,
      }
    case 'complete': {
      const totalStations = payload ? formatCount(payload, 'totalStations', 'TotalStations') : total
      const abnormalStations = payload ? formatCount(payload, 'abnormalStations', 'AbnormalStations') : undefined
      return {
        label: t(LINE_EVENT_LABEL_KEYS.complete),
        content: t('completeContent', {
          stations: totalStations === undefined ? '' : t('completeStationsSuffix', { count: totalStations }),
          abnormal: abnormalStations === undefined ? '' : t('completeAbnormalSuffix', { count: abnormalStations }),
        }),
        llmReport,
      }
    }
    case 'error':
      return { label: t(LINE_EVENT_LABEL_KEYS.error), content: message ?? t(LINE_EVENT_LABEL_KEYS.error), llmReport }
    default:
      return {
        label: LINE_EVENT_LABEL_KEYS[lineEvent.type] ? t(LINE_EVENT_LABEL_KEYS[lineEvent.type]) : lineEvent.type,
        content: message ?? t('unknownEventContent', { type: lineEvent.type }),
        llmReport,
      }
  }
}

function businessCandidate(result: unknown) {
  if (!isRecord(result)) {
    return undefined
  }
  const structured = readProperty(result, 'structuredContent', 'StructuredContent')
  return isRecord(structured) ? structured : result
}

export function findBusinessFailure(result: unknown): McpBusinessFailure | undefined {
  const candidate = businessCandidate(result)
  if (!candidate || readProperty(candidate, 'isSuccess', 'IsSuccess') !== false) {
    return undefined
  }
  const message = readProperty(candidate, 'errorMessage', 'ErrorMessage')
    ?? readProperty(candidate, 'exceptionMessage', 'ExceptionMessage')
  return {
    message: typeof message === 'string' && message.trim()
      ? message
      : t('businessFailureFallback'),
  }
}

export function createMcpProgressState(options: CreateMcpProgressStateOptions): McpProgressPageState {
  void options.jwt
  return {
    toolName: options.toolName,
    status: 'idle',
    hasProgress: false,
    progress: null,
    total: null,
    progressTokenSuffix: tokenSuffix(options.progressToken),
    acceptingResultChunks: false,
    resultChunks: [],
    timeline: [],
    rawBlocks: [],
    finalResult: undefined,
    businessFailure: undefined,
  }
}

export function appendMcpEnvelope(
  state: McpProgressPageState,
  item: McpStreamEnvelope,
  options: AppendMcpEnvelopeOptions = {},
) {
  state.rawBlocks.push(item.rawBlock)

  if (item.envelope.method !== 'notifications/progress') {
    return
  }
  if (options.expectedProgressToken !== undefined) {
    const params = isRecord(item.envelope.params) ? item.envelope.params : undefined
    if (params?.progressToken !== options.expectedProgressToken) {
      return
    }
  }
  const progress = readProgressParams(item.envelope.params)
  if (!progress) {
    return
  }
  const lineEvent = state.toolName === 'diagnose_line_stoppage'
    ? readLineProgressEvent(progress.message)
    : undefined
  const presentation = lineEvent ? lineEventPresentation(lineEvent) : undefined

  const percentage = progress.total === null
    ? null
    : Math.min(100, Math.max(0, (progress.progress / progress.total) * 100))
  state.hasProgress = true
  state.progress = progress.progress
  state.total = progress.total
  if (progress.progressTokenSuffix) {
    state.progressTokenSuffix = progress.progressTokenSuffix
  }
  state.timeline.push({
    sequence: state.timeline.length + 1,
    progress: progress.progress,
    total: progress.total,
    percentage,
    message: presentation?.content ?? progress.message,
    progressTokenSuffix: state.progressTokenSuffix,
    nestedIo: state.toolName === 'diagnose_line_stoppage'
      && (lineEvent?.type.startsWith('constraint_') === true
        || (percentage !== null && percentage >= 70 && percentage <= 82)),
  })

  if (state.acceptingResultChunks && lineEvent) {
    const llmReport = presentation?.llmReport
      && !state.resultChunks.some(chunk => chunk.llmReport)
      ? presentation.llmReport
      : undefined
    state.resultChunks.push({
      sequence: state.resultChunks.length + 1,
      content: presentation?.content ?? progress.message,
      rawContent: JSON.stringify(lineEvent.value, null, 2),
      progressTokenSuffix: state.progressTokenSuffix,
      eventType: lineEvent.type,
      eventLabel: presentation?.label,
      stationCode: lineEvent.stationCode,
      llmReport,
    })
  }
  else if (state.acceptingResultChunks && progress.message.startsWith(RESULT_CHUNK_PREFIX)) {
    const content = progress.message.slice(RESULT_CHUNK_PREFIX.length)
    if (content.length > 0) {
      state.resultChunks.push({
        sequence: state.resultChunks.length + 1,
        content,
        progressTokenSuffix: state.progressTokenSuffix,
      })
    }
  }
}

export function beginResultStream(state: McpProgressPageState) {
  state.resultChunks = []
  state.acceptingResultChunks = true
}

export function stopResultStream(state: McpProgressPageState) {
  state.acceptingResultChunks = false
}

export function applyFinalEnvelope(state: McpProgressPageState, envelope: McpJsonRpcEnvelope) {
  if (!Object.hasOwn(envelope, 'result')) {
    return false
  }
  state.finalResult = envelope.result
  state.businessFailure = findBusinessFailure(envelope.result)
  return true
}

export function createProgressDisplay(state: Pick<McpProgressPageState, 'hasProgress' | 'progress' | 'total'>) {
  if (!state.hasProgress || state.progress === null) {
    return {
      valueLabel: '—',
      percentage: null,
    }
  }
  if (state.total === null || state.total <= 0) {
    return {
      valueLabel: String(state.progress),
      percentage: null,
    }
  }
  return {
    valueLabel: `${state.progress} / ${state.total}`,
    percentage: Math.min(100, Math.max(0, (state.progress / state.total) * 100)),
  }
}

export function parseArgumentsJson(source: string):
  | { ok: true, value: Record<string, unknown> }
  | { ok: false, message: string } {
  let value: unknown
  try {
    value = JSON.parse(source)
  }
  catch {
    return { ok: false, message: t('argsJsonInvalid') }
  }
  if (!isRecord(value)) {
    return { ok: false, message: t('argsJsonNotObject') }
  }
  return { ok: true, value }
}

export function presentMcpPageError(error: unknown): McpPageErrorPresentation {
  if (error instanceof McpDiagnosisClientError) {
    return {
      code: error.code,
      message: t(CLIENT_ERROR_MESSAGE_KEYS[error.code]),
    }
  }
  return {
    code: 'unknown',
    message: t('unknownMcpError'),
  }
}

export function buildMcpEndpoint(apiBaseUrl: string, useDevProxy: boolean) {
  const base = useDevProxy ? '/proxy' : apiBaseUrl.trim().replace(/\/$/, '')
  return `${base}/mcp`
}
