import api from '@/api/index'

const routerName = 'CylinderSignalEvent'

export default {
  collectOnce: (data: {
    projectId: string | number
    stationCode?: string
    stationMode?: string
    resetCacheBeforeCollect?: boolean
    useSimulation?: boolean
    simulatedValues?: Record<string, unknown>
  }) => api.post(`${routerName}/CollectOnce`, data),

  generateSimulationValues: (data: {
    projectId: string | number
    stationCode?: string
  }) => api.post(`${routerName}/GenerateSimulationValues`, data),

  collectAllOnce: (data: {
    stationMode?: string
    resetCacheBeforeCollect?: boolean
    useSimulation?: boolean
    autoGenerateSimulationValues?: boolean
    triggerSource?: string
  }) => api.post(`${routerName}/CollectAllOnce`, data),

  getJobConfig: () => api.get(`${routerName}/GetJobConfig`),

  saveJobConfig: (data: {
    enabled: boolean
    intervalMilliseconds: number
    intervalSeconds?: number
    stationMode?: string
    resetCacheBeforeCollect?: boolean
    lastRunAt?: string
    lastRunStatus?: string
    lastMessage?: string
    lastProjectCount?: number
    lastChangedCount?: number
    lastInsertedCount?: number
  }) => api.post(`${routerName}/SaveJobConfig`, data),

  list: (params: {
    page?: number
    limit?: number
    projectId?: string | number
    plcId?: string
    stationCode?: string
    cylinderId?: string
    signalName?: string
    signalRole?: string
    from?: string
    to?: string
  }) => api.get(`${routerName}/GetListPaged`, {
    params: {
      page: params.page || 1,
      limit: params.limit || 20,
      ...params,
    },
  }),
}
