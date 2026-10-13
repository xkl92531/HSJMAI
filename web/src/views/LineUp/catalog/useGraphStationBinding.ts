import type { LineUpGraphCatalogLookup } from '@/api/modules/LineUp/catalog'
import type { GraphStationBindResult, GraphStationBoundBlock, GraphStationMonitorBinding, GraphStationUnbindResult } from '@/api/modules/PLC/graphStationBinding'
import { computed, ref } from 'vue'
import graphStationBindingApi from '@/api/modules/PLC/graphStationBinding'
import lineUpGraphCatalogApi from '@/api/modules/LineUp/catalog'
import projectApi from '@/api/modules/PLC/project'
import pack from './components/lang'

// —— 非 vue 模块的 i18n 桥（组件外无 useI18n 上下文）——
// 优先动态接入全局 i18n 实例（@/locales 具名导出 i18n，legacy:false 下 i18n.global.t 可用），
// 并把本视图语言包幂等合并进全局消息（页面 setup 的 mergeLocaleMessage 合并同一份数据，互不冲突）；
// 动态导入在无 vite 插件链的环境（vitest）无法解析 @/locales 的虚拟模块，捕获后回退本地四语字典：
// 语言探测顺序 = localStorage 'userPreferences'（fantastic-admin 本地偏好，storageTo=local 时写入）
// → navigator.language → zh-cn，与全局 fallbackLocale 一致（测试环境因此稳定拿到 zh-cn 文案）。
type I18nBridgeParams = Record<string, string | number>
type I18nBridgeTranslate = (key: string, params?: I18nBridgeParams) => string

let globalTranslate: I18nBridgeTranslate | undefined

void import('@/locales').then((mod) => {
    // vue-i18n Composer 的泛型重载与四语字典结构不完全吻合，这里按桥签名收窄（运行时语义不变）
    const bridge = mod.i18n.global as unknown as {
        t: I18nBridgeTranslate
        mergeLocaleMessage: (locale: string, message: Record<string, unknown>) => void
    }
    for (const locale of Object.keys(pack)) {
        bridge.mergeLocaleMessage(locale, pack[locale])
    }
    globalTranslate = bridge.t
}).catch(() => {
    // vitest 等未挂 vite 插件链的环境：保持本地字典回退
})

function detectBridgeLocale(): string {
    try {
        if (typeof localStorage !== 'undefined') {
            const raw = localStorage.getItem('userPreferences')
            const parsed = raw ? JSON.parse(raw) as Record<string, { app?: { defaultLang?: string } } | undefined> : null
            if (parsed && typeof parsed === 'object') {
                for (const profile of Object.values(parsed)) {
                    const lang = profile?.app?.defaultLang
                    if (typeof lang === 'string' && pack[lang]) {
                        return lang
                    }
                }
            }
        }
    }
    catch {
        // 本地偏好读取失败（JSON 损坏等）：继续走浏览器语言探测
    }
    const nav = typeof navigator !== 'undefined' ? String(navigator.language ?? '').toLowerCase() : ''
    if (/^zh-(tw|hant|hk|mo)/.test(nav)) {
        return 'zh-tw'
    }
    if (nav.startsWith('zh')) {
        return 'zh-cn'
    }
    if (nav.startsWith('ja')) {
        return 'ja'
    }
    if (nav.startsWith('en')) {
        return 'en'
    }
    return 'zh-cn'
}

function localTranslate(key: string, params?: I18nBridgeParams): string {
    const table = pack[detectBridgeLocale()] ?? pack['zh-cn']
    let text = table.lineupCatalog_form[key] ?? pack['zh-cn'].lineupCatalog_form[key] ?? key
    if (params) {
        text = text.replace(/\{(\w+)\}/g, (match, name: string) => (
            params[name] === undefined ? match : String(params[name])
        ))
    }
    return text
}

function t(key: string, params?: I18nBridgeParams): string {
    if (globalTranslate) {
        return globalTranslate(`lineupCatalog_form.${key}`, params)
    }
    return localTranslate(key, params)
}

/** 在用 PLC 项目（ai_plc_project_new 未删除，携带产线/设备绑定冗余字段） */
export interface GraphStationProjectOption {
    id: string
    name: string
    plcCode: string | null
    productionLineId: string | null
    /** 项目详情页绑定的 PLC 设备 Id（ai_plc_project_new.plcdeviceid；块→设备匹配优先用它） */
    plcDeviceId: string | null
    plcDeviceName: string | null
}

