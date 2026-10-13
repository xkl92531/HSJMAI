import api from '@/api/index'

/** GRAPH 程序块解析溯源段（source，来自 ai_plc_block_new） */
export interface PlcGraphBlockSource {
    /** 关联的 PLC 项目 Id（ai_plc_block_new.projectid） */
    plcProjectId: number | string
    /** 程序块 Id（ai_plc_block_new.id，本次解析生成） */
    plcBlockId: number | string
    /** 本次上传解析任务ID（plc_upload_{projectId}_{时间戳}_{GUID}，溯源用） */
    parseRunId: string | null
    /** GRAPH 内容 SHA256（graph-v1|块名|各GRAPH类LogicUnit拼接） */
    sourceHash: string | null
}

/** GRAPH 程序块基本信息段（block） */
export interface PlcGraphBlockInfo {
    /** 程序块名称（如 FB260_CacheGraph） */
    blockName: string
    /** 程序块类型（FB/FC/OB/DB/UDT） */
    blockType: string
    /** 程序块编号（如 260） */
    blockNumber: number | null
    /** 编程语言（GRAPH） */
    programmingLanguage: string
}

/** GRAPH 程序块解析信息信封（source + block 两段，camelCase） */
export interface PlcGraphBlockItem {
    source: PlcGraphBlockSource
    block: PlcGraphBlockInfo
    /** 该块是否已在导入工作台生成过预览（存在 PreviewReady 批次） */
    hasPreviewBatch: boolean
}

export interface PlcGraphBlockQuery {
    /** PLC 项目 id（可选，>0 生效） */
    projectId?: number | string
    /** PLC 设备编码（可选，ai_plc_project_new.plccode，如 PLC1/PLC2） */
    plcCode?: string
    /** 工站代码（可选，如 OP010） */
    stationCode?: string
    /** 块名模糊匹配（可选） */
    blockName?: string
    /** 单次返回上限（默认 500） */
    limit?: number
}

const routerName = 'PLC_GraphBlock'

export default {
    /**
     * 条件过滤查询 GRAPH 程序块解析信息（GET /PLC_GraphBlock/GetList）
     * 后端返回 HttpResponseResultModel 信封（BackResult 承载数组），此处解包为列表
     */
    getList: async (params: PlcGraphBlockQuery): Promise<PlcGraphBlockItem[]> => {
        const res: any = await api.get(`${routerName}/GetList`, { params })
        return res?.data ?? res?.backResult ?? []
    },

    /**
     * 按块主键获取单个 GRAPH 程序块解析信息（GET /PLC_GraphBlock/GetByBlock/{blockId}）
     */
    getByBlock: async (blockId: number | string): Promise<PlcGraphBlockItem> => {
        const res: any = await api.get(`${routerName}/GetByBlock/${blockId}`)
        return res?.data ?? res?.backResult
    },
}
