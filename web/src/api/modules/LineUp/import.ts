import type {
  EntityId,
  LineUpHierarchyReassignItem,
  LineUpHierarchyReassignReceipt,
  LineUpImportCommitReceipt,
  LineUpImportCommitRequest,
  LineUpImportGateway,
  LineUpImportPreview,
  LineUpImportProgress,
  LineUpImportStarted,
  LineUpWorkspace,
} from './types'
import api from '@/api/index'

interface ApiEnvelope<T> {
  isSuccess: boolean
  data?: T
  backResult?: T
  errorMessage?: string | null
  exceptionMessage?: string | null
}

function unwrap<T>(response: ApiEnvelope<T>): T {
  const result = response.data ?? response.backResult
  if (response.isSuccess === false || result === undefined || result === null) {
    // errorMessage=机器可读 code；exceptionMessage=后端定位 detail（块/绑定/issue 统计），拼一起展示
    const code = response.errorMessage || 'LineUpRequestFailed'
    const detail = response.exceptionMessage?.trim()
    throw new Error(detail ? `${code}: ${detail}` : code)
  }
  return result
}

async function startPreview(file: File, lineId: EntityId, deviceId: EntityId) {
  const form = new FormData()
  form.append('file', file, file.name)
  form.append('lineId', lineId)
  form.append('deviceId', deviceId)
  return unwrap<LineUpImportStarted>(await api.post('LineUp/Import/Preview', form, {
    headers: { 'Content-Type': 'multipart/form-data' },
  }) as unknown as ApiEnvelope<LineUpImportStarted>)
}

/** 从 PLC GRAPH 块免上传生成预览（graph 结构管理页弹框入口，服务端取库内归档原文） */
async function startPreviewFromBlock(blockId: EntityId, lineId: EntityId, deviceId: EntityId) {
  return unwrap<LineUpImportStarted>(await api.post('LineUp/Import/PreviewFromBlock', {
    blockId,
    lineId,
    deviceId,
  }) as unknown as ApiEnvelope<LineUpImportStarted>)
}

async function getProgress(batchId: EntityId) {
  const response = await api.get(`LineUp/Import/${encodeURIComponent(batchId)}/Progress`)
  return unwrap<LineUpImportProgress>(response as unknown as ApiEnvelope<LineUpImportProgress>)
}

/** 查找该产线+设备+GRAPH 块名下最新一条可预览批次；没有则返回 null（不抛错） */
async function getLatestPreviewBatch(lineId: EntityId, deviceId: EntityId, blockName: string): Promise<{ batchId: EntityId } | null> {
  const response = await api.get('LineUp/Import/LatestPreview', {
    params: { lineId, deviceId, blockName },
  }) as unknown as ApiEnvelope<{ batchId: EntityId } | null>
  if (response.isSuccess === false) {
    return null
  }
  return response.data ?? response.backResult ?? null
}

async function getPreview(batchId: EntityId) {
  const response = await api.get(`LineUp/Import/${encodeURIComponent(batchId)}/Preview`)
  return unwrap<LineUpImportPreview>(response as unknown as ApiEnvelope<LineUpImportPreview>)
}

async function commit(batchId: EntityId, request: LineUpImportCommitRequest) {
  const response = await api.post(
    `LineUp/Import/${encodeURIComponent(batchId)}/Commit`,
    request,
  )
  return unwrap<LineUpImportCommitReceipt>(response as unknown as ApiEnvelope<LineUpImportCommitReceipt>)
}

async function getWorkspace(lineId?: EntityId | null) {
  const response = await api.get('LineUp/Workspace', {
    params: lineId ? { lineId } : undefined,
  })
  return unwrap<LineUpWorkspace>(response as unknown as ApiEnvelope<LineUpWorkspace>)
}

/** 预览就绪后重建节点→BOM→点位图关联快照（服务端解析 PreviewJson 落 AI_LineUp_GraphNodeBom） */
async function rebuildGraphNodeBom(batchId: EntityId) {
  const response = await api.post(
    'LineUp/GraphNodeBom/' + encodeURIComponent(batchId) + '/Rebuild',
  )
  return unwrap<unknown>(response as unknown as ApiEnvelope<unknown>)
}

/** 把结构绑定按序列改派到新的工序/工站/子工站（已保存的直接改；未保存的带 batchId 从预览取元数据直接新建绑定） */
async function reassignHierarchy(lineId: EntityId, deviceId: EntityId, updates: LineUpHierarchyReassignItem[], batchId?: EntityId) {
  const response = await api.post('LineUp/Import/HierarchyReassign', {
    lineId,
    deviceId,
    batchId: batchId || undefined,
    updates,
  })
  return unwrap<LineUpHierarchyReassignReceipt>(response as unknown as ApiEnvelope<LineUpHierarchyReassignReceipt>)
}

export const lineUpImportGateway: LineUpImportGateway = {
  startPreview,
  startPreviewFromBlock,
  getProgress,
  getLatestPreviewBatch,
  getPreview,
  commit,
  reassignHierarchy,
  rebuildGraphNodeBom,
}

export default {
  ...lineUpImportGateway,
  getWorkspace,
}
