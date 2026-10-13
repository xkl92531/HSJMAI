import api from '@/api/index'

const routerName = 'OU'

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
  details: data => api.get(`${routerName}/getList`, {
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

  getTreeList: (data: any) => api.get(`${routerName}/GetTreeList`, { params: data }),
  getSort: (id: any) => api.get(`${routerName}/GetSort?ouId=${id}`, { }),
  syncOU: (id: any) => api.post(`${routerName}/SyncOU/${id}`, {}, {

  }),
  syncEmp: (id: any) => api.post(`${routerName}/SyncEmp/${id}`, {}, {

  }),
  getCurrentOUList: () => api.post(`${routerName}/GetCurrentOUList`, {}, {

  }),
  getUsersByDepart: (data: any) => api.post(`employee/GetUsersByDepart`, data, {

  }),

  sendDingMsg: (data: any) => api.post(`employee/SendDingMsg`, data, {

  }),
  sendEmailMsg: (data: any) => api.post(`employee/SendEmailMsg`, data, {

  }),
  getAllCompanyCodeDepart: () => api.get(`employee/GetAllCompanyCodeDepart`, { params: {} }),

  getTreeList2: (data: any) => api.get(`${routerName}/GetTreeList2`, { params: data }),

  getAllNameByDeptID: (deptId: string | number) => api.get(`${routerName}/GetAllNameByDeptID?deptId=${deptId}`, { }),

  resetDefaultOU: () => api.get(`${routerName}/ResetDefaultOU`, { }),
}
