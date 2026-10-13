import type { TiaGraphSelection } from '../components/tiaGraphSelection'
import type { LineUpImageBom, LineUpImageItem, LineUpImageMarker, LineUpImageWorkspace } from '@/api/modules/LineUp/imageMarks'
import type {
  LineUpConditionRuntimeValue,
  LineUpGatewayStatus,
  LineUpMonitorDefinition,
  LineUpMonitorDelta,
  LineUpMonitorEvent,
  LineUpMonitorSnapshot,
  LineUpRuntimeException,
  LineUpRuntimeQuality,
  LineUpStepStatus,
  LineUpTransitionStatus,
} from '@/api/modules/LineUp/monitor'
import { conditionsForSelection } from '../components/tiaGraphSelection'
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
  let text = table.lineupMonitor_form[key] ?? pack['zh-cn'].lineupMonitor_form[key] ?? key
  if (params) {
    text = text.replace(/\{(\w+)\}/g, (match, name: string) => (
      params[name] === undefined ? match : String(params[name])
    ))
  }
  return text
}

function t(key: string, params?: I18nBridgeParams): string {
  if (globalTranslate) {
    return globalTranslate(`lineupMonitor_form.${key}`, params)
  }
  return localTranslate(key, params)
}

export type LineUpRuntimeTone = 'idle' | 'active' | 'waiting' | 'completed' | 'warning' | 'timeout' | 'error' | 'unknown'
export type LineUpConnectionTone = 'online' | 'degraded' | 'offline' | 'connecting'

export interface LineUpGraphRuntimeNodeProjection {
  sourceKey: string
  tone: LineUpRuntimeTone
  label: string
  quality: string
  activeDurationMs: number | null
  activeDurationText: string
}

export interface LineUpGraphRuntimeEdgeProjection {
  sourceKey: string
  traversed: boolean
  tone: LineUpRuntimeTone
}

export interface LineUpGraphRuntimeProjection {
  nodes: Record<string, LineUpGraphRuntimeNodeProjection>
  edges: Record<string, LineUpGraphRuntimeEdgeProjection>
  connectionTone: LineUpConnectionTone
}

export type DeltaApplyResult
  = | { kind: 'applied', snapshot: LineUpMonitorSnapshot }
    | { kind: 'ignored' | 'gap' | 'definition-mismatch', snapshot: null }

export interface MonitorAvailabilityInput {
  hasSelection: boolean
  hasImport: boolean
  hasBinding?: boolean
  bindingStatus?: string
  runtimeBindingReady?: boolean
  monitoringEnabled?: boolean
  connectionState?: string
  gatewayStatus?: string
  snapshotRevision?: string
  safeErrorCode?: string
}

export interface RuntimeBindingActionInput {
  bindingStatus?: string
  runtimeBindingReady?: boolean
  monitoringEnabled?: boolean
}

export interface LineUpMonitorPlcReference {
  code: string
  abnormal: boolean
}

export interface LineUpMonitorBomProjection {
  boms: LineUpImageBom[]
  images: LineUpImageItem[]
  markers: LineUpImageMarker[]
  abnormalBomIds: string[]
}

export interface LineUpMonitorBomEmptyInput {
  loaded: boolean
  stationBomCount: number
  referenceCount: number
  matchedBomCount: number
  markerCount: number
}

const plcReferencePattern = /^(?:%[IQM][\w.]+|[A-Z_]\w*(?:\[\d+\])?(?:\.[A-Z_]\w*(?:\[\d+\])?)+)$/i

function canonicalPlcReference(value: string) {
  const candidate = value.trim().replaceAll('"', '').replaceAll(' ', '')
  return plcReferencePattern.test(candidate) ? candidate : ''
}

function normalizedPlcReference(value: string | null) {
  return canonicalPlcReference(value ?? '').toLowerCase()
}

