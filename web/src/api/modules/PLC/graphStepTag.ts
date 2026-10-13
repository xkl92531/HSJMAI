import api from '@/api/index'

const routerName = 'PLC_GraphStepTag'

export default {
    /** 按项目获取注册汇总（总体统计 + 按 DB 分组） */
    summary: (projectId: number | string) =>
        api.get(routerName + '/GetRegistrationSummary/' + projectId),

    /** 按 DB 获取台账明细行 */
    rowsByDb: (projectId: number | string, dbName: string) =>
        api.get(routerName + '/GetRowsByDb', {
            params: { projectId, dbName },
        }),

    /** 按项目 + 步号取台账行（fbName 非空时只取该 GRAPH FB 对应实例 DB） */
    rowsByStep: (projectId: number | string, stepNumber: number, fbName?: string, limit = 50) =>
        api.get(routerName + '/GetRowsByStep', {
            params: { projectId, stepNumber, fbName: fbName || undefined, limit },
        }),

    /** 手动触发重新注册 */
    retry: (projectId: number | string) =>
        api.post(routerName + '/RetryRegistration/' + projectId),
}

/** GRAPH 步位台账行（ai_plc_graphsteptag，前端消费字段的子集；后端以 camelCase 序列化） */
export interface PlcGraphStepTagRow {
    id: string | number
    dbName?: string
    fbName?: string
    symbol: string
    regStatus?: string
    stepNumber?: number
    bitIndex?: number
    verifyDetail?: string
    verifyTime?: string
}

/** 按项目 + 步号取台账行（解包后的行数组；业务失败时公共拦截器已 reject） */
export async function getStepTagRowsByStep(projectId: number | string, stepNumber: number, fbName?: string, limit = 50): Promise<PlcGraphStepTagRow[]> {
    const response = await api.get(routerName + '/GetRowsByStep', {
        params: { projectId, stepNumber, fbName: fbName || undefined, limit },
    })
    const payload = response as { data?: PlcGraphStepTagRow[] }
    return Array.isArray(payload.data) ? payload.data : []
}
