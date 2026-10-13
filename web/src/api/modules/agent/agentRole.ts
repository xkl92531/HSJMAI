import api from '../../index'

export default {
  // 分页列表
  list: (data: { page?: number, limit?: number, sortField?: string, sortType?: string }) =>
    api.get('Agent_Role/GetListPaged', { params: data }),

  // 不分页列表
  listAll: (data?: any) =>
    api.get('Agent_Role/GetList', { params: data }),

  // 获取详情
  detail: (id: number | string) =>
    api.get('Agent_Role/Get', { params: { id } }),

  // 新增
  create: (data: any) =>
    api.post('Agent_Role/Insert', data),

  // 修改
  edit: (id: number | string, data: any) =>
    api.post(`Agent_Role/UpdateById/${id}`, data),

  // 删除
  delete: (id: number | string) =>
    api.delete(`Agent_Role/Delete/${id}`),

  // 批量删除
  batchDelete: (ids: (number | string)[]) =>
    api.delete('Agent_Role/DeleteBatch', { data: ids }),

  // 导出Excel
  exportExcel: (data?: any) =>
    api.get('Agent_Role/ExportToExcel', { params: data }),
}
