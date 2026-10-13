import type { LineUpGraphViewerDefinition } from './catalog'
import type { EntityId } from './types'
import api from '@/api/index'

type RuntimeEnum<T extends string> = T

export type LineUpGatewayStatus = RuntimeEnum<'Connecting' | 'Online' | 'Degraded' | 'Offline' | 'Reloading' | 'ConfigurationError'>
export type LineUpStepStatus = RuntimeEnum<'Idle' | 'Active' | 'Waiting' | 'Completed' | 'Warning' | 'TimedOut' | 'Unknown'>
export type LineUpTransitionStatus = RuntimeEnum<'Unsatisfied' | 'Satisfied' | 'JustFired' | 'Unknown'>
export type LineUpTriState = RuntimeEnum<'False' | 'True' | 'Unknown'>
export type LineUpRuntimeQuality = RuntimeEnum<'Good' | 'Bad' | 'Unknown' | 'Stale'>
export type LineUpMonitorNodeType = 'Line' | 'Process' | 'Station' | 'SubStation' | 'Graph'
export type LineUpMonitorRuntimeStatus = 'Unconfigured' | 'NeedsPrepare' | 'NeedsReview' | 'Ready' | 'Enabled' | 'Online' | 'Warning' | 'Error'

export interface LineUpMonitorNavigationNode {
  nodeId: string
  nodeType: LineUpMonitorNodeType
  parentNodeId: string | null
  entityId: EntityId
  code: string
  name: string
  sortOrder: number
  descendantCount: number
  issueCount: number
  runtimeStatus: LineUpMonitorRuntimeStatus
  publishedBindingCount: number
}

export interface LineUpMonitorBindingSummary {
  bindingId: EntityId
  graphAssetId: EntityId
  publishedRevisionId: EntityId
  lineId: EntityId
  deviceId: EntityId
  processId: EntityId
  stationId: EntityId
  subStationId: EntityId
  /** 图谱资产所属 PLC 项目 id（ai_plc_project_new.Id），供流程图工步详情查 ai_plc_graphsteptag 台账 */
  plcProjectId: EntityId
  graphSourceName: string
  bindingStatus: string
  monitoringEnabled: boolean
  runtimeBindingReady: boolean
  sequences: LineUpMonitorSequenceSummary[]
}

export interface LineUpMonitorSequenceSummary {
  sequenceSourceKey: string
  sequenceDisplayName: string
  stepCount: number
  transitionCount: number
  /** 顺控运行态（Error/Online/Enabled）；空 = 无实时快照（监控未启用或未轮询），前端不显示状态点 */
  runtimeStatus: string
}

export interface LineUpMonitorStepDefinition {
  sourceKey: string
  displayName: string
  isInitial: boolean
  warningTime: string
  maximumStepTime: string
}

export interface LineUpMonitorTransitionDefinition {
  sourceKey: string
  displayName: string
  conditionCanonicalKeys: string[]
}

export interface LineUpMonitorEdgeDefinition {
  sourceKey: string
  fromSourceKey: string
  toSourceKey: string
  edgeKind: string
}

export interface LineUpMonitorDefinition {
  bindingId: EntityId
  publishedRevisionId: EntityId
  stationId: EntityId
  sequenceSourceKey: string
  sequenceDisplayName: string
  definitionRevision: string
  steps: LineUpMonitorStepDefinition[]
  transitions: LineUpMonitorTransitionDefinition[]
  edges: LineUpMonitorEdgeDefinition[]
  viewer: LineUpGraphViewerDefinition
}

export interface LineUpConnectionHealth {
  status: LineUpGatewayStatus
  evaluatedAtUtc: string
  lastGoodSampleAtUtc: string | null
  errorCode: string
}

export interface LineUpStepRuntimeState {
  sourceKey: string
  displayName: string
  status: LineUpStepStatus
  lastValidValue: boolean | null
  quality: LineUpRuntimeQuality
  activeDurationMs: string
  stateChangedAtUtc: string
  lastGoodAtUtc: string | null
  qualityCode: string
}

export interface LineUpTransitionRuntimeState {
  sourceKey: string
  displayName: string
  status: LineUpTransitionStatus
  condition: LineUpTriState
  quality: LineUpRuntimeQuality
  stateChangedAtUtc: string
  qualityCode: string
}

export interface LineUpConditionRuntimeValue {
  canonicalKey: string
  ownerSourceKey: string
  networkSourceKey: string
  partSourceKey: string
  state: LineUpTriState
  quality: LineUpRuntimeQuality
  value: unknown
  sourceTimestamp: string | null
  qualityCode: string
  /** Access(GlobalVariable) 部件的完整变量路径（如 DB760_MainGraph.放置过程完成.X）；空/缺 = 派生部件（Contact/Coil 等） */
  variablePath?: string
}

