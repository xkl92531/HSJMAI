import api from '@/api/index'

const routerName = 'PLC_VariableAccess'

export default {
  list: (data: { page: number, limit: number, ProjectName?: string }) =>
    api.get(`${routerName}/GetListPaged`, {
      params: data,
    }),

  detail: (id: number | string) =>
    api.get(`${routerName}/Get`, {
      params: {
        id,
      },
    }),

  create: (data: any) => api.post(`${routerName}/Insert`, data, {}),

  edit: (data: any) =>
    api.post(`${routerName}/UpdateById/${data.id}`, data, {}),

  delete: (id: number | string) => api.delete(`${routerName}/Delete/${id}`, {}),

  batchDelete: (ids: any) =>
    api.delete(`${routerName}/DeleteBatch`, {
      data: ids,
    }),

  export: (data: any) =>
    api.get(`${routerName}/ExportToExcel`, {
      params: data,
    }),

  import: (data: FormData) =>
    api.post(`${routerName}/import`, data, {
      headers: {
        'Content-Type': 'multipart/form-data',
      },
    }),

  analyzeTagAttributes: (projectId: any) =>
    api.post(`${routerName}/AnalyzeTagAttributes/${projectId}`),


}
