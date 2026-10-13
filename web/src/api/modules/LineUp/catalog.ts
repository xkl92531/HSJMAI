import type {
  EntityId,
  LineUpGraphLogicEndpoint,
  LineUpGraphLogicNetwork,
  LineUpGraphLogicPart,
  LineUpGraphLogicWire,
  LineUpImportAction,
  LineUpImportNode,
  LineUpImportSequence,
  LineUpImportSummary,
} from './types'
import api from '@/api/index'

export interface LineUpGraphCatalogProject { id: EntityId, name: string, plcDeviceId: EntityId | null }
export interface LineUpGraphCatalogLine { id: EntityId, code: string | null, name: string | null }
export interface LineUpGraphCatalogProcess { id: EntityId, lineId: EntityId, code: string | null, name: string | null }
export interface LineUpGraphCatalogStation { id: EntityId, lineId: EntityId, processId: EntityId, code: string | null, name: string | null }
export interface LineUpGraphCatalogSubStation { id: EntityId, lineId: EntityId, processId: EntityId, stationId: EntityId, code: string | null, name: string | null, plcId: EntityId | null, plcName: string | null }
export interface LineUpGraphCatalogDevice { id: EntityId, lineId: EntityId, name: string | null, projectName: string | null, deviceType: string | null }

export interface LineUpGraphCatalogLookup {
  projects: LineUpGraphCatalogProject[]
  lines: LineUpGraphCatalogLine[]
  processes: LineUpGraphCatalogProcess[]
  stations: LineUpGraphCatalogStation[]
  subStations: LineUpGraphCatalogSubStation[]
  devices: LineUpGraphCatalogDevice[]
}

export interface LineUpGraphCatalogQuery {
  plcProjectId?: EntityId
  lineId?: EntityId
  processId?: EntityId
  stationId?: EntityId
  subStationId?: EntityId
  deviceId?: EntityId
  status?: string
  keyword?: string
  page: number
  pageSize: number
}

export interface LineUpGraphCatalogAsset {
  assetId: EntityId
  plcProjectId: EntityId
  plcProjectName: string
  sourceBlockName: string
  blockType: string
  blockNumber: number
  currentPlcBlockId: EntityId
  latestRevisionId: EntityId | null
  latestRevisionNo: number
  latestRevisionStatus: string
  summary: LineUpImportSummary
  bindingId: EntityId | null
  lineId: EntityId | null
  processId: EntityId | null
  stationId: EntityId | null
  subStationId: EntityId | null
  deviceId: EntityId | null
  publishedRevisionId: EntityId | null
  publishedRevisionNo: number | null
  bindingStatus: string | null
  monitoringEnabled: boolean
  /** 该块是否已在导入工作台生成过预览（存在 PreviewReady 批次） */
  hasPreviewBatch: boolean
}

export interface LineUpGraphCatalogPage {
  items: LineUpGraphCatalogAsset[]
  total: number
  page: number
  pageSize: number
}

export interface LineUpGraphRevision {
  id: EntityId
  revisionNo: number
  status: string
  sourceHash: string
  publishedAt: string | null
  publishedBy: EntityId | null
}

export interface LineUpGraphBinding {
  id: EntityId
  graphAssetId: EntityId
  lineId: EntityId
  processId: EntityId
  stationId: EntityId
  subStationId: EntityId | null
  deviceId: EntityId
  instanceDbBlockId: EntityId | null
  publishedRevisionId: EntityId | null
  bindingStatus: string
  monitoringEnabled: boolean
  modifyTime: string | null
}

export interface LineUpGraphAlias { elementSourceKey: string, displayName: string }

export type LineUpGraphViewerLogicEndpoint = LineUpGraphLogicEndpoint
export type LineUpGraphViewerLogicWire = LineUpGraphLogicWire
export type LineUpGraphViewerLogicPart = LineUpGraphLogicPart
export type LineUpGraphViewerLogicNetwork = LineUpGraphLogicNetwork

export type LineUpGraphViewerNode = LineUpImportNode

export type LineUpGraphViewerAction = LineUpImportAction

export interface LineUpGraphViewerDefinition extends Omit<LineUpImportSequence, 'nodes' | 'actions'> {
  viewerSchemaVersion: number
  viewerCapabilities: string[]
  viewerWarnings: string[]
  nodes: LineUpGraphViewerNode[]
  actions: LineUpGraphViewerAction[]
}

export interface LineUpGraphPreview {
  schema?: string
  viewerSchemaVersion: number
  viewerCapabilities: string[]
  viewerWarnings: string[]
  blockName: string
  blockNumber: number
  programmingLanguage: string
  sequences: LineUpGraphViewerDefinition[]
}

export interface LineUpGraphCatalogDetail {
  asset: LineUpGraphCatalogAsset
  revisions: LineUpGraphRevision[]
  binding: LineUpGraphBinding | null
  aliases: LineUpGraphAlias[]
  preview: LineUpGraphPreview
}

export interface LineUpGraphDiffCounts {
  sequenceCount: number
  nodeCount: number
  actionCount: number
  edgeCount: number
}