export interface LineUpEdgeRuntimeState {
  sourceKey: string
  wasTraversed: boolean
  traversedAtUtc: string | null
}

export interface LineUpRuntimeException {
  code: string
  ownerSourceKey: string
  severity: string
  safeMessage: string
  occurredAtUtc: string
}

export interface LineUpMonitorSnapshot {
  bindingId: EntityId
  publishedRevisionId: EntityId
  stationId: EntityId
  sequenceSourceKey: string
  definitionRevision: string
  snapshotRevision: string
  evaluatedAtUtc: string
  connection: LineUpConnectionHealth
  steps: LineUpStepRuntimeState[]
  transitions: LineUpTransitionRuntimeState[]
  conditions: LineUpConditionRuntimeValue[]
  edges: LineUpEdgeRuntimeState[]
  exceptions: LineUpRuntimeException[]
  activeStepSourceKeys: string[]
}

export interface LineUpMonitorDelta {
  bindingId: EntityId
  publishedRevisionId: EntityId
  stationId: EntityId
  sequenceSourceKey: string
  definitionRevision: string
  fromSnapshotRevision: string
  toSnapshotRevision: string
  serverTimestamp: string
  connection: LineUpConnectionHealth
  changedSteps: LineUpStepRuntimeState[]
  changedTransitions: LineUpTransitionRuntimeState[]
  changedConditions: LineUpConditionRuntimeValue[]
  changedEdges: LineUpEdgeRuntimeState[]
  exceptions: LineUpRuntimeException[]
}

export interface LineUpMonitorEvent {
  id: EntityId
  bindingId: EntityId
  sequenceSourceKey: string
  ownerSourceKey: string
  eventType: string
  previousState: string
  currentState: string
  occurredAtUtc: string
  durationMs: string | null
  quality: string
  qualityCode: string
  definitionRevision: string
  gatewayStreamId: string
  gatewaySequence: string
  eventOrdinal: number
  detailsJson: string
}

export interface LineUpMonitorEventPage {
  items: LineUpMonitorEvent[]
  nextBeforeEventId: EntityId
  hasMore: boolean
}

export interface LineUpRuntimeBindingIssue {
  code: string
  ownerSourceKey: string
  severity: string
  networkSourceKey: string
  partSourceKey: string
}

export interface LineUpRuntimeBindingPreparation {
  bindingId: EntityId
  bindingStatus: string
  definitionRevision: string
  tagCount: number
  canValidate: boolean
  issues: LineUpRuntimeBindingIssue[]
}

export interface LineUpRuntimeBindingValidation {
  bindingId: EntityId
  bindingStatus: string
  goodCount: number
  totalCount: number
  canEnable: boolean
  issues: LineUpRuntimeBindingIssue[]
}

export interface LineUpMonitoringStatus {
  bindingId: EntityId
  bindingStatus: string
  monitoringEnabled: boolean
  globalMonitorEnabled: boolean
}

export interface LineUpMonitorGateway {
  getNavigation: (includeUnconfigured?: boolean, keyword?: string) => Promise<LineUpMonitorNavigationNode[]>
  getBindingsBySubStation: (subStationId: EntityId) => Promise<LineUpMonitorBindingSummary[]>
  getBindings: (lineId: EntityId, deviceId: EntityId, stationId: EntityId) => Promise<LineUpMonitorBindingSummary[]>
  getDefinition: (bindingId: EntityId, sequenceSourceKey: string) => Promise<LineUpMonitorDefinition>
  getSnapshot: (bindingId: EntityId, sequenceSourceKey: string) => Promise<LineUpMonitorSnapshot>
  getEvents: (bindingId: EntityId, sequenceSourceKey: string, beforeEventId?: EntityId, limit?: number) => Promise<LineUpMonitorEventPage>
}

export interface LineUpMonitorAdminGateway extends LineUpMonitorGateway {
  prepareBinding: (bindingId: EntityId) => Promise<LineUpRuntimeBindingPreparation>
  validateBinding: (bindingId: EntityId) => Promise<LineUpRuntimeBindingValidation>
  setMonitoring: (bindingId: EntityId, enabled: boolean) => Promise<LineUpMonitoringStatus>
}

export interface LineUpMonitorRealtimeConnection {
  on: (name: string, handler: (payload: any) => void) => void
  onreconnecting: (handler: (...args: any[]) => void) => void
  onreconnected: (handler: (...args: any[]) => void) => void
  onclose: (handler: (...args: any[]) => void) => void
  start: () => Promise<void>
  stop: () => Promise<void>
  invoke: (method: string, ...args: unknown[]) => Promise<unknown>
}

interface ApiEnvelope<T> {
  isSuccess: boolean
  data?: T
  backResult?: T
  errorMessage?: string | null
}

