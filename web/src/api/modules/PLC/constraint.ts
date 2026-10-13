import api from '@/api/index'

const routerName = 'PLC_IOConstraint'

export default {
  /** 分页查询约束规则（含信号明细） */
  list: (params: any) =>
    api.get(`${routerName}/GetListPaged`, {
      params: {
        projectId: params.projectId,
        page: params.page || 1,
        limit: params.limit || 20,
        ruleType: params.ruleType || undefined,
        equipmentId: params.equipmentId || undefined,
      },
    }),

  /** 获取约束规则统计摘要 */
  summary: (projectId: number | string) =>
    api.get(`${routerName}/GetSummary`, { params: { projectId } }),

  /** 单信号约束分析 */
  analyzeSignal: (ioTagId: number | string) =>
    api.post(`${routerName}/AnalyzeSignal/${ioTagId}`),

  /** 删除约束规则 */
  delete: (id: number | string) =>
    api.delete(`${routerName}/Delete/${id}`),

  /** 模拟约束违规（基于真实数据构造违规场景） */
  simulateViolations: (params: {
    projectId: number | string
    stationCode?: string
    maxViolationsPerOperator?: number
    seed?: number
  }) =>
    api.get(`${routerName}/SimulateViolations`, { params }),

  /** 获取约束生成系统提示词 */
  getPrompt: () => api.get(`${routerName}/GetPrompt`),

  /** 保存约束生成系统提示词 */
  savePrompt: (content: string) =>
    api.post(`${routerName}/SavePrompt`, { content }),

  /** 恢复约束生成系统提示词为默认 */
  resetPrompt: () => api.post(`${routerName}/ResetPrompt`),

  /** 手动新增约束规则（含信号） */
  createRule: (data: any) => api.post(`${routerName}/Insert`, data),

  /** 编辑约束规则（含信号替换） */
  updateRule: (id: number | string, data: any) =>
    api.post(`${routerName}/UpdateById/${id}`, data),
}
