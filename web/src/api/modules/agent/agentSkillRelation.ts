import api from '../../index'

export default {
    // 获取角色已授权的技能ID列表
    getSkillIdsByRoleId: (roleId: number | string) =>
        api.get('Agent_Skill_Relation/GetSkillIdsByRoleId', { params: { roleId } }),

    // 批量更新角色技能授权
    saveSkillAuth: (roleId: number | string, skillIds: (number | string)[]) =>
        api.post('Agent_Skill_Relation/SaveSkillAuth', { roleId, skillIds }),

    // 分页列表
    list: (data: { page?: number, limit?: number, roleId?: number | string }) =>
        api.get('Agent_Skill_Relation/GetListPaged', { params: data }),

    // 删除
    delete: (id: number | string) =>
        api.delete(`Agent_Skill_Relation/Delete/${id}`),
}
