export type EntityId = string
export type ImportStatus = 'Uploaded' | 'Parsing' | 'PreviewReady' | 'Committed' | 'Failed'
export type CommitMode = 'StructureOnly' | 'ReadyForMonitoring'
export type BindingStatus = 'Unresolved' | 'NeedsReview' | 'Resolved'
export type BindingStatusValue = BindingStatus | 0 | 1 | 2
export type IssueSeverity = 'Info' | 'Warning' | 'Blocking'
export type IssueSeverityValue = IssueSeverity | 0 | 1 | 2
export type ConfidenceLevel = 'Automatic' | 'Suggested' | 'NeedsReview'
export type ConfidenceLevelValue = ConfidenceLevel | 0 | 1 | 2
export type CandidateKind = 'ExistingBinding' | 'ExistingSubStation' | 'ProposedSubStation' | 'StationCandidate'
export type CandidateKindValue = CandidateKind | 0 | 1 | 2 | 3
export type StructuralStatus = 'Ready' | 'Blocked'
export type StructuralStatusValue = StructuralStatus | 0 | 1

export interface LineUpIssue {
  severity: IssueSeverityValue
  issueType: string
  sourceKey: string | null
  message: string
}

export interface LineUpImportSummary {
  documentCount: number
  sequenceCount: number
  stepCount: number
  transitionCount: number
  actionCount: number
  edgeCount: number
  blockingIssueCount: number
}

export interface LineUpGraphLogicEndpoint {
  kind: string
  uId: number | null
  name: string
}

export interface LineUpGraphLogicWire {
  sourceKey: string
  uId: number
  endpoints: LineUpGraphLogicEndpoint[]
}

export interface LineUpGraphLogicPart {
  sourceKey: string
  uId: number
  partName: string
  scope: string
  symbolPath: string[]
  constantValue: string
  negated: boolean
  attributes: Array<{ name: string, value: string }>
  templateValues: Array<{ name: string, value: string }>
  pinNames: string[]
}

export interface LineUpGraphLogicNetwork {
  sourceKey: string
  ownerSourceKey: string
  role: string
  ordinal: number
  programmingLanguage: string
  title: string
  parts: LineUpGraphLogicPart[]
  wires: LineUpGraphLogicWire[]
}

export interface LineUpImportNode {
  nodeKind: string
  sourceKey: string
  sourceName: string
  displayName: string
  number: number
  isInitial: boolean
  isFinal: boolean
  maximumStepTime?: string
  warningTime?: string
  comment?: string
  actionGroupTitle: string
  logicNetworks: LineUpGraphLogicNetwork[]
  programmingLanguage?: string
  isMissing?: boolean
  isJump?: boolean
}

export interface LineUpImportAction {
  sourceKey: string
  stepSourceKey: string
  sourceName: string
  displayName: string
  ordinal: number
  stepNumber: number
  qualifier: string
  event: string | null
  interlock: boolean | null
  tokens: string[]
  actionText: string
}

export interface LineUpImportEdge {
  sourceKey: string
  fromSourceKey: string
  toSourceKey: string
  fromNodeKind: string
  toNodeKind: string
  edgeKind: string
  branchOrder: number | null
}

export interface LineUpImportSequence {
  sourceKey: string
  sourceName: string
  displayName: string
  nodes: LineUpImportNode[]
  actions: LineUpImportAction[]
  edges: LineUpImportEdge[]
}

export interface LineUpAddressSequence {
  sequenceSourceKey: string
  bindingStatus: BindingStatusValue
}

export interface LineUpAddressCandidate {
  candidateKey: string
  candidateKind: string
  id: EntityId
  name: string
  evidenceCodes: string[]
}

export interface LineUpAddressResolution {
  lineId: EntityId
  deviceId: EntityId
  plcProjectId: EntityId | null
  plcProjectName: string | null
  graphBlockId: EntityId | null
  graphBlockName: string | null
  instanceDbBlockId: EntityId | null
  instanceDbBlockName: string | null
  structuralStatus: StructuralStatusValue
  canCommitStructure: boolean
  canCommitForMonitoring: boolean
  sequences: LineUpAddressSequence[]
  candidates: LineUpAddressCandidate[]
  issues: LineUpIssue[]
}

