import api from '@/api/index'

const routerName = 'PLC_AlarmVariable'

export default {
  list: (params: {
    page: number
    limit: number
    projectId?: number | string
    keyword?: string
    category?: string
    level?: string
    monitorEnabled?: boolean
    manuallyConfirmed?: boolean
    collectable?: boolean
  }) => api.get(`${routerName}/GetListPaged`, { params }),

  insert: (data: any) => api.post(`${routerName}/Insert`, data, {}),

  update: (data: any) => api.post(`${routerName}/UpdateById/${data.id}`, data, {}),

  confirm: (id: number | string, confirmed: boolean) =>
    api.post(`${routerName}/Confirm/${id}`, null, { params: { confirmed } }),

  setMonitor: (ids: any[], enabled: boolean) =>
    api.post(`${routerName}/SetMonitor`, { ids, enabled }),

  setPush: (ids: any[], enabled: boolean) => api.post(`${routerName}/SetPush`, { ids, enabled }),

  remove: (id: number | string) => api.delete(`${routerName}/Delete/${id}`, {}),

  removeBatch: (ids: any[]) => api.delete(`${routerName}/DeleteBatch`, { data: ids }),

  rerunRecognition: (projectId: number | string) => api.post(`${routerName}/RerunRecognition/${projectId}`),
}