export function plcReferencesForMonitorSelection(
  selection: TiaGraphSelection | null,
  conditions: LineUpConditionRuntimeValue[],
): LineUpMonitorPlcReference[] {
  if (!selection) {
    return []
  }
  const abnormalPartKeys = new Set(conditions
    .filter(item => item.quality !== 'Good')
    .map(item => item.partSourceKey))
  const references = new Map<string, LineUpMonitorPlcReference>()

  const add = (value: string, abnormal: boolean) => {
    const code = canonicalPlcReference(value)
    const normalized = code.toLowerCase()
    if (!normalized) {
      return
    }
    const current = references.get(normalized)
    references.set(normalized, {
      code: current?.code ?? code,
      abnormal: abnormal || current?.abnormal === true,
    })
  }

  for (const network of selection.networks) {
    for (const part of network.parts) {
      if (part.symbolPath.length >= 2) {
        add(part.symbolPath.join('.'), abnormalPartKeys.has(part.sourceKey))
      }
    }
  }
  for (const action of selection.actions) {
    for (const token of action.tokens) {
      add(token, false)
    }
  }
  return [...references.values()].sort((left, right) =>
    Number(right.abnormal) - Number(left.abnormal)
    || left.code.localeCompare(right.code),
  )
}

export function projectBomForMonitorSelection(
  workspace: LineUpImageWorkspace,
  references: LineUpMonitorPlcReference[],
): LineUpMonitorBomProjection {
  const referenceByCode = new Map(references.map(item => [normalizedPlcReference(item.code), item]))
  const matchedBoms = workspace.boms.filter(item => (
    referenceByCode.has(normalizedPlcReference(item.tagCode))
    || referenceByCode.has(normalizedPlcReference(item.dbAddress))
  ))
  const abnormalBomIds = matchedBoms.filter((item) => {
    const tagReference = referenceByCode.get(normalizedPlcReference(item.tagCode))
    const addressReference = referenceByCode.get(normalizedPlcReference(item.dbAddress))
    return tagReference?.abnormal === true || addressReference?.abnormal === true
  }).map(item => item.id)
  const abnormalIds = new Set(abnormalBomIds)
  const boms = [...matchedBoms].sort((left, right) =>
    Number(abnormalIds.has(right.id)) - Number(abnormalIds.has(left.id)),
  )
  const bomIds = new Set(boms.map(item => item.id))
  const markers = workspace.markers.filter(item => item.bomIds.some(id => bomIds.has(id)))
  const imageIds = new Set(markers.map(item => item.imageId))
  const images = workspace.images.filter(item => imageIds.has(item.id))
  return { boms, images, markers, abnormalBomIds }
}

export function describeMonitorBomLocation(input: LineUpMonitorBomEmptyInput) {
  if (!input.loaded) {
    return ''
  }
  if (input.stationBomCount === 0) {
    return t('bomEmptyNoImages')
  }
  if (input.referenceCount === 0) {
    return t('bomEmptyNoVariables')
  }
  if (input.matchedBomCount === 0) {
    return t('bomEmptyNoMatched', { count: input.referenceCount })
  }
  if (input.markerCount === 0) {
    return t('bomEmptyNoMarkers')
  }
  return ''
}

export interface LineUpMonitorIdentity {
  bindingId: string
  publishedRevisionId: string
  stationId: string
  sequenceSourceKey: string
}

function hasCompleteIdentity(identity: LineUpMonitorIdentity) {
  return Boolean(identity.bindingId
    && identity.publishedRevisionId
    && identity.stationId
    && identity.sequenceSourceKey)
}

function matchesIdentity(
  value: LineUpMonitorIdentity,
  expected: LineUpMonitorIdentity,
) {
  return value.bindingId === expected.bindingId
    && value.publishedRevisionId === expected.publishedRevisionId
    && value.stationId === expected.stationId
    && value.sequenceSourceKey === expected.sequenceSourceKey
}

export function matchingMonitorSnapshot(
  definition: LineUpMonitorDefinition | null,
  snapshot: LineUpMonitorSnapshot | null,
  expected: LineUpMonitorIdentity,
) {
  if (!definition
    || !snapshot
    || !hasCompleteIdentity(expected)
    || !definition.definitionRevision
    || !snapshot.definitionRevision
    || !matchesIdentity(definition, expected)
    || !matchesIdentity(snapshot, expected)
    || definition.definitionRevision !== snapshot.definitionRevision) {
    return null
  }
  return snapshot
}

