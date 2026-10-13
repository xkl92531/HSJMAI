import type { EntityId } from './types'
import api from '@/api/index'

/**
 * PLC 设备（Basic_Device）基础信息维护网关。
 * 复用 LineUp/MasterData/Device 级别接口：Code=DeviceName（全局唯一）、Name=ProjectName、IpAddress 独立字段。
 */
export interface LineUpDeviceItem {
  id: EntityId
  code: string | null
  name: string | null
  ipAddress: string | null
  lineId: EntityId | null
  remark: string | null
}

export interface LineUpDevicePage {
  total: number
  items: LineUpDeviceItem[]
}

export interface LineUpDeviceWrite {
  code: string
  name?: string | null
  ipAddress?: string | null
  lineId?: EntityId | null
  remark?: string | null
}

export interface LineUpDeviceQueryOptions {
  lineId?: EntityId | null
  keyword?: string
  page?: number
  pageSize?: number
}

interface Envelope<T> { isSuccess: boolean, data?: T, backResult?: T, errorMessage?: string }

function unwrap<T>(value: Envelope<T>): T {
  const data = value.data ?? value.backResult
  if (value.isSuccess === false || data === undefined) {
    throw new Error(value.errorMessage || 'LineUpRequestFailed')
  }
  return data
}

interface DeviceRawItem {
  id: EntityId
  code: string | null
  name: string | null
  ipAddress?: string | null
  lineId?: EntityId | null
  remark?: string | null
}

const gateway = {
  async query(options: LineUpDeviceQueryOptions = {}): Promise<LineUpDevicePage> {
    const response = await api.get('LineUp/MasterData/Device', {
      params: {
        parentId: options.lineId || undefined,
        page: options.page ?? 1,
        pageSize: options.pageSize ?? 50,
        keyword: options.keyword || undefined,
      },
    })
    const page = unwrap(response as unknown as Envelope<LineUpDevicePage>)
    return { total: page.total, items: (page.items ?? []).map(normalize) }
  },
  async create(request: LineUpDeviceWrite): Promise<EntityId> {
    const response = await api.post('LineUp/MasterData/Device', {
      code: request.code,
      name: request.name ?? '',
      isEnabled: true,
      ipAddress: request.ipAddress || undefined,
      lineId: request.lineId || undefined,
      remark: request.remark || undefined,
    })
    return String(unwrap(response as unknown as Envelope<EntityId>))
  },
  async update(id: EntityId, request: LineUpDeviceWrite): Promise<EntityId> {
    const response = await api.put(`LineUp/MasterData/Device/${encodeURIComponent(id)}`, {
      code: request.code,
      name: request.name ?? '',
      isEnabled: true,
      ipAddress: request.ipAddress || undefined,
      lineId: request.lineId || undefined,
      remark: request.remark || undefined,
    })
    return String(unwrap(response as unknown as Envelope<EntityId>))
  },
  async remove(id: EntityId): Promise<boolean> {
    const response = await api.delete(`LineUp/MasterData/Device/${encodeURIComponent(id)}`)
    return unwrap(response as unknown as Envelope<boolean>)
  },
}

function normalize(item: DeviceRawItem): LineUpDeviceItem {
  return {
    id: String(item.id),
    code: item.code,
    name: item.name,
    ipAddress: item.ipAddress ?? null,
    lineId: item.lineId ? String(item.lineId) : null,
    remark: item.remark ?? null,
  }
}

export default gateway
