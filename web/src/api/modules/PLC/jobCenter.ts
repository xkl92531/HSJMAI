import api from '@/api/index'

const routerName = 'PlcJobCenter'

export type JobScheduleType = 'interval' | 'daily'
export type JobExecutorType = 'internal' | 'http'

export type JobStationGroup = {
  stationCode: string
  stationName: string
  projectCount: number
  pointCount: number
  tableNames: string[]
}

export default {
  getTasks: () => api.get(`${routerName}/GetTasks`),

  getTaskTemplates: () => api.get(`${routerName}/GetTaskTemplates`),

  getTaskDetail: (taskKey: string) => api.get(`${routerName}/GetTaskDetail`, {
    params: { taskKey },
  }),

  getStationGroups: (projectIdsText?: string) => api.get(`${routerName}/GetStationGroups`, {
    params: { projectIdsText },
  }),

  createTask: (data: {
    taskName: string
    taskType: string
    enabled: boolean
    scheduleType: JobScheduleType
    intervalMilliseconds?: number
    dailyTime?: string
    timeoutMilliseconds?: number
    retryCount?: number
    projectIdsText?: string
    stationGroupCodesJson?: string
    stationMode?: string
    resetCacheBeforeCollect?: boolean
    requestUrl?: string
    httpMethod?: string
    headersJson?: string
    queryJson?: string
    bodyJson?: string
    contentType?: string
  }) => api.post(`${routerName}/CreateTask`, data),

  saveTaskConfig: (data: {
    taskKey: string
    taskName?: string
    enabled: boolean
    scheduleType: JobScheduleType
    intervalMilliseconds?: number
    dailyTime?: string
    timeoutMilliseconds?: number
    retryCount?: number
    projectIdsText?: string
    stationGroupCodesJson?: string
    stationMode?: string
    resetCacheBeforeCollect?: boolean
    requestUrl?: string
    httpMethod?: string
    headersJson?: string
    queryJson?: string
    bodyJson?: string
    contentType?: string
  }) => api.post(`${routerName}/SaveTaskConfig`, data),

  changeTaskState: (data: {
    taskKey: string
    enabled: boolean
  }) => api.post(`${routerName}/ChangeTaskState`, data),

  deleteTask: (data: {
    taskKey: string
  }) => api.post(`${routerName}/DeleteTask`, data),

  runTaskNow: (data: {
    taskKey: string
    useSimulation?: boolean
    autoGenerateSimulationValues?: boolean
  }) => api.post(`${routerName}/RunTaskNow`, data),

  clearTaskCache: (data: {
    taskKey: string
    clearAllProjectCaches?: boolean
  }) => api.post(`${routerName}/ClearTaskCache`, data),
}