export function conditionsForMonitorSelection(
  definition: LineUpMonitorDefinition | null,
  snapshot: LineUpMonitorSnapshot | null,
  selection: TiaGraphSelection | null,
) {
  if (!definition || definition.viewer.viewerSchemaVersion < 2 || !selection) {
    return []
  }
  const identityMatched = conditionsForSelection(definition, snapshot, selection)
  return identityMatched
}

export function eventsForMonitorSelection(
  events: LineUpMonitorEvent[],
  selection: TiaGraphSelection | null,
) {
  if (!selection) {
    return []
  }
  const selectedSourceKey = selection.node.sourceKey
  return events.filter(item => item.ownerSourceKey === selectedSourceKey)
}

export function exceptionsForMonitorSelection(
  exceptions: LineUpRuntimeException[],
  selection: TiaGraphSelection | null,
) {
  if (!selection) {
    return []
  }
  return exceptions.filter(item => item.ownerSourceKey === selection.node.sourceKey)
}

export function runtimeBindingActions(input: RuntimeBindingActionInput) {
  const enabled = input.monitoringEnabled === true
  const ready = input.runtimeBindingReady === true
  return {
    canPrepare: !enabled,
    canValidate: !enabled && input.bindingStatus === 'NeedsReview',
    canEnable: !enabled && ready && input.bindingStatus === 'Resolved',
    canDisable: enabled,
  }
}

export function normalizeStepStatus(value: LineUpStepStatus) {
  return value
}

export function normalizeTransitionStatus(value: LineUpTransitionStatus) {
  return value
}

export function normalizeQuality(value: LineUpRuntimeQuality) {
  return value
}

export function normalizeGatewayStatus(value: LineUpGatewayStatus) {
  return value
}

export function monitorAvailabilityText(input: MonitorAvailabilityInput) {
  if (!input.hasSelection) {
    return t('availPickLinePlc')
  }
  if (!input.hasImport) {
    return t('availNoGraphStructure')
  }
  if (!input.hasBinding) {
    return t('availNoRuntimeBinding')
  }
  if (input.bindingStatus !== 'Resolved' || !input.runtimeBindingReady) {
    return t('availAddressPending')
  }
  if (!input.monitoringEnabled) {
    return t('availMonitorNotEnabled')
  }
  if (input.connectionState === 'reconnecting') {
    return t('availReconnecting')
  }
  if (input.connectionState === 'error') {
    return t('availUnavailableWithError', { error: input.safeErrorCode || 'Unknown' })
  }
  if (input.gatewayStatus) {
    return t('availGatewaySnapshot', { status: input.gatewayStatus, revision: input.snapshotRevision ?? '—' })
  }
  return t('availConnecting')
}

function stepTone(status: LineUpStepStatus, quality: LineUpRuntimeQuality): LineUpRuntimeTone {
  if (normalizeQuality(quality) !== 'Good') {
    return 'unknown'
  }
  return ({
    Idle: 'idle',
    Active: 'active',
    Waiting: 'waiting',
    Completed: 'completed',
    Warning: 'warning',
    // 2026-09-11 B：步超时不再标红——TIA 模板默认 T#10S 普遍非现场组态的监护，
    // 设备待机停在步上是正常工况；用比健康活动步稍淡的专属色，与告警（琥珀）区分。
    TimedOut: 'timeout',
    Unknown: 'unknown',
  } as const)[normalizeStepStatus(status)]
}

function transitionTone(status: LineUpTransitionStatus, quality: LineUpRuntimeQuality): LineUpRuntimeTone {
  if (normalizeQuality(quality) !== 'Good') {
    return 'unknown'
  }
  return ({ Unsatisfied: 'idle', Satisfied: 'waiting', JustFired: 'active', Unknown: 'unknown' } as const)[normalizeTransitionStatus(status)]
}

function connectionTone(status: LineUpGatewayStatus): LineUpConnectionTone {
  return ({
    Online: 'online',
    Degraded: 'degraded',
    Offline: 'offline',
    Connecting: 'connecting',
    Reloading: 'connecting',
    ConfigurationError: 'offline',
  } as const)[normalizeGatewayStatus(status)]
}

function revision(value: string) {
  try {
    return BigInt(value)
  }
  catch {
    return null
  }
}

