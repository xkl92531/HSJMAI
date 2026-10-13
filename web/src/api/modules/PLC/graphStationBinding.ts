import api from '@/api/index'

/** 单条工站绑定信息（ai_plc_stationblockbinding） */
export interface GraphStationBindingItem {
  /** 绑定行 Id（解绑时回传） */
  bindingId: string
  /** 工站代码（Basic_WorkStation.Code，如 OP010） */
  stationCode: string
  /** 绑定原因（手动绑定 / DerivedFromStationColoring+BlockCall 等） */
  reason: string | null
  /** 归属置信度（手动绑定恒为 1） */
  confidence: number | null
  /** 是否共享块（一块多站） */
  isShared: boolean | null
  /** 绑定时间 */
  boundAt: string | null
}

/** GRAPH 程序块及其工站绑定（bindings 为空 = 未绑定） */
export interface GraphStationBoundBlock {
  blockId: string
  blockName: string
  blockType: string
  blockNumber: number | null
  /** 所属 PLC 项目 Id（ai_plc_project_new.id） */
  plcProjectId: string
  bindings: GraphStationBindingItem[]
}

export interface GraphStationBindSkip { blockName: string, reason: string }

/** 批量绑定结果 */
export interface GraphStationBindResult {
  boundCount: number
  movedCount: number
  updatedBlockCount: number
  skippedCount: number
  skipped: GraphStationBindSkip[]
}

/** 解绑结果 */
export interface GraphStationUnbindResult {
  unboundCount: number
  clearedBlockCount: number
}

export interface GraphStationBindRequest {
  stationCode: string
  blockIds: Array<string | number>
}

export interface GraphStationUnbindRequest {
  bindingIds: Array<string | number>
}

/** 监控目录行（LineUpMonitorBindingSummaryDto）：结构绑定 + 监控状态 */
export interface GraphStationMonitorBinding {
  bindingId: string
  graphAssetId?: string
  publishedRevisionId?: string
  lineId?: string
  deviceId?: string
  stationId?: string
  subStationId?: string
  plcProjectId?: string
  graphSourceName?: string
  bindingStatus?: string
  monitoringEnabled: boolean
  runtimeBindingReady?: boolean
}
const routerName = 'PLC_GraphStationBinding'

interface ApiEnvelope<T> {
  isSuccess: boolean
  data?: T
  backResult?: T
  errorMessage?: string | null
}

function unwrap<T>(response: ApiEnvelope<T>, fallbackError: string): T {
  const result = response?.data ?? response?.backResult
  if (response?.isSuccess === false) {
    throw new Error(response.errorMessage || fallbackError)
  }
  if ((result === undefined || result === null) && response?.isSuccess !== true) {
    throw new Error(response?.errorMessage || fallbackError)
  }
  return result as T
}

export default {
  /**
   * 查询 GRAPH 程序块及其工站绑定（GET /PLC_GraphStationBinding/GetBlocks）
   * projectId 可选（>0 时仅该项目）；按块编号升序
   */
  getBlocks: async (projectId?: string | number): Promise<GraphStationBoundBlock[]> => {
    const res: any = await api.get(`${routerName}/GetBlocks`, {
      params: projectId ? { projectId } : undefined,
    })
    return unwrap<GraphStationBoundBlock[]>(res, 'GraphStationBindingRequestFailed') ?? []
  },

  /**
   * 批量绑定 GRAPH 块到工站（POST /PLC_GraphStationBinding/Bind）
   */
  bind: async (request: GraphStationBindRequest): Promise<GraphStationBindResult> => {
    const res: any = await api.post(`${routerName}/Bind`, request)
    return unwrap<GraphStationBindResult>(res, 'GraphStationBindingRequestFailed')
  },

  /**
   * 按绑定行 Id 解绑（POST /PLC_GraphStationBinding/Unbind）
   */
  unbind: async (request: GraphStationUnbindRequest): Promise<GraphStationUnbindResult> => {
    const res: any = await api.post(`${routerName}/Unbind`, request)
    return unwrap<GraphStationUnbindResult>(res, 'GraphStationBindingRequestFailed')
  },
  /**
   * 监控目录行（GET LineUp/Monitor/Bindings）：已保存结构并发布到子工站的 GRAPH 资产绑定，
   * GraphSourceName 即 GRAPH 块名，用于块级匹配；MonitoringEnabled 标识监控是否已开启。
   */
  listMonitorBindings: async (query: { lineId?: string | number, deviceId?: string | number, stationId?: string | number }): Promise<GraphStationMonitorBinding[]> => {
    const res: any = await api.get('LineUp/Monitor/Bindings', { params: query })
    return unwrap<GraphStationMonitorBinding[]>(res, 'LineUpMonitorRequestFailed') ?? []
  },

  /** 生成运行时绑定（POST LineUp/RuntimeBindings/{id}/Prepare） */
  prepareRuntimeBinding: async (bindingId: string | number): Promise<unknown> => {
    const res: any = await api.post(`LineUp/RuntimeBindings/${bindingId}/Prepare`, undefined, { timeout: 1000 * 180 })
    return unwrap<unknown>(res, 'RuntimeBindingPrepareFailed')
  },

  /** 在线校验运行时绑定（POST LineUp/RuntimeBindings/{id}/Validate，IGW 缺席仅警告；IGW 读回可能超 60s 默认超时，放宽到 3 分钟） */
  validateRuntimeBinding: async (bindingId: string | number): Promise<unknown> => {
    const res: any = await api.post(`LineUp/RuntimeBindings/${bindingId}/Validate`, undefined, { timeout: 1000 * 180 })
    return unwrap<unknown>(res, 'RuntimeBindingValidateFailed')
  },

  /** 开/关实时监控（PUT LineUp/RuntimeBindings/{id}/Monitoring） */
  setRuntimeBindingMonitoring: async (bindingId: string | number, enabled: boolean): Promise<unknown> => {
    const res: any = await api.put(`LineUp/RuntimeBindings/${bindingId}/Monitoring`, { enabled }, { timeout: 1000 * 180 })
    return unwrap<unknown>(res, 'RuntimeBindingMonitoringFailed')
  },
}
