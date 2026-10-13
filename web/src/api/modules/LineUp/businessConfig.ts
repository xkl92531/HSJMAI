import type { EntityId } from './types'
import api from '@/api/index'

export type LineUpBusinessKind = 'Action' | 'Condition' | 'Solution'

export interface LineUpBusinessItem {
  id: EntityId
  kind: LineUpBusinessKind
  parentId: EntityId
  subStationId: EntityId
  code: string | null
  name: string | null
  value: string | null
  isEnabled: boolean
}

export interface LineUpBusinessPage { total: number, items: LineUpBusinessItem[] }

export interface LineUpBusinessWrite {
  id?: EntityId
  parentId: EntityId
  subStationId: EntityId
  code: string
  name: string
  value?: string | null
  isEnabled: boolean
}

export interface LineUpBusinessGateway {
  query: (kind: LineUpBusinessKind, parentId: EntityId, options?: { page?: number, pageSize?: number, keyword?: string }) => Promise<LineUpBusinessPage>
  create: (kind: LineUpBusinessKind, request: LineUpBusinessWrite) => Promise<EntityId>
  update: (kind: LineUpBusinessKind, id: EntityId, request: LineUpBusinessWrite) => Promise<EntityId>
  remove: (kind: LineUpBusinessKind, id: EntityId) => Promise<boolean>
}

interface Envelope<T> { isSuccess: boolean, data?: T, backResult?: T, errorMessage?: string }

function unwrap<T>(value: Envelope<T>): T {
  const data = value.data ?? value.backResult
  if (value.isSuccess === false || data === undefined) {
    throw new Error(value.errorMessage || 'LineUpRequestFailed')
  }
  return data
}

const gateway: LineUpBusinessGateway = {
  async query(kind, parentId, options = {}) {
    const response = await api.get(`LineUp/BusinessConfig/${kind}`, {
      params: { parentId, page: options.page ?? 1, pageSize: options.pageSize ?? 50, keyword: options.keyword || undefined },
    })
    return unwrap(response as unknown as Envelope<LineUpBusinessPage>)
  },
  async create(kind, request) {
    const response = await api.post(`LineUp/BusinessConfig/${kind}`, request)
    return String(unwrap(response as unknown as Envelope<EntityId>))
  },
  async update(kind, id, request) {
    const response = await api.put(`LineUp/BusinessConfig/${kind}/${encodeURIComponent(id)}`, request)
    return String(unwrap(response as unknown as Envelope<EntityId>))
  },
  async remove(kind, id) {
    const response = await api.delete(`LineUp/BusinessConfig/${kind}/${encodeURIComponent(id)}`)
    return unwrap(response as unknown as Envelope<boolean>)
  },
}

export default gateway
