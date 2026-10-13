import type { EntityId } from './types'
import api from '@/api/index'

export interface LineUpGraphNodeImageItem {
  batchId: string
  lineId: string
  deviceId: string
  blockName: string
  sequenceSourceKey: string
  nodeSourceKey: string
  nodeKind: string
  subStationId: string
  sourceImageId: string
  storageKey: string
  pixelWidth: number
  pixelHeight: number
  contentUrl: string
}

export interface LineUpGraphNodeImagePayload {
  sourceImageId: EntityId
  pixelWidth: number
  pixelHeight: number
  pngBase64: string
}

export interface LineUpGraphNodeImageSaveRequest {
  batchId: EntityId
  lineId: EntityId
  deviceId: EntityId
  blockName: string
  sequenceSourceKey: string
  nodeSourceKey: string
  nodeKind: string
  subStationId: EntityId
  images: LineUpGraphNodeImagePayload[]
}

interface Envelope<T> {
  isSuccess: boolean
  data?: T
  backResult?: T
  errorMessage?: string
}

function unwrap<T>(value: Envelope<T>): T {
  const data = value.data ?? value.backResult
  if (value.isSuccess === false || data === undefined) {
    throw new Error(value.errorMessage || 'LineUpGraphNodeImageRequestFailed')
  }
  return data
}

const graphNodeImageApi = {
  async save(request: LineUpGraphNodeImageSaveRequest): Promise<{ imageCount: number }> {
    const response = await api.post('LineUp/GraphNodeImage/Save', request)
    const result = unwrap(response as unknown as Envelope<{ imageCount: number }>)
    return result
  },

  async listByNode(lineId: EntityId, blockName: string, nodeSourceKey: string): Promise<LineUpGraphNodeImageItem[]> {
    const response = await api.get('LineUp/GraphNodeImage/ByNode', {
      params: { lineId, blockName, nodeSourceKey },
    })
    return unwrap(response as unknown as Envelope<LineUpGraphNodeImageItem[]>)
  },

  async getContent(contentUrl: string): Promise<Blob> {
    const relative = contentUrl.startsWith('/') ? contentUrl.slice(1) : contentUrl
    return await api.get(relative, { responseType: 'blob' }) as unknown as Blob
  },
}

export default graphNodeImageApi