export interface LineUpHierarchyCandidate {
  candidateKey: string
  candidateKind: CandidateKindValue
  sequenceSourceKey: string
  processId: EntityId
  stationId: EntityId
  subStationId: EntityId | null
  proposedSubStationCode: string | null
  proposedSubStationName: string | null
  processCode: string | null
  processName: string | null
  stationCode: string | null
  stationName: string | null
  subStationCode: string | null
  subStationName: string | null
  confidenceLevel: ConfidenceLevelValue
  evidenceCodes: string[]
}

export interface LineUpHierarchySequence {
  sequenceSourceKey: string
  confidenceLevel: ConfidenceLevelValue
  processId: EntityId | null
  stationId: EntityId | null
  subStationId: EntityId | null
  selectedCandidateKey: string | null
  candidates: LineUpHierarchyCandidate[]
}

export interface LineUpHierarchyResolution {
  lineId: EntityId
  deviceId: EntityId
  resolvedPlcProjectId: EntityId | null
  canCommitStructure: boolean
  sequences: LineUpHierarchySequence[]
  issues: LineUpIssue[]
}

export interface LineUpImportDocument {
  sourceFileName: string
  sourceRelativePath: string
  sourceSha256: string
  blockName: string
  blockNumber: number
  programmingLanguage: string
  instanceDbHint: string | null
  sequences: LineUpImportSequence[]
  address: LineUpAddressResolution
  hierarchy: LineUpHierarchyResolution
  issues: LineUpIssue[]
}

export interface LineUpImportStarted {
  batchId: EntityId
  status: ImportStatus
}

export interface LineUpImportProgress {
  batchId: EntityId
  status: ImportStatus
  errorCode: string | null
  previewRevision: number
  canLoadPreview: boolean
}

export interface LineUpImportPreview {
  batchId: EntityId
  status: ImportStatus
  previewSchemaVersion: number
  previewRevision: number
  previewHash: string
  sourceFileName: string
  sourceFileHash: string
  lineId: EntityId
  deviceId: EntityId
  documents: LineUpImportDocument[]
  issues: LineUpIssue[]
  summary: LineUpImportSummary
  diffToken: string
  canCommitStructure: boolean
  canCommitForMonitoring: boolean
}

export interface LineUpHierarchyDecision {
  sequenceSourceKey: string
  candidateKey: string
}

export interface LineUpDisplayNameChange {
  sourceKey: string
  displayName: string
}

export interface LineUpImportCommitRequest {
  commitMode: CommitMode
  previewRevision: number
  previewHash: string
  diffToken: string
  hierarchyDecisions: LineUpHierarchyDecision[]
  displayNameChanges: LineUpDisplayNameChange[]
}

export interface LineUpImportCommitReceipt {
  batchId: EntityId
  status: ImportStatus
  commitMode: CommitMode
  previewRevision: number
  previewHash: string
  committedAt: string
}

export interface LineUpWorkspaceLine {
  id: EntityId
  code: string
  name: string
}

export interface LineUpWorkspaceDevice {
  id: EntityId
  name: string
  projectName: string
  deviceType: string
  registrationStatus: number
}

export interface LineUpWorkspaceImport {
  batchId: EntityId
  lineId: EntityId
  deviceId: EntityId
  sourceFileName: string
  status: ImportStatus
  errorCode: string | null
  previewRevision: number
  createdAt: string | null
  committedAt: string | null
  summary: LineUpImportSummary
}

export interface LineUpWorkspaceSummary {
  lineCount: number
  deviceCount: number
  recentImportCount: number
  previewReadyCount: number
  committedCount: number
  failedCount: number
}

