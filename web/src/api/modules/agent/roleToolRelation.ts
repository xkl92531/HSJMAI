import api from '../../index'

export default {
  // 分页列表
  list: (data: { page?: number, limit?: number, sortField?: string, sortType?: string, roleID?: number | string }) =>
    api.get('Role_Tool_Relation/GetListPaged', { params: data }),

  // 不分页列表
  listAll: (data?: any) =>
    api.get('Role_Tool_Relation/GetList', { params: data }),

  // 获取详情
  detail: (id: number | string) =>
    api.get('Role_Tool_Relation/Get', { params: { id } }),

  // 新增
  create: (data: any) =>
    api.post('Role_Tool_Relation/Insert', data),

  // 修改
  edit: (id: number | string, data: any) =>
    api.post(`Role_Tool_Relation/UpdateById/${id}`, data),

  // 删除
  delete: (id: number | string) =>
    api.delete(`Role_Tool_Relation/Delete/${id}`),

  // 批量删除
  batchDelete: (ids: (number | string)[]) =>
    api.delete('Role_Tool_Relation/DeleteBatch', { data: ids }),

  // 获取角色已授权的工具ID列表
  getToolIdsByRoleId: (roleId: number | string) =>
    api.get('Role_Tool_Relation/GetToolIdsByRoleId', { params: { roleId } }),

  // 批量更新角色工具授权
  saveToolAuth: (roleId: number | string, toolIds: (number | string)[]) =>
    api.post('Role_Tool_Relation/SaveToolAuth', { roleId, toolIds }),
}
