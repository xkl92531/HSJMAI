import type { PlcGraphBlockItem, PlcGraphBlockQuery } from '@/api/modules/PLC/graphBlock'
import type { LineUpGraphCatalogLookup } from '@/api/modules/LineUp/catalog'
import { computed, reactive, ref } from 'vue'
import graphBlockApi from '@/api/modules/PLC/graphBlock'
import lineUpGraphCatalogApi from '@/api/modules/LineUp/catalog'
import projectApi from '@/api/modules/PLC/project'

/** 产线下拉选项（Basic_ProductionLine，未删且启用） */
export interface GraphBlockLineOption { id: string, code: string | null, name: string | null }

/** 在用 PLC 项目（ai_plc_project_new 未删除，携带产线/设备绑定冗余字段） */
export interface GraphBlockProjectOption {
    id: string
    name: string
    plcCode: string | null
    productionLineId: string | null
    plcDeviceId: string | null
    plcDeviceName: string | null
}

/** 在用 PLC 设备（由当前产线下的在用项目推导：设备去重 + plcCode 供查询） */
export interface GraphBlockDeviceOption {
    id: string
    plcCode: string | null
    name: string | null
}

export interface GraphBlockCatalogGateway {
    getProductionLines: () => Promise<GraphBlockLineOption[]>
    getProjects: () => Promise<GraphBlockProjectOption[]>
    getHierarchy: (params: { lineId?: string }) => Promise<Pick<LineUpGraphCatalogLookup, 'processes' | 'stations'>>
    getGraphBlocks: (params: PlcGraphBlockQuery) => Promise<PlcGraphBlockItem[]>
}

/** 查询单次返回上限（后端默认 500，GRAPH 块总量级远小于此） */
const QUERY_LIMIT = 500

const defaultGateway: GraphBlockCatalogGateway = {
    async getProductionLines() {
        const res = await projectApi.productionLines() as unknown as GraphBlockLineOption[] | { data?: GraphBlockLineOption[] }
        const rows = Array.isArray(res) ? res : (res?.data ?? [])
        return rows.filter(item => item && item.id != null)
    },
    async getProjects() {
        const res = await projectApi.list({ page: 1, limit: 200 }) as unknown as { data?: any[] }
        const rows = Array.isArray(res?.data) ? res.data : []
        return rows
            .filter(item => item && item.id != null)
            .map(item => ({
                id: String(item.id),
                name: String(item.projectName ?? item.name ?? item.id),
                plcCode: item.plcCode ?? null,
                productionLineId: item.productionLineId != null ? String(item.productionLineId) : null,
                plcDeviceId: item.plcDeviceId != null ? String(item.plcDeviceId) : null,
                plcDeviceName: item.plcDeviceName ?? null,
            }))
    },
    async getHierarchy(params) {
        const lookups = await lineUpGraphCatalogApi.getLookups({ lineId: params.lineId || undefined })
        return { processes: lookups.processes, stations: lookups.stations }
    },
    getGraphBlocks: params => graphBlockApi.getList(params),
}

function errorMessage(error: unknown, fallback: string) {
    return error instanceof Error && error.message ? error.message : fallback
}

