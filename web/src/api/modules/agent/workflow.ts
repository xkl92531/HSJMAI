import api from '../../index'

export default {
  // 分页列表
  list: (data: { page?: number, limit?: number, sortField?: string, sortType?: string, status?: number }) =>
    api.get('Agent_Workflow/GetListPaged', { params: data }),

  // 不分页列表
  listAll: (data?: any) =>
    api.get('Agent_Workflow/GetList', { params: data }),

  // 获取详情（基础）
  detail: (id: number | string) =>
    api.get('Agent_Workflow/Get', { params: { id } }),

  // 获取详情（含任务列表）
  getDetail: (id: number | string) =>
    api.get(`Agent_Workflow/GetDetail/${id}`),

  // 获取任务列表
  getTasks: (id: number | string) =>
    api.get(`Agent_Workflow/GetTasks/${id}`),

  // 新增
  create: (data: any) =>
    api.post('Agent_Workflow/Insert', data),

  // 修改
  edit: (id: number | string, data: any) =>
    api.post(`Agent_Workflow/UpdateById/${id}`, data),

  // 删除
  delete: (id: number | string) =>
    api.delete(`Agent_Workflow/Delete/${id}`),

  // 批量删除
  batchDelete: (ids: (number | string)[]) =>
    api.delete('Agent_Workflow/DeleteBatch', { data: ids }),

  // 导出Excel
  exportExcel: (data?: any) =>
    api.get('Agent_Workflow/ExportToExcel', { params: data }),
}
