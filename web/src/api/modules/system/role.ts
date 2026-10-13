import api from '@/api/index'

const routerName = 'SysRole'

export default {
  list: (data: { title?: string, from: number, limit: number }) =>
    api.get(`${routerName}/getListPaged`, {
      params: data,
    }),

  detail: (id: number | string) =>
    api.get(`${routerName}/get`, {
      params: {
        id,
      },
    }),
  details: data =>
    api.get(`${routerName}/getList`, {
      params: data,
    }),
  create: (data: any) => api.post(`${routerName}/Insert`, data, {}),

  edit: (data: any) =>
    api.post(`${routerName}/UpdateById/${data.id}`, data, {}),

  delete: (id: number | string) => api.delete(`${routerName}/delete/${id}`, {}),

  batchDelete: (ids: any) =>
    api.delete(`${routerName}/deleteBatch`, {
      data: ids,
    }),

  saveRole: (data: any) => api.post(`SysRoleMenu/SaveRole`, data, { headers: { loading: true } }),

  updateStatus: (data: any) => api.post(`${routerName}/UpdateStatus`, data, {}),
  getUserListPageByRole: (data: { title?: string, from: number, limit: number }) =>
    api.get(`${routerName}/GetUserListPageByRole`, {
      params: data,
    }),
  saveUser: (data: any) => api.post(`${routerName}/SaveUser`, data, {}),

  deleteUser: (data: any) => api.post(`${routerName}/DeleteUser`, data, {}),

}
