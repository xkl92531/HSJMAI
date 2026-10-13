import api from '@/api/index'

const routerName = 'SysMenu'

export default {
  list: (data: {
    title?: string
    from: number
    limit: number
  }) => api.get(`${routerName}/getListPaged`, {
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

  getTreeChildList: () => api.get(`${routerName}/GetTreeChildList`, { }),

  getTreetList: (data: any) => api.post(`${routerName}/GetTreetList`, data),

  getEntity: (id: number | string) => api.get(`${routerName}/GetEntity`, {
    params: {
      id,
    },
  }),

  getSort: (data: any) => api.post(`${routerName}/GetSort?&systemId=${data.systemId}&menuId=${data.menuId}`, data),
  getTreeMenuListByRoleId: (data: any) => api.post(`${routerName}/GetTreeMenuListByRoleId`, data),
  getMenuListByRole: (data: any) => api.post(`${routerName}/GetMenuListByRole`, data, { headers: { loading: true } }),
}
