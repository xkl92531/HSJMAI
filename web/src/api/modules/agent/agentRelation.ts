import api from '../../index'

export default {
    // 获取智能体的下属智能体ID列表
    getSubordinateIdsByPlannerRoleId: (plannerRoleId: number | string) =>
        api.get('Agent_Relation/GetSubordinateIdsByPlannerRoleId', { params: { plannerRoleId } }),

    // 批量更新Planner的下属智能体
    saveSubordinates: (plannerRoleId: number | string, subordinateRoleIds: (number | string)[]) =>
        api.post('Agent_Relation/SaveSubordinates', { plannerRoleId, subordinateRoleIds }),

    // 分页列表
    list: (data: { page?: number, limit?: number, parentRoleId?: number | string }) =>
        api.get('Agent_Relation/GetListPaged', { params: data }),

    // 删除
    delete: (id: number | string) =>
        api.delete(`Agent_Relation/Delete/${id}`),
}