export function useGraphBlockCatalog(gateway: GraphBlockCatalogGateway = defaultGateway) {
    const filters = reactive({
        /** 产线（Basic_ProductionLine.Id） */
        lineId: '',
        /** 工序（联动筛选工站用，不直接参与查询） */
        processId: '',
        /** 工站（Basic_WorkStation.Id；查询取其 Code 作 stationCode） */
        stationId: '',
        /** PLC 设备（在用项目推导的 plcDeviceId；查询取其 plcCode） */
        deviceId: '',
        /** PLC 项目（ai_plc_project_new.Id，查询作 projectId） */
        projectId: '',
        /** 块名模糊匹配 */
        keyword: '',
        /** 本地分页（新接口单次返回，不分页，前端切片） */
        page: 1,
        pageSize: 20,
    })
    const lines = ref<GraphBlockLineOption[]>([])
    const projects = ref<GraphBlockProjectOption[]>([])
    const lookups = ref<Pick<LineUpGraphCatalogLookup, 'processes' | 'stations'>>({ processes: [], stations: [] })
    const items = ref<PlcGraphBlockItem[]>([])
    const loading = ref(false)
    const safeErrorCode = ref('')
    let catalogGeneration = 0
    let hierarchyGeneration = 0

    const availableProcesses = computed(() => lookups.value.processes.filter(item =>
        !filters.lineId || item.lineId === filters.lineId,
    ))
    const availableStations = computed(() => lookups.value.stations.filter(item =>
        (!filters.lineId || item.lineId === filters.lineId)
        && (!filters.processId || item.processId === filters.processId),
    ))
    const availableProjects = computed(() => projects.value.filter(item =>
        !filters.lineId || item.productionLineId === filters.lineId,
    ))
    /** 当前产线下的在用 PLC 设备：在用项目按产线过滤后按设备去重（"在用" = 有未删除 PLC 项目挂在设备上） */
    const availableDevices = computed<GraphBlockDeviceOption[]>(() => {
        const seen = new Set<string>()
        const result: GraphBlockDeviceOption[] = []
        for (const project of availableProjects.value) {
            if (!project.plcDeviceId || seen.has(project.plcDeviceId)) {
                continue
            }
            seen.add(project.plcDeviceId)
            result.push({ id: project.plcDeviceId, plcCode: project.plcCode, name: project.plcDeviceName ?? project.plcCode })
        }
        return result
    })
    const selectedProject = computed(() => projects.value.find(item => item.id === filters.projectId) ?? null)
    const selectedDevice = computed(() => availableDevices.value.find(item => item.id === filters.deviceId) ?? null)
    const selectedStation = computed(() => lookups.value.stations.find(item => item.id === filters.stationId) ?? null)
    const projectNameById = computed(() => {
        const map = new Map<string, string>()
        for (const project of projects.value) {
            map.set(project.id, project.name)
        }
        return map
    })
    const pagedItems = computed(() => {
        const start = (filters.page - 1) * filters.pageSize
        return items.value.slice(start, start + filters.pageSize)
    })
    const pageSummary = computed(() => ({
        projectCount: new Set(pagedItems.value.map(item => String(item.source.plcProjectId))).size,
        blockTypeCount: new Set(pagedItems.value.map(item => item.block.blockType)).size,
    }))

    function requestParams(): PlcGraphBlockQuery {
        return {
            projectId: filters.projectId || undefined,
            plcCode: selectedDevice.value?.plcCode || undefined,
            stationCode: selectedStation.value?.code || undefined,
            blockName: filters.keyword.trim() || undefined,
            limit: QUERY_LIMIT,
        }
    }

    async function loadBaseOptions() {
        const [lineRows, projectRows] = await Promise.all([
            gateway.getProductionLines(),
            gateway.getProjects(),
        ])
        lines.value = lineRows
        projects.value = projectRows
    }

    async function loadHierarchy() {
        const generation = ++hierarchyGeneration
        const result = await gateway.getHierarchy({ lineId: filters.lineId || undefined })
        if (generation === hierarchyGeneration) {
            lookups.value = result
        }
    }

    async function loadCatalog() {
        const generation = ++catalogGeneration
        loading.value = true
        safeErrorCode.value = ''
        try {
            const result = await gateway.getGraphBlocks(requestParams())
            if (generation === catalogGeneration) {
                items.value = result
            }
        }
        catch (error) {
            if (generation === catalogGeneration) {
                safeErrorCode.value = errorMessage(error, 'GraphBlockRequestFailed')
            }
            throw error
        }
        finally {
            if (generation === catalogGeneration) {
                loading.value = false
            }
        }
    }

    async function initialize(plcProjectId = '') {
        await loadBaseOptions()
        filters.projectId = ''
        filters.lineId = ''
        filters.processId = ''
        filters.stationId = ''
        filters.deviceId = ''
        const target = plcProjectId ? projects.value.find(item => item.id === plcProjectId) : null
        if (target) {
            // PLC 项目列表页「GRAPH 结构」入口：定位项目并回填产线/设备联动
            filters.projectId = target.id
            filters.lineId = target.productionLineId || ''
            filters.deviceId = target.plcDeviceId || ''
        }
        await loadHierarchy()
        await loadCatalog()
    }

    async function search() {
        filters.page = 1
        await loadCatalog()
    }

    /** 静默刷新：保持筛选与页码不变，仅重拉列表（keep-alive 页面重新激活时用） */
    async function refresh() {
        await loadCatalog()
    }

    async function selectLine(lineId: string) {
        filters.lineId = lineId
        filters.processId = ''
        filters.stationId = ''
        filters.deviceId = ''
        filters.projectId = ''
        await loadHierarchy()
        await search()
    }

    function selectProcess(processId: string) {
        filters.processId = processId
        filters.stationId = ''
    }

    function selectStation(stationId: string) {
        filters.stationId = stationId
    }

    function selectDevice(deviceId: string) {
        filters.deviceId = deviceId
        // 已选项目不属于该设备时清空项目，避免 projectId 与 plcCode 矛盾（后端 AND 组合会恒空）
        if (filters.projectId && selectedProject.value && selectedProject.value.plcDeviceId !== deviceId) {
            filters.projectId = ''
        }
    }

    function selectProject(projectId: string) {
        filters.projectId = projectId
        // 选项目后自动对齐其绑定设备（同一设备选项下 plcCode 一致，查询结果不受影响）
        const target = projectId ? projects.value.find(item => item.id === projectId) : null
        if (target?.plcDeviceId) {
            filters.deviceId = target.plcDeviceId
        }
        else if (!projectId) {
            filters.deviceId = ''
        }
    }

    /**
     * 「查看流程图」跳转目标：GRAPH 智能导入工作台（/lineup/import）的生成预览功能。
     * 携带当前产线/设备定位参数；blockName 用于目标页提示用户选择该块的 XML 生成预览。
     */
    function buildGraphImportRoute(blockName?: string) {
        const query: Record<string, string> = {}
        if (filters.lineId) {
            query.lineId = filters.lineId
        }
        if (selectedDevice.value) {
            query.deviceId = selectedDevice.value.id
        }
        if (blockName) {
            query.blockName = blockName
        }
        return { path: '/lineup/import', query }
    }

    return {
        filters,
        lines,
        projects,
        lookups,
        items,
        loading,
        safeErrorCode,
        buildGraphImportRoute,
        availableProcesses,
        availableStations,
        availableProjects,
        availableDevices,
        selectedProject,
        selectedDevice,
        selectedStation,
        projectNameById,
        pagedItems,
        pageSummary,
        initialize,
        search,
        refresh,
        selectLine,
        selectProcess,
        selectStation,
        selectDevice,
        selectProject,
    }
}
