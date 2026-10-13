import api from '../../index'

export default {
  // 分页列表 - 技能定义
  list: (data: { page?: number, limit?: number, sortField?: string, sortType?: string }) =>
    api.get('Skill_Definition/GetListPaged', { params: data }),

  // 不分页列表 - 获取所有已启用且已批准的技能
  listAll: (data?: { isEnabled?: boolean, approvalStatus?: string }) =>
    api.get('Skill_Definition/GetList', { params: data }),

  // 获取详情
  detail: (id: number | string) =>
    api.get('Skill_Definition/Get', { params: { id } }),

  // 新增
  create: (data: any) =>
    api.post('Skill_Definition/Insert', data),

  // 修改
  edit: (id: number | string, data: any) =>
    api.post(`Skill_Definition/UpdateById/${id}`, data),

  // 删除
  delete: (id: number | string) =>
    api.delete(`Skill_Definition/Delete/${id}`),

  // 批量删除
  batchDelete: (ids: (number | string)[]) =>
    api.delete('Skill_Definition/DeleteBatch', { data: ids }),

  // 审批技能
  approve: (id: number | string, status: 'Approved' | 'Rejected') =>
    api.post(`Skill_Definition/UpdateApprovalStatus`, { id, approvalStatus: status }),
}