function unwrap<T>(response: ApiEnvelope<T>): T {
  const result = response.data ?? response.backResult
  if (response.isSuccess === false || result === undefined || result === null) {
    throw new Error(response.errorMessage || 'LineUpMonitorRequestFailed')
  }
  return result
}

async function getBindings(lineId: EntityId, deviceId: EntityId, stationId: EntityId) {
  const response = await api.get('LineUp/Monitor/Bindings', { params: { lineId, deviceId, stationId } })
  return unwrap<LineUpMonitorBindingSummary[]>(response as unknown as ApiEnvelope<LineUpMonitorBindingSummary[]>)
}

async function getNavigation(includeUnconfigured = true, keyword = '') {
  const response = await api.get('LineUp/Monitor/Navigation', {
    params: { includeUnconfigured, keyword: keyword.trim() || undefined },
  })
  return unwrap<LineUpMonitorNavigationNode[]>(response as unknown as ApiEnvelope<LineUpMonitorNavigationNode[]>)
}

async function getBindingsBySubStation(subStationId: EntityId) {
  const response = await api.get('LineUp/Monitor/Bindings', { params: { subStationId } })
  return unwrap<LineUpMonitorBindingSummary[]>(response as unknown as ApiEnvelope<LineUpMonitorBindingSummary[]>)
}

async function getDefinition(bindingId: EntityId, sequenceSourceKey: string) {
  const response = await api.get(`LineUp/Monitor/${encodeURIComponent(bindingId)}/Definition`, {
    params: { sequenceSourceKey },
  })
  return unwrap<LineUpMonitorDefinition>(response as unknown as ApiEnvelope<LineUpMonitorDefinition>)
}

async function getSnapshot(bindingId: EntityId, sequenceSourceKey: string) {
  const response = await api.get(`LineUp/Monitor/${encodeURIComponent(bindingId)}/Snapshot`, {
    params: { sequenceSourceKey },
  })
  return unwrap<LineUpMonitorSnapshot>(response as unknown as ApiEnvelope<LineUpMonitorSnapshot>)
}

async function getEvents(bindingId: EntityId, sequenceSourceKey: string, beforeEventId?: EntityId, limit = 50) {
  const response = await api.get(`LineUp/Monitor/${encodeURIComponent(bindingId)}/Events`, {
    params: { sequenceSourceKey, beforeEventId: beforeEventId || undefined, limit },
  })
  return unwrap<LineUpMonitorEventPage>(response as unknown as ApiEnvelope<LineUpMonitorEventPage>)
}

async function prepareBinding(bindingId: EntityId) {
  const response = await api.post(`LineUp/RuntimeBindings/${encodeURIComponent(bindingId)}/Prepare`)
  return unwrap<LineUpRuntimeBindingPreparation>(response as unknown as ApiEnvelope<LineUpRuntimeBindingPreparation>)
}

async function validateBinding(bindingId: EntityId) {
  const response = await api.post(`LineUp/RuntimeBindings/${encodeURIComponent(bindingId)}/Validate`)
  return unwrap<LineUpRuntimeBindingValidation>(response as unknown as ApiEnvelope<LineUpRuntimeBindingValidation>)
}

async function setMonitoring(bindingId: EntityId, enabled: boolean) {
  const response = await api.put(`LineUp/RuntimeBindings/${encodeURIComponent(bindingId)}/Monitoring`, { enabled })
  return unwrap<LineUpMonitoringStatus>(response as unknown as ApiEnvelope<LineUpMonitoringStatus>)
}

// ===== 变量采集开关（2026-09-12）=====
// 屏蔽 = 变量不进运行时读计划：引用它的转移条件求值落 Unknown（不降级连接状态）。

export async function listTagCollectionRules(plcProjectId: EntityId): Promise<string[]> {
  const response = await api.get('LineUp/Monitor/TagCollection/Rules', { params: { plcProjectId } })
  return unwrap<string[]>(response as unknown as ApiEnvelope<string[]>)
}

export async function disableTagCollection(plcProjectId: EntityId, variablePath: string, reason?: string): Promise<boolean> {
  const response = await api.post('LineUp/Monitor/TagCollection/Disable', {
    plcProjectId,
    variablePath,
    reason: reason || null,
  })
  return unwrap<boolean>(response as unknown as ApiEnvelope<boolean>)
}

export async function enableTagCollection(plcProjectId: EntityId, variablePath: string): Promise<boolean> {
  const response = await api.post('LineUp/Monitor/TagCollection/Enable', {
    plcProjectId,
    variablePath,
  })
  return unwrap<boolean>(response as unknown as ApiEnvelope<boolean>)
}

const lineUpMonitorApi: LineUpMonitorAdminGateway = {
  getNavigation,
  getBindingsBySubStation,
  getBindings,
  getDefinition,
  getSnapshot,
  getEvents,
  prepareBinding,
  validateBinding,
  setMonitoring,
}

export default lineUpMonitorApi
