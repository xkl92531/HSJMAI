import type { EntityId } from './types'
import api from '@/api/index'

export type LineUpMasterLevel = 'ProductionLine' | 'Process' | 'WorkStation' | 'SubStation'

export interface LineUpMasterItem {
  id: EntityId
  level: LineUpMasterLevel
  parentId: EntityId | null
  code: string | null
  name: string | null
  isEnabled: boolean
  plcId?: EntityId | null
  plcName?: string | null
  projectName?: string | null
  remark?: string | null
}

export interface LineUpMasterPage {
  total: number
  items: LineUpMasterItem[]
}

export interface LineUpMasterWrite {
  parentId?: EntityId | null
  lineId?: EntityId | null
  code: string
  name: string
  isEnabled: boolean
  plcId?: EntityId | null
  remark?: string | null
}

export interface LineUpMasterQueryOptions {
  page?: number
  pageSize?: number
  keyword?: string
}

export interface LineUpMasterGateway {
  query: (level: LineUpMasterLevel, parentId?: EntityId, options?: LineUpMasterQueryOptions) => Promise<LineUpMasterPage>
  create: (level: LineUpMasterLevel, request: LineUpMasterWrite) => Promise<EntityId>
  update: (level: LineUpMasterLevel, id: EntityId, request: LineUpMasterWrite) => Promise<EntityId>
  remove: (level: LineUpMasterLevel, id: EntityId) => Promise<boolean>
}

interface Envelope<T> { isSuccess: boolean, data?: T, backResult?: T, errorMessage?: string }

function unwrap<T>(value: Envelope<T>): T {
  const data = value.data ?? value.backResult
  if (value.isSuccess === false || data === undefined) {
    throw new Error(value.errorMessage || 'LineUpRequestFailed')
  }
  return data
}

const gateway: LineUpMasterGateway = {
  async query(level, parentId, options = {}) {
    const response = await api.get(`LineUp/MasterData/${level}`, {
      params: {
        parentId,
        page: options.page ?? 1,
        pageSize: options.pageSize ?? 50,
        keyword: options.keyword || undefined,
      },
    })
    return unwrap(response as unknown as Envelope<LineUpMasterPage>)
  },
  async create(level, request) {
    const response = await api.post(`LineUp/MasterData/${level}`, request)
    return String(unwrap(response as unknown as Envelope<EntityId>))
  },
  async update(level, id, request) {
    const response = await api.put(`LineUp/MasterData/${level}/${encodeURIComponent(id)}`, request)
    return String(unwrap(response as unknown as Envelope<EntityId>))
  },
  async remove(level, id) {
    const response = await api.delete(`LineUp/MasterData/${level}/${encodeURIComponent(id)}`)
    return unwrap(response as unknown as Envelope<boolean>)
  },
}

export default gateway