export function safeDurationNumber(value: string | null | undefined) {
  if (!value || !/^\d+$/.test(value)) {
    return null
  }
  try {
    const parsed = BigInt(value)
    return parsed <= BigInt(Number.MAX_SAFE_INTEGER) ? Number(parsed) : null
  }
  catch {
    return null
  }
}

export function formatMonitorDuration(value: string | null | undefined) {
  const milliseconds = safeDurationNumber(value)
  if (milliseconds !== null) {
    return `${(milliseconds / 1000).toFixed(1)} s`
  }
  if (value && /^\d+$/.test(value)) {
    return `${value.slice(0, 64)} ms`
  }
  return '—'
}

function mergeByKey<T extends { sourceKey: string }>(current: T[], changed: T[]) {
  const values = new Map(current.map(item => [item.sourceKey, item]))
  changed.forEach(item => values.set(item.sourceKey, item))
  return [...values.values()]
}

function mergeConditions(current: LineUpMonitorSnapshot['conditions'], changed: LineUpMonitorDelta['changedConditions']) {
  const values = new Map(current.map(item => [item.canonicalKey, item]))
  changed.forEach(item => values.set(item.canonicalKey, item))
  return [...values.values()]
}

export function applyMonitorDelta(current: LineUpMonitorSnapshot, delta: LineUpMonitorDelta): DeltaApplyResult {
  if (current.bindingId !== delta.bindingId
    || current.publishedRevisionId !== delta.publishedRevisionId
    || current.stationId !== delta.stationId
    || current.sequenceSourceKey !== delta.sequenceSourceKey
    || current.definitionRevision !== delta.definitionRevision) {
    return { kind: 'definition-mismatch', snapshot: null }
  }
  const currentRevision = revision(current.snapshotRevision)
  const fromRevision = revision(delta.fromSnapshotRevision)
  const toRevision = revision(delta.toSnapshotRevision)
  if (currentRevision === null || fromRevision === null || toRevision === null) {
    return { kind: 'gap', snapshot: null }
  }
  if (toRevision <= currentRevision) {
    return { kind: 'ignored', snapshot: null }
  }
  if (fromRevision !== currentRevision || toRevision !== currentRevision + 1n) {
    return { kind: 'gap', snapshot: null }
  }

  const steps = mergeByKey(current.steps, delta.changedSteps)
  return {
    kind: 'applied',
    snapshot: {
      ...current,
      snapshotRevision: delta.toSnapshotRevision,
      evaluatedAtUtc: delta.serverTimestamp,
      connection: delta.connection,
      steps,
      transitions: mergeByKey(current.transitions, delta.changedTransitions),
      conditions: mergeConditions(current.conditions, delta.changedConditions),
      edges: mergeByKey(current.edges, delta.changedEdges),
      exceptions: delta.exceptions,
      activeStepSourceKeys: steps.filter(item => ['Active', 'Waiting', 'Warning', 'TimedOut'].includes(normalizeStepStatus(item.status))).map(item => item.sourceKey),
    },
  }
}

export function projectMonitorSnapshot(snapshot: LineUpMonitorSnapshot): LineUpGraphRuntimeProjection {
  const nodes: Record<string, LineUpGraphRuntimeNodeProjection> = {}
  snapshot.steps.forEach((item) => {
    nodes[item.sourceKey] = {
      sourceKey: item.sourceKey,
      tone: stepTone(item.status, item.quality),
      label: normalizeStepStatus(item.status),
      quality: normalizeQuality(item.quality),
      activeDurationMs: safeDurationNumber(item.activeDurationMs),
      activeDurationText: item.activeDurationMs,
    }
  })
  snapshot.transitions.forEach((item) => {
    nodes[item.sourceKey] = {
      sourceKey: item.sourceKey,
      tone: transitionTone(item.status, item.quality),
      label: normalizeTransitionStatus(item.status),
      quality: normalizeQuality(item.quality),
      activeDurationMs: 0,
      activeDurationText: '0',
    }
  })
  const edges = Object.fromEntries(snapshot.edges.map(item => [item.sourceKey, {
    sourceKey: item.sourceKey,
    traversed: item.wasTraversed,
    tone: item.wasTraversed ? 'active' : 'idle',
  } satisfies LineUpGraphRuntimeEdgeProjection]))
  return { nodes, edges, connectionTone: connectionTone(snapshot.connection.status) }
}
