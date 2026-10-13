import type { EntityId } from './types'
import api from '@/api/index'

export interface LineUpImageHierarchy {
  id: EntityId
  code: string | null
  name: string | null
  workStationId: EntityId
  workStationCode: string | null
  workStationName: string | null
  processId: EntityId
  processCode: string | null
  processName: string | null
  productionLineId: EntityId
  productionLineCode: string | null
  productionLineName: string | null
}

export interface LineUpImageItem {
  id: EntityId
  subStationId: EntityId
  pictureName: string | null
  contentUrl: string | null
  currentContentUrl: string | null
  contentSha256: string | null
  byteLength: number | null
  pixelWidth: number | null
  pixelHeight: number | null
  detectedMimeType: string | null
  rowVersion: number
}

export interface LineUpImageBom {
  id: EntityId
  stationId: EntityId | null
  refNo: string | null
  name: string | null
  tagCode: string | null
  dbAddress: string | null
  modelNo: string | null
  brand: string | null
  ioAddress: string | null
  partNo: string | null
  plcId: EntityId | null
  plcName: string | null
  plcIp: string | null
  isAssociated: boolean | null
}

export interface LineUpPlcDevice {
  id: EntityId
  code: string | null
  name: string | null
  ip: string | null
}

export interface BomWritePayload {
  refNo?: string | null
  tagCode?: string | null
  name?: string | null
  dbAddress?: string | null
  modelNo?: string | null
  brand?: string | null
  ioAddress?: string | null
  partNo?: string | null
  plcId?: EntityId | null
}

export interface LineUpMarkerPoint {
  id: string | null
  x: number | null
  y: number | null
  description: string | null
}

export interface LineUpMarkerBillboard {
  id: string | null
  x: number | null
  y: number | null
  width: number | null
  height: number | null
  text: string | null
  description: string | null
  combinedBomId: string | null
  backgroundColor: string | null
  borderColor: string | null
}

export interface LineUpMarkerLine {
  id: string | null
  startX: number | null
  startY: number | null
  endX: number | null
  endY: number | null
  markId: string | null
  billboardId: string | null
  combinedBomId: string | null
}

export interface LineUpImageMarker {
  id: EntityId
  imageId: EntityId
  markType: string | null
  isSingleMark: boolean
  point: LineUpMarkerPoint | null
  billboard: LineUpMarkerBillboard | null
  line: LineUpMarkerLine | null
  bomIds: EntityId[]
  issues: Array<{ code: string, field: string }>
}

export interface LineUpImageWorkspace {
  subStation: LineUpImageHierarchy
  images: LineUpImageItem[]
  boms: LineUpImageBom[]
  markers: LineUpImageMarker[]
}

export interface LineUpFaultLocation {
  boms: LineUpImageBom[]
  images: LineUpImageItem[]
  markers: LineUpImageMarker[]
}

export interface LineUpImageMarkerWrite {
  id: EntityId | null
  markType: string | null
  isSingleMark: boolean
  point: LineUpMarkerPoint | null
  billboard: LineUpMarkerBillboard | null
  line: LineUpMarkerLine | null
  bomIds: EntityId[]
}

export interface LineUpImageSaveResult {
  id: EntityId
  rowVersion: number
  currentContentUrl: string | null
}

export interface BomImportPreviewRow {
  rowIndex: number
  refNo: string | null
  name: string | null
  tagCode: string | null
  dbAddress: string | null
  modelNo: string | null
  brand: string | null
  partNo: string | null
  status: 'create' | 'update' | 'error'
  errorCodes: string[]
}

export interface BomImportPreviewResult {
  totalRows: number
  createCount: number
  updateCount: number
  errorCount: number
  rows: BomImportPreviewRow[]
}

export interface BomImportCommitRow {
  rowIndex: number
  refNo: string | null
  name: string | null
  tagCode: string | null
  dbAddress: string | null
  modelNo: string | null
  brand: string | null
  partNo: string | null
}

export interface BomImportCommitPayload {
  productionLineId: EntityId
  plcId: EntityId
  rows: BomImportCommitRow[]
}

export interface BomImportCommitResult {
  createdCount: number
  updatedCount: number
  failedRows: Array<{ rowIndex: number, errorCodes: string[] }>
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
    throw new Error(value.errorMessage || 'LineUpImageMarkRequestFailed')
  }
  return data
}