export interface LineUpWorkspace {
  lines: LineUpWorkspaceLine[]
  selectedLineId: EntityId | null
  devices: LineUpWorkspaceDevice[]
  recentImports: LineUpWorkspaceImport[]
  summary: LineUpWorkspaceSummary
}

export interface LineUpImportGateway {
  startPreview: (file: File, lineId: EntityId, deviceId: EntityId) => Promise<LineUpImportStarted>
  /** 从 PLC GRAPH 块免上传生成预览（服务端取 ai_plc_sourcefile.filecontent 归档原文） */
  startPreviewFromBlock?: (blockId: EntityId, lineId: EntityId, deviceId: EntityId) => Promise<LineUpImportStarted>
  getProgress: (batchId: EntityId) => Promise<LineUpImportProgress>
  /** 查找该产线+设备+GRAPH 块名下最新一条可预览批次（无则 null） */
  getLatestPreviewBatch?: (lineId: EntityId, deviceId: EntityId, blockName: string) => Promise<{ batchId: EntityId } | null>
  getPreview: (batchId: EntityId) => Promise<LineUpImportPreview>
  commit: (batchId: EntityId, request: LineUpImportCommitRequest) => Promise<LineUpImportCommitReceipt>
  /** 把结构绑定按序列改派到新的工序/工站/子工站（已保存的直接改；未保存的带 batchId 由后端从预览取元数据直接新建绑定） */
  /** 预览就绪后异步重建节点→BOM→点位图关联快照（AI_LineUp_GraphNodeBom），失败静默 */
  rebuildGraphNodeBom?: (batchId: EntityId) => Promise<unknown>
  reassignHierarchy?: (lineId: EntityId, deviceId: EntityId, updates: LineUpHierarchyReassignItem[], batchId?: EntityId) => Promise<LineUpHierarchyReassignReceipt>
}

/** 层级改派条目：按序列 SourceKey 把既有结构绑定改指到同线同 PLC 的工序/工站/子工站 */
export interface LineUpHierarchyReassignItem {
  sequenceSourceKey: string
  /** 目标层级 Id；传 '0' 表示该级用 new*Code 新建 */
  processId: EntityId
  stationId: EntityId
  subStationId: EntityId
  /** 该级不存在时的新增编码（忽略大小写与既有编码匹配可幂等复用） */
  newProcessCode?: string | null
  newStationCode?: string | null
  newSubStationCode?: string | null
}

export interface LineUpHierarchyReassignReceipt {
  updatedCount: number
  createdProcessCount: number
  createdStationCount: number
  createdSubStationCount: number
  /** 本次直接新建的结构绑定行数（此前无绑定、由预览批次元数据直接建绑定的顺控数） */
  createdBindingCount: number
}

export function normalizeBindingStatus(value: BindingStatusValue): BindingStatus {
  if (value === 2 || value === 'Resolved') {
    return 'Resolved'
  }
  if (value === 1 || value === 'NeedsReview') {
    return 'NeedsReview'
  }
  return 'Unresolved'
}

export function normalizeIssueSeverity(value: IssueSeverityValue): IssueSeverity {
  if (value === 2 || value === 'Blocking') {
    return 'Blocking'
  }
  if (value === 1 || value === 'Warning') {
    return 'Warning'
  }
  return 'Info'
}

export function normalizeConfidence(value: ConfidenceLevelValue): ConfidenceLevel {
  if (value === 0 || value === 'Automatic') {
    return 'Automatic'
  }
  if (value === 1 || value === 'Suggested') {
    return 'Suggested'
  }
  return 'NeedsReview'
}

export function normalizeCandidateKind(value: CandidateKindValue): CandidateKind {
  if (value === 0 || value === 'ExistingBinding') {
    return 'ExistingBinding'
  }
  if (value === 1 || value === 'ExistingSubStation') {
    return 'ExistingSubStation'
  }
  if (value === 2 || value === 'ProposedSubStation') {
    return 'ProposedSubStation'
  }
  return 'StationCandidate'
}
