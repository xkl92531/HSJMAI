import api from '../../index'

export default {
  // 分页列表
  list: (data: { page?: number, limit?: number, sortField?: string, sortType?: string }) =>
    api.get('Tool_Definition/GetListPaged', { params: data }),

  // 不分页列表
  listAll: (data?: any) =>
    api.get('Tool_Definition/GetList', { params: data }),

  // 获取详情
  detail: (id: number | string) =>
    api.get('Tool_Definition/Get', { params: { id } }),

  // 新增
  create: (data: any) =>
    api.post('Tool_Definition/Insert', data),

  // 修改
  edit: (id: number | string, data: any) =>
    api.post(`Tool_Definition/UpdateById/${id}`, data),

  // 删除
  delete: (id: number | string) =>
    api.delete(`Tool_Definition/Delete/${id}`),

  // 批量删除
  batchDelete: (ids: (number | string)[]) =>
    api.delete('Tool_Definition/DeleteBatch', { data: ids }),

  // 导出Excel
  exportExcel: (data?: any) =>
    api.get('Tool_Definition/ExportToExcel', { params: data }),
}
