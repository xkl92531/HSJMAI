import api from '@/api/index'

const routerName = 'StopCauseDiagnosis'

/** FB/FC 内部局部变量 → 全局符号路径 的映射（LAD 梯形图展示用） */
export type FbLocalSymbolMap = Record<string, string>

/**
 * 批量具体化 FB/FC 内部局部变量（如 S_AutoRunMode → IDB150_xxx.S_AutoRunMode）。
 * 返回的 map 只含可唯一具体化的项，失败路径不返回（调用方按原样显示）。
 */
export async function concretizeFbLocals(
    projectId: number | string,
    blockName: string,
    localPaths: string[],
): Promise<FbLocalSymbolMap> {
    const response = await api.post(routerName + '/concretize-fb-locals', {
        projectId,
        blockName,
        localPaths,
    })
    const payload = response as { data?: FbLocalSymbolMap }
    return payload.data && typeof payload.data === 'object' ? payload.data : {}
}