export type GraphStationBindingNodeKind = 'line' | 'process' | 'station' | 'substation' | 'block' | 'placeholder'

/** 左侧导航树节点：产线 → 工序 → 工站 → 子工站（含 PLC 设备），已绑定 GRAPH 块挂为子工站叶子（可解绑） */
export interface GraphStationBindingNode {
    key: string
    kind: GraphStationBindingNodeKind
    label: string
    /** line/process/station 的编码 */
    code?: string | null
    lineId?: string
    processId?: string
    stationId?: string
    /** 所属工站显示名（子工站节点向上带出） */
    stationLabel?: string
    /** 子工站 Id（Basic_SubStation.Id） */
    subStationId?: string
    /** station/substation 节点：工站代码（绑定目标） */
    stationCode?: string
    /** substation 节点：PLC 编码（Basic_SubStation.PlcName ↔ 项目 plccode） */
    plcCode?: string
    /** substation 节点：PLC 项目 Id */
    projectId?: string
    /** block 节点：绑定行 Id（解绑用） */
    bindingId?: string
    /** block 节点：GRAPH 块名 */
    blockName?: string
    /** 节点下已绑定块数（station/substation 徽标） */
    count?: number
    children: GraphStationBindingNode[]
}

/** 当前选中节点决定的右侧列表范围与绑定目标 */
export interface GraphStationScope {
    lineId: string
    stationCode: string
    stationName: string
    projectId: string
    plcCode: string
}

export interface GraphStationBindingGateway {
    getLookups: () => Promise<LineUpGraphCatalogLookup>
    getProjects: () => Promise<GraphStationProjectOption[]>
    getBlocks: () => Promise<GraphStationBoundBlock[]>
    bind: (request: { stationCode: string, blockIds: string[] }) => Promise<GraphStationBindResult>
    unbind: (request: { bindingIds: string[] }) => Promise<GraphStationUnbindResult>
    rerunRegistration: (projectId: string) => Promise<unknown>
    /** 监控目录查询（按 line+device+station 定位子工站下的结构绑定行） */
    listMonitorBindings: (query: { lineId?: string, deviceId?: string, stationId?: string }) => Promise<GraphStationMonitorBinding[]>
    /** 独立开启监控三连（Prepare → Validate → Monitoring(true)），不依赖重新保存结构 */
    enableMonitoring: (bindingId: string) => Promise<void>
}

