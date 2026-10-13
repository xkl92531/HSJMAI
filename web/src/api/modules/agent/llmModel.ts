import api from '../../index'

export default {
  // 分页列表
  list: (data: { page?: number, limit?: number, sortField?: string, sortType?: string }) =>
    api.get('LLM_Model/GetListPaged', { params: data }),

  // 不分页列表
  listAll: (data?: any) =>
    api.get('LLM_Model/GetList', { params: data }),

  // 获取详情
  detail: (id: number | string) =>
    api.get('LLM_Model/Get', { params: { id } }),

  // 新增
  create: (data: any) =>
    api.post('LLM_Model/Insert', data),

  // 修改
  edit: (id: number | string, data: any) =>
    api.post(`LLM_Model/UpdateById/${id}`, data),

  // 删除
  delete: (id: number | string) =>
    api.delete(`LLM_Model/Delete/${id}`),

  // 批量删除
  batchDelete: (ids: (number | string)[]) =>
    api.delete('LLM_Model/DeleteBatch', { data: ids }),

  // 导出Excel
  exportExcel: (data?: any) =>
    api.get('LLM_Model/ExportToExcel', { params: data }),

  // 快速测试模型
  quickTest: (modelId: number | string, data: { message: string, systemPrompt?: string, imageUrls?: string[] }) =>
    api.post(`LLM_Model/QuickTest?modelId=${modelId}`, data),
}