const imageMarkApi = {
  async getStation(subStationId: EntityId, bomKeyword?: string): Promise<LineUpImageWorkspace> {
    const response = await api.get(`LineUp/ImageMarks/Stations/${encodeURIComponent(subStationId)}`, {
      params: { bomKeyword: bomKeyword || undefined },
    })
    return unwrap(response as unknown as Envelope<LineUpImageWorkspace>)
  },

  async upload(subStationId: EntityId, file: File): Promise<LineUpImageItem> {
    const form = new FormData()
    form.append('subStationId', subStationId)
    form.append('file', file, file.name)
    const response = await api.post('LineUp/ImageMarks/Images', form)
    return unwrap(response as unknown as Envelope<LineUpImageItem>)
  },

  async save(
    imageId: EntityId,
    rowVersion: number,
    markersJson: string,
    renderedImage?: File,
  ): Promise<LineUpImageSaveResult> {
    const form = new FormData()
    form.append('rowVersion', String(rowVersion))
    form.append('markersJson', markersJson)
    if (renderedImage) {
      form.append('renderedImage', renderedImage, renderedImage.name)
    }
    const response = await api.put(`LineUp/ImageMarks/Images/${encodeURIComponent(imageId)}`, form)
    return unwrap(response as unknown as Envelope<LineUpImageSaveResult>)
  },

  async remove(imageId: EntityId): Promise<boolean> {
    const response = await api.delete(`LineUp/ImageMarks/Images/${encodeURIComponent(imageId)}`)
    return unwrap(response as unknown as Envelope<boolean>)
  },

  async createBom(subStationId: EntityId, payload: BomWritePayload): Promise<LineUpImageBom> {
    const response = await api.post(`LineUp/ImageMarks/Boms/${encodeURIComponent(subStationId)}`, payload)
    return unwrap(response as unknown as Envelope<LineUpImageBom>)
  },

  async updateBom(bomId: EntityId, payload: BomWritePayload): Promise<LineUpImageBom> {
    const response = await api.put(`LineUp/ImageMarks/Boms/${encodeURIComponent(bomId)}`, payload)
    return unwrap(response as unknown as Envelope<LineUpImageBom>)
  },

  async removeBom(bomId: EntityId): Promise<boolean> {
    const response = await api.delete(`LineUp/ImageMarks/Boms/${encodeURIComponent(bomId)}`)
    return unwrap(response as unknown as Envelope<boolean>)
  },

  async setBomAssociation(bomId: EntityId, associated: boolean): Promise<boolean> {
    const response = await api.put(`LineUp/ImageMarks/Boms/${encodeURIComponent(bomId)}/Association`, null, {
      params: { associated },
    })
    return unwrap(response as unknown as Envelope<boolean>)
  },

  async listPlcs(productionLineId?: EntityId): Promise<LineUpPlcDevice[]> {
    const response = await api.get('LineUp/ImageMarks/Plcs', {
      params: { productionLineId: productionLineId || undefined },
    })
    return unwrap(response as unknown as Envelope<LineUpPlcDevice[]>)
  },

  async importBomPreview(productionLineId: EntityId, file: File): Promise<BomImportPreviewResult> {
    const form = new FormData()
    form.append('productionLineId', String(productionLineId))
    form.append('file', file, file.name)
    const response = await api.post('LineUp/ImageMarks/Boms/Import/Preview', form)
    return unwrap(response as unknown as Envelope<BomImportPreviewResult>)
  },

  async importBomCommit(payload: BomImportCommitPayload): Promise<BomImportCommitResult> {
    const response = await api.post('LineUp/ImageMarks/Boms/Import/Commit', payload)
    return unwrap(response as unknown as Envelope<BomImportCommitResult>)
  },

  async downloadBomTemplate(): Promise<void> {
    const blob = await api.get(encodeURIComponent('BOM物料模板.xlsx'), { responseType: 'blob' }) as unknown as Blob
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = 'BOM物料模板.xlsx'
    link.click()
    URL.revokeObjectURL(url)
  },

  async getContent(contentUrl: string): Promise<Blob> {
    return await api.get(contentUrl.replace(/^\//, ''), { responseType: 'blob' }) as unknown as Blob
  },

  async getFaultLocation(tagCode?: string, bomId?: EntityId): Promise<LineUpFaultLocation> {
    const response = await api.get('LineUp/ImageMarks/FaultLocation', {
      params: { tagCode: tagCode || undefined, bomId: bomId || undefined },
    })
    return unwrap(response as unknown as Envelope<LineUpFaultLocation>)
  },
}

export default imageMarkApi
