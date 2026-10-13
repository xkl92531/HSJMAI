import api from '@/api/index'

const routerName = 'test1'

export default {
  list: (data: {
    title?: string
    from: number
    limit: number
  }) => api.get(`${routerName}/getListPaged`, {
    params: data,

  }),
  getFirst: (data: any) => api.get(`${routerName}/getFirst`, {
    params: data,
  }),
  detail: (id: number | string) => api.get(`${routerName}/get`, {
    params: {
      id,
    },

  }),
  details: (data: any) => api.get(`${routerName}/getList`, {
    params: data,

  }),
  create: (data: any) => api.post(`${routerName}/insert`, data, {

  }),

  edit: (data: any) => api.post(`${routerName}/updateById/${data.id}`, data, {

  }),

  delete: (id: number | string) => api.delete(`${routerName}/delete/${id}`, {

  }),

  batchDelete: (ids: any) => api.delete(`${routerName}/deleteBatch`, {
    data: ids,
  }),
  exportToExcel: (data: any) => api.get(`${routerName}/exportToExcel`, {
    params: data,

  }),
}