const defaultGateway: GraphStationBindingGateway = {
    async getLookups() {
        return await lineUpGraphCatalogApi.getLookups({})
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
    getBlocks: () => graphStationBindingApi.getBlocks(),
    bind: request => graphStationBindingApi.bind(request),
    unbind: request => graphStationBindingApi.unbind(request),
    rerunRegistration: projectId => projectApiRerunRegistration(projectId),
    listMonitorBindings: query => graphStationBindingApi.listMonitorBindings(query),
    enableMonitoring: async (bindingId) => {
        // 2026-09-10：Prepare 失败时后端返回 200 + canValidate=false + issues（不抛错），
        // 此前直接继续跑 Validate 会报无关的 RuntimeBindingNotPrepared，掩盖真实原因——
        // 这里拦下并把 Blocking issue 直接抛给行详情显示。
        const prepared = await graphStationBindingApi.prepareRuntimeBinding(bindingId) as {
            canValidate?: boolean
            issues?: Array<{ code?: string, ownerSourceKey?: string, severity?: string }>
        }
        if (prepared && prepared.canValidate === false) {
            const blocking = (prepared.issues ?? [])
                .filter(issue => (issue.severity ?? 'Blocking') === 'Blocking')
                .map(issue => `${issue.code ?? '?'}@${issue.ownerSourceKey ?? ''}`)
                .join('; ')
            throw new Error(t('prepareFailed', { detail: blocking || t('prepareFailedUnknownReason') }))
        }
        await graphStationBindingApi.validateRuntimeBinding(bindingId)
        await graphStationBindingApi.setRuntimeBindingMonitoring(bindingId, true)
    },
}

/** POST /PLC_Upload/RerunGraphStepRegistration/{projectId}（延迟导入避免与 upload 模块循环依赖） */
async function projectApiRerunRegistration(projectId: string) {
    const uploadApi = (await import('@/api/modules/PLC/upload')).default
    return await uploadApi.rerunGraphStepRegistration(projectId)
}

/** 子工站基础资料行（Basic_SubStation） */
type SubStationLookup = LineUpGraphCatalogLookup['subStations'][number]

/** 工站基础资料行（Basic_WorkStation） */
type StationLookup = LineUpGraphCatalogLookup['stations'][number]

function optionLabel(code: string | null | undefined, name: string | null | undefined) {
    return ((code || '') + ' ' + (name || '')).trim()
}

export function useGraphStationBinding(gateway: GraphStationBindingGateway = defaultGateway) {
    const bootLoading = ref(false)
    const blocksLoading = ref(false)
    const actionLoading = ref(false)
    const errorCode = ref('')

    const lookups = ref<LineUpGraphCatalogLookup>({ projects: [], lines: [], processes: [], stations: [], subStations: [], devices: [] })
    const projects = ref<GraphStationProjectOption[]>([])
    const blocks = ref<GraphStationBoundBlock[]>([])

    const selectedKey = ref('')
    const keyword = ref('')
    const selectedBlockIds = ref<string[]>([])
    /** 初始定位项目（如上传完成弹框带入 projectId 但树上无对应设备节点时兜底过滤右侧列表） */
    const focusProjectId = ref('')

    const projectById = computed(() => {
        const map = new Map<string, GraphStationProjectOption>()
        for (const project of projects.value) {
            map.set(project.id, project)
        }
        return map
    })

    /** 工站 → 子工站列表（Basic_SubStation，按 stationId 聚合，编码排序） */
    const subStationsByStationId = computed(() => {
        const map = new Map<string, SubStationLookup[]>()
        for (const sub of lookups.value.subStations) {
            const key = String(sub.stationId)
            if (!map.has(key)) {
                map.set(key, [])
            }
            map.get(key)!.push(sub)
        }
        for (const list of map.values()) {
            list.sort((a, b) => (a.code ?? '').localeCompare(b.code ?? ''))
        }
        return map
    })

    /** PLC 编码 → 在用项目（同编码多项目取第一个；正常一 PLC 一在用项目） */
    const projectByPlcCode = computed(() => {
        const map = new Map<string, GraphStationProjectOption>()
        for (const project of projects.value) {
            const code = (project.plcCode ?? '').trim()
            if (code && !map.has(code)) {
                map.set(code, project)
            }
        }
        return map
    })

    /** 已绑定块索引：key = 工站代码(小写) | 项目Id → 块列表 */
    const boundIndex = computed(() => {
        const map = new Map<string, GraphStationBoundBlock[]>()
        for (const block of blocks.value) {
            for (const binding of block.bindings) {
                const code = (binding.stationCode ?? '').trim()
                if (!code) {
                    continue
                }
                const key = code.toLowerCase() + '|' + String(block.plcProjectId)
                if (!map.has(key)) {
                    map.set(key, [])
                }
                map.get(key)!.push(block)
            }
        }
        return map
    })

    function boundBlocksOf(stationCode: string, projectId?: string): GraphStationBoundBlock[] {
        const code = (stationCode ?? '').trim().toLowerCase()
        if (!code) {
            return []
        }
        if (projectId) {
            return [...(boundIndex.value.get(code + '|' + projectId) ?? [])]
        }
        const result: GraphStationBoundBlock[] = []
        for (const [key, value] of boundIndex.value) {
            if (key.startsWith(code + '|')) {
                result.push(...value)
            }
        }
        return result
    }

    /** 左侧导航树：产线 → 工序 → 工站 → 子工站（含 PLC 设备），已绑定 GRAPH 块挂为子工站叶子 */
    const tree = computed<GraphStationBindingNode[]>(() => {
        const nodes: GraphStationBindingNode[] = []
        const stations = lookups.value.stations
        const processes = lookups.value.processes

        for (const line of lookups.value.lines) {
            const lineId = String(line.id)
            const lineNode: GraphStationBindingNode = {
                key: 'line:' + lineId,
                kind: 'line',
                label: optionLabel(line.code, line.name) || t('fallbackLineLabel', { id: lineId }),
                code: line.code,
                lineId,
                children: [],
            }
            const lineStations = stations.filter(item => String(item.lineId) === lineId)
            const knownProcessIds = new Set(processes.filter(item => String(item.lineId) === lineId).map(item => String(item.id)))

            for (const process of processes.filter(item => String(item.lineId) === lineId)) {
                const processId = String(process.id)
                const processNode: GraphStationBindingNode = {
                    key: 'process:' + processId,
                    kind: 'process',
                    label: optionLabel(process.code, process.name) || t('fallbackProcessLabel', { id: processId }),
                    code: process.code,
                    lineId,
                    processId,
                    children: [],
                }
                for (const station of lineStations.filter(item => String(item.processId) === processId)) {
                    const stationNode = buildStationNode(station)
                    if (stationNode) {
                        processNode.children.push(stationNode)
                    }
                }
                // 聚焦 PLC 设备时，无存活工站的工序不显示
                if (!focusProjectId.value || processNode.children.length > 0) {
                    lineNode.children.push(processNode)
                }
            }
            // 工序缺失/未知的工站直接挂产线，保证不丢
            for (const station of lineStations.filter(item => !item.processId || !knownProcessIds.has(String(item.processId)))) {
                const stationNode = buildStationNode(station)
                if (stationNode) {
                    lineNode.children.push(stationNode)
                }
            }
            lineNode.count = lineNode.children.reduce((sum, child) => sum + (child.count ?? 0), 0)
            // 聚焦 PLC 设备时，无存活工序/工站的产线不显示
            if (!focusProjectId.value || lineNode.children.length > 0) {
                nodes.push(lineNode)
            }
        }

        if (nodes.length === 0) {
            nodes.push({
                key: 'placeholder:none',
                kind: 'placeholder',
                label: focusProjectId.value
                    ? t('placeholderNoMatchUnderDevice')
                    : t('placeholderBasicDataMissing'),
                children: [],
            })
        }
        return nodes
    })

    /**
     * 工站节点：子工站 = 组织架构节点，其下挂已绑定 GRAPH 块叶子（按项目匹配子工站；可解绑）；
     * 项目与所有子工站 PLC 都不匹配的绑定块直接挂工站节点下（不丢）。
     * count 徽标统计该层级下已绑定块数。
     */
    function buildStationNode(station: StationLookup): GraphStationBindingNode | null {
        const stationId = String(station.id)
        const stationCode = (station.code ?? '').trim()
        const stationLabel = optionLabel(station.code, station.name) || t('fallbackStationLabel', { id: stationId })
        const stationNode: GraphStationBindingNode = {
            key: 'station:' + stationId,
            kind: 'station',
            label: stationLabel,
            code: station.code,
            lineId: String(station.lineId),
            processId: station.processId ? String(station.processId) : undefined,
            stationId,
            stationCode,
            children: [],
        }
        const focus = focusProjectId.value.trim()
        const subStations = (subStationsByStationId.value.get(stationId) ?? []).filter((sub) => {
            // 聚焦某 PLC 项目时（两入口 initialProjectId），仅显示该项目设备下的子工站；
            // 未绑 PLC 的子工站聚焦时隐藏，避免树里混入其它 PLC 设备的数据
            if (!focus) {
                return true
            }
            const plcName = (sub.plcName ?? '').trim()
            if (!plcName) {
                return false
            }
            return projectByPlcCode.value.get(plcName)?.id === focus
        })
        const matchedProjectIds = new Set<string>()
        for (const sub of subStations) {
            const plcName = (sub.plcName ?? '').trim()
            const project = plcName ? projectByPlcCode.value.get(plcName) : undefined
            const subNode: GraphStationBindingNode = {
                key: 'station:' + stationId + ':sub:' + String(sub.id),
                kind: 'substation',
                label: optionLabel(sub.code, sub.name) || t('fallbackSubStationLabel', { id: String(sub.id) }),
                plcCode: plcName || undefined,
                projectId: project?.id,
                lineId: String(station.lineId),
                stationId,
                stationLabel,
                subStationId: String(sub.id),
                stationCode,
                children: [],
            }
            subNode.count = boundBlocksOf(stationCode, project?.id).length
            // 已绑定块挂为子工站叶子：块的项目匹配该子工站 PLC（项目 plcCode ↔ 子工站 PlcName）
            for (const block of boundBlocksOf(stationCode, project?.id)) {
                const binding = block.bindings.find(
                    item => (item.stationCode ?? '').trim().toLowerCase() === stationCode.toLowerCase(),
                )
                if (!binding) {
                    continue
                }
                subNode.children.push({
                    key: 'binding:' + String(binding.bindingId),
                    kind: 'block',
                    label: block.blockName,
                    blockName: block.blockName,
                    bindingId: String(binding.bindingId),
                    plcCode: plcName || undefined,
                    projectId: project?.id,
                    lineId: String(station.lineId),
                    stationId,
                    stationCode,
                    children: [],
                })
            }
            if (project) {
                matchedProjectIds.add(project.id)
            }
            stationNode.children.push(subNode)
        }
        // 项目与所有子工站 PLC 都不匹配的绑定块直接挂工站下（徽标口径不变：计数含这部分）。
        // 聚焦某 PLC 项目时，其它项目的块不显示（绑定仍在库中，取消聚焦即恢复）。
        for (const block of boundBlocksOf(stationCode)) {
            const project = projectById.value.get(String(block.plcProjectId))
            if (focus && project && project.id !== focus) {
                continue
            }
            if (project && matchedProjectIds.has(project.id)) {
                continue
            }
            for (const binding of block.bindings) {
                const code = (binding.stationCode ?? '').trim().toLowerCase()
                if (code !== stationCode.toLowerCase()) {
                    continue
                }
                stationNode.children.push({
                    key: 'binding:' + String(binding.bindingId),
                    kind: 'block',
                    label: block.blockName,
                    blockName: block.blockName,
                    bindingId: String(binding.bindingId),
                    lineId: String(station.lineId),
                    stationId,
                    stationCode,
                    children: [],
                })
            }
        }
        stationNode.count = focus
            ? boundBlocksOf(stationCode, focus).length
            : boundBlocksOf(stationCode).length
        // 聚焦 PLC 设备时，既无匹配子工站、也无该设备绑定块的空工站不显示
        if (focus && stationNode.children.length === 0) {
            return null
        }
        return stationNode
    }

    const nodeByKey = computed(() => {
        const map = new Map<string, GraphStationBindingNode>()
        const walk = (nodes: GraphStationBindingNode[]) => {
            for (const node of nodes) {
                map.set(node.key, node)
                walk(node.children)
            }
        }
        walk(tree.value)
        return map
    })

    const selectedNode = computed(() => nodeByKey.value.get(selectedKey.value) ?? null)

    /** 右侧列表范围与绑定目标（由选中节点推导） */
    const scope = computed<GraphStationScope>(() => {
        const node = selectedNode.value
        const empty: GraphStationScope = { lineId: '', stationCode: '', stationName: '', projectId: '', plcCode: '' }
        if (!node) {
            return { ...empty, projectId: focusProjectId.value }
        }
        if (node.kind === 'line' || node.kind === 'process') {
            return { ...empty, lineId: node.lineId ?? '', projectId: focusProjectId.value }
        }
        if (node.kind === 'station') {
            return {
                lineId: node.lineId ?? '',
                stationCode: node.stationCode ?? '',
                stationName: node.label,
                projectId: focusProjectId.value,
                plcCode: '',
            }
        }
        if (node.kind === 'substation') {
            return {
                lineId: node.lineId ?? '',
                stationCode: node.stationCode ?? '',
                stationName: node.stationLabel ?? node.label,
                projectId: node.projectId ?? '',
                plcCode: node.plcCode ?? '',
            }
        }
        return { ...empty, projectId: focusProjectId.value }
    })

    /** 右侧未绑定块列表（按范围 + 关键词过滤） */
    const unboundBlocks = computed(() => {
        const kw = keyword.value.trim().toLowerCase()
        return blocks.value.filter((block) => {
            if (block.bindings.length > 0) {
                return false
            }
            if (scope.value.projectId) {
                if (String(block.plcProjectId) !== scope.value.projectId) {
                    return false
                }
            }
            else if (scope.value.lineId) {
                const project = projectById.value.get(String(block.plcProjectId))
                if (!project || project.productionLineId !== scope.value.lineId) {
                    return false
                }
            }
            if (kw && !block.blockName.toLowerCase().includes(kw)) {
                return false
            }
            return true
        })
    })

    const canBind = computed(() => Boolean(scope.value.stationCode) && selectedBlockIds.value.length > 0)

    async function loadBlocks() {
        blocksLoading.value = true
        try {
            blocks.value = await gateway.getBlocks()
        }
        catch (error) {
            errorCode.value = error instanceof Error && error.message ? error.message : 'GraphStationBindingRequestFailed'
            throw error
        }
        finally {
            blocksLoading.value = false
        }
    }

    async function initialize(initial?: { lineId?: string, projectId?: string }) {
        bootLoading.value = true
        errorCode.value = ''
        try {
            const [lookupData, projectRows, blockRows] = await Promise.all([
                gateway.getLookups(),
                gateway.getProjects(),
                gateway.getBlocks(),
            ])
            lookups.value = lookupData
            projects.value = projectRows
            blocks.value = blockRows
        }
        catch (error) {
            errorCode.value = error instanceof Error && error.message ? error.message : 'GraphStationBindingRequestFailed'
            throw error
        }
        finally {
            bootLoading.value = false
        }

        selectedKey.value = ''
        // 入口项目上下文决定设备过滤：带 projectId 打开时树只显示该 PLC 设备的子工站/块，
        // 定位到子工站节点仅改变选中，不再清空 focus（清空会让树回到全量、混入其它 PLC 数据）
        focusProjectId.value = initial?.projectId ?? ''
        if (initial?.projectId) {
            const subNode = [...nodeByKey.value.values()].find(node =>
                node.kind === 'substation' && node.projectId === initial.projectId)
            if (subNode) {
                selectedKey.value = subNode.key
            }
        }
        if (!selectedKey.value && initial?.lineId) {
            const lineNode = nodeByKey.value.get('line:' + initial.lineId)
            if (lineNode) {
                selectedKey.value = lineNode.key
            }
        }
        if (!selectedKey.value) {
            const firstLine = tree.value.find(node => node.kind === 'line')
            selectedKey.value = firstLine?.key ?? ''
        }
    }

    function selectNode(key: string) {
        selectedKey.value = key
        // 设备过滤（focusProjectId）由入口项目上下文决定，导航切换不清空；
        // 无项目上下文打开时本就为空（全量显示），语义一致
        selectedBlockIds.value = []
    }

    /** 弹框内手动切换 PLC 设备（项目）过滤：空串 = 全部设备（全量树） */
    function setFocusProject(projectId: string) {
        focusProjectId.value = (projectId ?? '').trim()
        // 切换后原选中节点可能已被过滤掉：重置到第一条产线
        if (selectedKey.value && !nodeByKey.value.has(selectedKey.value)) {
            const firstLine = tree.value.find(node => node.kind === 'line')
            selectedKey.value = firstLine?.key ?? ''
        }
        selectedBlockIds.value = []
    }

    /** 勾选右侧块后调用（同步 el-table 多选） */
    function setSelection(ids: string[]) {
        selectedBlockIds.value = ids
    }

    /** 批量绑定所选块到当前工站；返回后端结果（含跳过明细） */
    async function bindSelected(): Promise<GraphStationBindResult> {
        const stationCode = scope.value.stationCode
        if (!stationCode || selectedBlockIds.value.length === 0) {
            throw new Error('BindingTargetMissing')
        }
        actionLoading.value = true
        try {
            const result = await gateway.bind({ stationCode, blockIds: [...selectedBlockIds.value] })
            selectedBlockIds.value = []
            await loadBlocks()
            return result
        }
        finally {
            actionLoading.value = false
        }
    }

    /** 解绑（左侧块节点删除按钮）；块回到右侧未绑定区 */
    async function unbind(bindingIds: string[]): Promise<GraphStationUnbindResult> {
        if (bindingIds.length === 0) {
            throw new Error('BindingTargetMissing')
        }
        actionLoading.value = true
        try {
            const result = await gateway.unbind({ bindingIds })
            await loadBlocks()
            return result
        }
        finally {
            actionLoading.value = false
        }
    }

    /** 按当前范围项目重跑 GRAPH 步位注册（绑定关系变更后立即生效） */
    async function rerunRegistration(): Promise<unknown> {
        const projectId = scope.value.projectId || focusProjectId.value
        if (!projectId) {
            throw new Error('BindingTargetMissing')
        }
        actionLoading.value = true
        try {
            return await gateway.rerunRegistration(projectId)
        }
        finally {
            actionLoading.value = false
        }
    }

    const stationByCode = computed(() => {
        const map = new Map<string, string>()
        for (const node of nodeByKey.value.values()) {
            if (node.kind === 'station' && node.stationCode) {
                map.set(node.stationCode.toLowerCase(), node.label)
            }
        }
        return map
    })

    return {
        bootLoading,
        blocksLoading,
        actionLoading,
        errorCode,
        lookups,
        projects,
        blocks,
        tree,
        nodeByKey,
        selectedKey,
        selectedNode,
        scope,
        focusProjectId,
        keyword,
        selectedBlockIds,
        unboundBlocks,
        canBind,
        stationByCode,
        initialize,
        selectNode,
        setFocusProject,
        listMonitorBindings: gateway.listMonitorBindings,
        enableMonitoring: gateway.enableMonitoring,
        setSelection,
        bindSelected,
        unbind,
        rerunRegistration,
        loadBlocks,
    }
}