export function totalGraphDiffCounts(group: LineUpGraphDiffCounts | undefined) {
  return group
    ? group.sequenceCount + group.nodeCount + group.actionCount + group.edgeCount
    : 0
}
export interface LineUpGraphDiffItem { elementKind: string, sourceKey: string, changeKind: string }
export interface LineUpGraphRevisionDiff {
  isInitialPublish: boolean
  added: LineUpGraphDiffCounts
  removed: LineUpGraphDiffCounts
  changed: LineUpGraphDiffCounts
  items: LineUpGraphDiffItem[]
}

export interface LineUpGraphPublishPreview {
  bindingId: EntityId
  targetRevisionId: EntityId
  targetRevisionNo: number
  publishedRevisionId: EntityId | null
  publishedRevisionNo: number | null
  diff: LineUpGraphRevisionDiff
  preview: LineUpGraphPreview
}

export interface LineUpGraphBindingRequest {
  lineId: EntityId
  processId: EntityId
  stationId: EntityId
  subStationId: EntityId
  deviceId: EntityId
  instanceDbBlockId: EntityId | null
}

export interface LineUpGraphAliasSaveRequest {
  expectedLatestRevisionId: EntityId
  aliases: LineUpGraphAlias[]
}

export interface LineUpGraphPublishRequest {
  targetRevisionId: EntityId
  expectedLatestRevisionId: EntityId
  expectedPublishedRevisionId: EntityId | null
}

export interface LineUpGraphPublishReceipt {
  bindingId: EntityId
  publishedRevisionId: EntityId
  publishedRevisionNo: number
  previousPublishedRevisionId: EntityId | null
  publishedAt: string
  monitoringEnabled: boolean
}

export interface LineUpGraphCatalogGateway {
  getLookups: (params: { plcProjectId?: EntityId, lineId?: EntityId }) => Promise<LineUpGraphCatalogLookup>
  getAssets: (params: LineUpGraphCatalogQuery) => Promise<LineUpGraphCatalogPage>
  getDetail: (assetId: EntityId, bindingId?: EntityId) => Promise<LineUpGraphCatalogDetail>
  saveBinding: (assetId: EntityId, request: LineUpGraphBindingRequest) => Promise<LineUpGraphBinding>
  saveAliases: (bindingId: EntityId, request: LineUpGraphAliasSaveRequest) => Promise<LineUpGraphAlias[]>
  previewPublish: (bindingId: EntityId, targetRevisionId: EntityId) => Promise<LineUpGraphPublishPreview>
  publish: (bindingId: EntityId, request: LineUpGraphPublishRequest) => Promise<LineUpGraphPublishReceipt>
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
    throw new Error(response.errorMessage || 'LineUpGraphCatalogRequestFailed')
  }
  return result
}

async function getLookups(params: { plcProjectId?: EntityId, lineId?: EntityId }) {
  return unwrap<LineUpGraphCatalogLookup>(await api.get('LineUp/GraphCatalog/Lookups', { params }) as unknown as ApiEnvelope<LineUpGraphCatalogLookup>)
}

async function getAssets(params: LineUpGraphCatalogQuery) {
  return unwrap<LineUpGraphCatalogPage>(await api.get('LineUp/GraphCatalog', { params }) as unknown as ApiEnvelope<LineUpGraphCatalogPage>)
}

async function getDetail(assetId: EntityId, bindingId?: EntityId) {
  return unwrap<LineUpGraphCatalogDetail>(await api.get(`LineUp/GraphCatalog/${encodeURIComponent(assetId)}`, {
    params: bindingId ? { bindingId } : undefined,
  }) as unknown as ApiEnvelope<LineUpGraphCatalogDetail>)
}

async function saveBinding(assetId: EntityId, request: LineUpGraphBindingRequest) {
  return unwrap<LineUpGraphBinding>(await api.post(`LineUp/GraphCatalog/${encodeURIComponent(assetId)}/Bindings`, request) as unknown as ApiEnvelope<LineUpGraphBinding>)
}

async function saveAliases(bindingId: EntityId, request: LineUpGraphAliasSaveRequest) {
  return unwrap<LineUpGraphAlias[]>(await api.put(`LineUp/GraphCatalog/Bindings/${encodeURIComponent(bindingId)}/Aliases`, request) as unknown as ApiEnvelope<LineUpGraphAlias[]>)
}

async function previewPublish(bindingId: EntityId, targetRevisionId: EntityId) {
  return unwrap<LineUpGraphPublishPreview>(await api.get(`LineUp/GraphCatalog/Bindings/${encodeURIComponent(bindingId)}/PublishPreview`, {
    params: { targetRevisionId },
  }) as unknown as ApiEnvelope<LineUpGraphPublishPreview>)
}

async function publish(bindingId: EntityId, request: LineUpGraphPublishRequest) {
  return unwrap<LineUpGraphPublishReceipt>(await api.post(`LineUp/GraphCatalog/Bindings/${encodeURIComponent(bindingId)}/Publish`, request) as unknown as ApiEnvelope<LineUpGraphPublishReceipt>)
}

const lineUpGraphCatalogApi: LineUpGraphCatalogGateway = {
  getLookups,
  getAssets,
  getDetail,
  saveBinding,
  saveAliases,
  previewPublish,
  publish,
}

export default lineUpGraphCatalogApi
