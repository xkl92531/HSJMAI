<script setup lang="ts">
import type { GraphStationBoundBlock, GraphStationMonitorBinding } from '@/api/modules/PLC/graphStationBinding'
import type { GraphStationBindingNode } from '../useGraphStationBinding'
import { ElMessage, ElMessageBox } from 'element-plus'
import { computed, ref, watch } from 'vue'
import plcUploadApi from '@/api/modules/PLC/upload'
import lineUpImportApi from '@/api/modules/LineUp/import'
import { registrationNotice, useGraphStationBinding } from '../useGraphStationBinding'
import { useI18n } from 'vue-i18n'
import messages from './lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}

/**
 * 批量绑定 GRAPH 与工站弹框：
 * - 左侧树：产线 → 工序 → 工站 → 子工站（携带 PLC 设备），已绑定 GRAPH 块挂为子工站叶子（可解绑，解绑后回到右侧）
 * - 右侧表：当前范围未绑定的 GRAPH 块，勾选（多选）绑定到左侧选中的工站
 * 数据源 ai_plc_stationblockbinding；绑定/解绑同步 ai_plc_block_new.stationcode
 */
const props = defineProps<{
    modelValue: boolean
    /** 入口定位：产线 Id（GRAPH 结构管理页当前筛选） */
    initialLineId?: string
    /** 入口定位：PLC 项目 Id（上传完成自动弹出时带入） */
    initialProjectId?: string
}>()

const emit = defineEmits<{
    (e: 'update:modelValue', value: boolean): void
    (e: 'closed'): void
    (e: 'changed'): void
}>()

const workspace = useGraphStationBinding()
const treeRef = ref()
const booted = ref(false)
const bootedKey = ref('')

const initKey = computed(() => (props.initialLineId || '') + '|' + (props.initialProjectId || ''))

watch(() => props.modelValue, async (visible) => {
    if (!visible) {
        return
    }
    if (booted.value && bootedKey.value === initKey.value) {
        return
    }
    booted.value = true
    bootedKey.value = initKey.value
    try {
        await workspace.initialize({
            lineId: props.initialLineId || undefined,
            projectId: props.initialProjectId || undefined,
        })
    }
    catch {
        ElMessage.error(t('lineupCatalog_form.loadFailed'))
    }
})

// 选中节点变化时同步 el-tree 高亮
watch(() => workspace.selectedKey.value, (key) => {
    treeRef.value?.setCurrentKey?.(key || null)
})

function handleUpdateModelValue(value: boolean) {
    emit('update:modelValue', value)
}

function handleClose() {
    emit('update:modelValue', false)
    emit('closed')
}

function friendlyError(error: unknown, fallback: string) {
    if (error instanceof Error && error.message) {
        return error.message
    }
    return fallback
}

/** 从 axios 错误响应体里透出后端真实错误信息（LineUp 422 = ImportError ProblemDetails） */
function apiErrorDetail(error: unknown, fallback: string) {
    const resp = (error as { response?: { data?: Record<string, unknown> } } | null)?.response?.data
    const picked = resp && (resp.errorMessage || resp.backResult || resp.detail || resp.title) as string | undefined
    return picked || friendlyError(error, fallback)
}

function onNodeClick(data: GraphStationBindingNode) {
    if (data.kind === 'placeholder' || data.kind === 'block') {
        return
    }
    workspace.selectNode(data.key)
}

function projectName(projectId: string) {
    const project = workspace.projects.value.find(item => item.id === projectId)
    return project ? project.name : t('lineupCatalog_form.projectFallback', { id: projectId })
}

function projectLabel(row: GraphStationBoundBlock) {
    const project = workspace.projects.value.find(item => item.id === String(row.plcProjectId))
    if (!project) {
        return t('lineupCatalog_form.projectFallback', { id: row.plcProjectId })
    }
    return (project.plcCode ? project.plcCode + ' · ' : '') + project.name
}

/** 有改动时提示已排队 GRAPH 步位重新注册（后端 RegistrationMessage） */
function showRegistrationNotice(result: { registrationMessage?: string | null } | null | undefined) {
    const text = registrationNotice(result)
    if (text) {
        ElMessage.info(text)
    }
}

function bindingTooltip(node: GraphStationBindingNode) {
    if (node.bindingStatus === 'Manual') {
        return t('lineupCatalog_form.bindingManualTooltip')
    }
    const reason = (node.reason ?? '').replace(/^自动建议[:：]/, '').trim() || '—'
    const confidence = node.confidence == null ? '—' : Math.round(Number(node.confidence) * 100) + '%'
    return t('lineupCatalog_form.bindingSuggestedTooltip', { reason, confidence })
}

async function acceptSuggestion(node: GraphStationBindingNode) {
    if (!node.bindingId) {
        return
    }
    try {
        const result = await workspace.acceptSuggestions([node.bindingId])
        ElMessage.success(t('lineupCatalog_form.acceptedCount', { count: 1 }))
        if (result.skipped && result.skipped.length > 0) {
            const head = result.skipped.slice(0, 3).map(item => t('lineupCatalog_form.skippedItem', { name: item.blockName, reason: item.reason })).join(t('lineupCatalog_form.listSep'))
            ElMessage.warning(t('lineupCatalog_form.skippedDetail', { list: head }) + (result.skipped.length > 3 ? t('lineupCatalog_form.moreSuffix') : ''))
        }
        showRegistrationNotice(result)
        emit('changed')
    }
    catch (error) {
        ElMessage.error(friendlyError(error, t('lineupCatalog_form.acceptFailed')))
    }
}

async function confirmReject(node: GraphStationBindingNode) {
    if (!node.bindingId) {
        return
    }
    try {
        await ElMessageBox.confirm(
            t('lineupCatalog_form.rejectConfirm', { name: node.blockName ?? '', code: node.stationCode ?? '' }),
            t('lineupCatalog_form.rejectTitle'),
            { type: 'warning', confirmButtonText: t('lineupCatalog_form.rejectSuggestionAction'), cancelButtonText: t('common.cancel') },
        )
    }
    catch {
        return
    }
    try {
        const result = await workspace.rejectSuggestions([node.bindingId])
        ElMessage.success(t('lineupCatalog_form.rejectedCount', { count: result.rejectedCount ?? result.unboundCount })
            + ((result.restoredBlockCount ?? 0) > 0 ? t('lineupCatalog_form.stationMarkRestored', { count: result.restoredBlockCount }) : '')
            + (result.clearedBlockCount > 0 ? t('lineupCatalog_form.stationMarkCleared', { count: result.clearedBlockCount }) : ''))
        showRegistrationNotice(result)
        emit('changed')
    }
    catch (error) {
        ElMessage.error(friendlyError(error, t('lineupCatalog_form.rejectFailed')))
    }
}

function onSelectionChange(rows: GraphStationBoundBlock[]) {
    workspace.setSelection(rows.map(row => String(row.blockId)))
}

const scopeSummary = computed(() => {
    const scope = workspace.scope.value
    if (scope.stationCode) {
        const device = scope.plcCode ? t('lineupCatalog_form.deviceWithParens', { code: scope.plcCode }) : ''
        return t('lineupCatalog_form.boundTarget', { code: scope.stationCode, name: scope.stationName || '', device })
    }
    if (scope.projectId) {
        return t('lineupCatalog_form.scopeProject', { name: projectName(scope.projectId) })
    }
    if (scope.lineId) {
        return t('lineupCatalog_form.scopeLine')
    }
    return t('lineupCatalog_form.scopeEmpty')
})

async function refresh() {
    try {
        await workspace.loadBlocks()
    }
    catch {
        ElMessage.error(friendlyError(null, t('lineupCatalog_form.refreshFailed')))
    }
}

async function bindNow() {
    if (!workspace.canBind.value) {
        return
    }
    try {
        const result = await workspace.bindSelected()
        const parts = [t('lineupCatalog_form.bindAdded', { count: result.boundCount })]
        if (result.movedCount > 0) {
            parts.push(t('lineupCatalog_form.bindMoved', { count: result.movedCount }))
        }
        if (result.updatedBlockCount > 0) {
            parts.push(t('lineupCatalog_form.bindStationMarked', { count: result.updatedBlockCount }))
        }
        if (result.skippedCount > 0) {
            parts.push(t('lineupCatalog_form.bindSkipped', { count: result.skippedCount }))
        }
        ElMessage.success(parts.join(t('lineupCatalog_form.commaSep')))
        if (result.skipped && result.skipped.length > 0) {
            const head = result.skipped.slice(0, 3).map(item => t('lineupCatalog_form.skippedItem', { name: item.blockName, reason: item.reason })).join(t('lineupCatalog_form.listSep'))
            ElMessage.warning(t('lineupCatalog_form.skippedDetail', { list: head }) + (result.skipped.length > 3 ? t('lineupCatalog_form.moreSuffix') : ''))
        }
        showRegistrationNotice(result)
        emit('changed')
    }
    catch (error) {
        ElMessage.error(friendlyError(error, t('lineupCatalog_form.bindFailed')))
    }
}

async function confirmUnbind(node: { blockName?: string, block?: { blockName: string }, bindingId: string }) {
    if (!node.bindingId) {
        return
    }
    // 兼容两种入参：树节点（blockName 顶层）与「已绑定」面板行（块名在 block.blockName）
    const blockName = node.blockName ?? node.block?.blockName ?? ''
    try {
        await ElMessageBox.confirm(
            t('lineupCatalog_form.unbindConfirm', { name: blockName }),
            t('lineupCatalog_form.unbindTitle'),
            { type: 'warning', confirmButtonText: t('lineupCatalog_form.unbindAction'), cancelButtonText: t('common.cancel') },
        )
    }
    catch {
        return
    }
    try {
        const result = await workspace.unbind([node.bindingId])
        ElMessage.success(t('lineupCatalog_form.unboundCount', { count: result.unboundCount })
            + ((result.restoredBlockCount ?? 0) > 0 ? t('lineupCatalog_form.stationMarkRestored', { count: result.restoredBlockCount }) : '')
            + (result.clearedBlockCount > 0 ? t('lineupCatalog_form.stationMarkCleared', { count: result.clearedBlockCount }) : ''))
        showRegistrationNotice(result)
        emit('changed')
    }
    catch (error) {
        ElMessage.error(friendlyError(error, t('lineupCatalog_form.unbindFailed')))
    }
}

async function rerunRegistration() {
    const projectId = workspace.scope.value.projectId || workspace.focusProjectId.value
    if (!projectId) {
        return
    }
    try {
        await ElMessageBox.confirm(
            t('lineupCatalog_form.rerunConfirm', { name: projectName(projectId) }),
            t('lineupCatalog_form.rerunTitle'),
            { type: 'info', confirmButtonText: t('lineupCatalog_form.executeAction'), cancelButtonText: t('common.cancel') },
        )
    }
    catch {
        return
    }
    try {
        await workspace.rerunRegistration()
        ElMessage.success(t('lineupCatalog_form.rerunQueued'))
    }
    catch (error) {
        ElMessage.error(friendlyError(error, t('lineupCatalog_form.rerunEnqueueFailed')))
    }
}

const canRerun = computed(() => Boolean(workspace.scope.value.projectId || workspace.focusProjectId.value))

// ---------- 确认后自动链路：生成预览 → 保存结构（按勾选定监控）→ GRAPH 步位注册 ----------
// 人工只做「上传 + 批量绑定工站」；本按钮对全部已绑定块自动执行三步。
// 注册已从 zip 上传后台链路移除（上传时无工站绑定，提前注册只会空分组），统一收口到这里。
type AutoStep = 'pending' | 'preview' | 'commit' | 'monitor' | 'done' | 'failed' | 'skipped' | 'running'
interface AutoPipelineRow {
    blockName: string
    stationCodes: string
    step: AutoStep
    detail: string
}

const autoRunning = ref(false)
const autoRows = ref<AutoPipelineRow[]>([])
const autoRegistration = ref<{ status: AutoStep, detail: string }>({ status: 'skipped', detail: '' })
/** 保存结构时是否同时开启实时监控（ReadyForMonitoring）；InstanceDB 未确认的块自动降级 StructureOnly */
const enableMonitoring = ref(true)
// 自动链路步骤开关：①生成预览保存结构（默认勾）②graph步位注册（默认勾）③开启监控（默认勾，独立于①）
const autoGenerate = ref(true)
const autoRegister = ref(true)
// 三步全不勾时禁用执行
const canRunAuto = computed(() => autoGenerate.value || autoRegister.value || enableMonitoring.value)
const executeLabel = computed(() => {
    const parts: string[] = []
    if (autoGenerate.value) {
        parts.push(t('lineupCatalog_form.stepSaveStructure'))
    }
    if (autoRegister.value) {
        parts.push(t('lineupCatalog_form.stepRegister'))
    }
    if (enableMonitoring.value) {
        parts.push(t('lineupCatalog_form.stepMonitor'))
    }
    return parts.length > 0 ? t('lineupCatalog_form.executePrefix', { steps: parts.join(' + ') }) : t('lineupCatalog_form.selectStepsFirst')
})

/** 服务端错误码提取：响应体 errorCode 优先，错误消息以 "<Code>: " 开头兜底 */
function errorCodeOf(error: unknown): string {
    const data = (error as { response?: { data?: Record<string, unknown> } } | null)?.response?.data
    const code = data && typeof data.errorCode === 'string' ? data.errorCode : ''
    if (code) {
        return code
    }
    const message = data && typeof data.errorMessage === 'string' ? data.errorMessage : ''
    return message.split(':', 1)[0] ?? ''
}

function autoStepText(step: AutoStep): string {
    switch (step) {
        case 'pending': return t('lineupCatalog_form.stepPending')
        case 'preview': return t('lineupCatalog_form.stepPreviewing')
        case 'commit': return t('lineupCatalog_form.stepCommitting')
        case 'monitor': return t('lineupCatalog_form.stepMonitoring')
        case 'done': return t('lineupCatalog_form.stepDone')
        case 'skipped': return t('lineupCatalog_form.stepSkipped')
        default: return t('lineupCatalog_form.stepFailed')
    }
}

/** 块 → PLC 设备：项目详情页绑定的设备 ID（plcdeviceid）优先，项目名 === 设备 ProjectName 兜底；均限定当前产线 */
function deviceForBlock(block: GraphStationBoundBlock) {
    const lookups = workspace.lookups.value
    const blockProjectId = String(block.plcProjectId)
    const project = workspace.projects.value.find(p => String(p.id) === blockProjectId)
    const projectName = project?.name
        ?? lookups.projects.find(p => String(p.id) === blockProjectId)?.name
    const scopeLineId = props.initialLineId || workspace.scope.value.lineId || ''
    const inScopeLine = (lineId: unknown) => !scopeLineId || String(lineId) === String(scopeLineId)
    // 优先：ai_plc_project_new.plcdeviceid（项目详情「绑定 PLC 设备」），名字漂移不再阻断
    if (project?.plcDeviceId) {
        const byId = lookups.devices.find(d => String(d.id) === String(project.plcDeviceId) && inScopeLine(d.lineId))
        if (byId) {
            return byId
        }
    }
    if (!projectName) {
        return null
    }
    return lookups.devices.find(d =>
        d.projectName === projectName &&
        inScopeLine(d.lineId)) ?? null
}

function sleep(ms: number) {
    return new Promise(resolve => setTimeout(resolve, ms))
}

/** 轮询批次进度直到 PreviewReady / Failed / 超时 */
async function waitForPreviewReady(batchId: string, timeoutMs = 150000): Promise<void> {
    const deadline = Date.now() + timeoutMs
    while (Date.now() < deadline) {
        await sleep(1500)
        const progress = await lineUpImportApi.getProgress(batchId)
        if (progress.status === 'PreviewReady') {
            return
        }
        if (progress.status === 'Failed') {
            throw new Error(progress.errorCode || 'LineUpPreviewFailed')
        }
    }
    throw new Error('LineUpPreviewTimeout')
}

/**
 * 独立开启监控（勾③未勾①）：查监控目录 → 已保存结构的块 Prepare→Validate→Monitoring(true)。
 * 未保存结构的块标跳过；已开启的块幂等跳过。目录按 (line,device,station) 组合缓存避免重复查询。
 */
async function runStandaloneMonitoring(boundBlocks: GraphStationBoundBlock[]) {
    const catalogCache = new Map<string, GraphStationMonitorBinding[]>()
    for (const block of boundBlocks) {
        const row = autoRows.value.find(r => r.blockName === block.blockName)
        if (!row) {
            continue
        }
        try {
            const device = deviceForBlock(block)
            const stationCode = (block.bindings[0]?.stationCode ?? '').trim()
            const station = workspace.lookups.value.stations.find(
                s => (s.code ?? '').trim().toLowerCase() === stationCode.toLowerCase(),
            )
            if (!device || !station) {
                row.step = 'skipped'
                row.detail = t('lineupCatalog_form.monitorSkipNoStation', { code: stationCode })
                continue
            }
            const cacheKey = String(station.lineId) + '|' + String(device.id) + '|' + String(station.id)
            let catalog = catalogCache.get(cacheKey)
            if (!catalog) {
                catalog = await workspace.listMonitorBindings({
                    lineId: String(station.lineId),
                    deviceId: String(device.id),
                    stationId: String(station.id),
                })
                catalogCache.set(cacheKey, catalog)
            }
            const matched = catalog.find(item =>
                (item.graphSourceName ?? '') === block.blockName
                && String(item.plcProjectId ?? '') === String(block.plcProjectId),
            )
            if (!matched) {
                row.step = 'skipped'
                row.detail = t('lineupCatalog_form.monitorSkipNotSaved')
                continue
            }
            if (matched.monitoringEnabled) {
                row.step = 'done'
                row.detail = t('lineupCatalog_form.monitorAlreadyOn')
                continue
            }
            row.step = 'monitor'
            row.detail = t('lineupCatalog_form.monitorEnabling')
            await workspace.enableMonitoring(matched.bindingId)
            row.step = 'done'
            row.detail = t('lineupCatalog_form.monitorEnabled')
        }
        catch (error) {
            row.step = 'failed'
            row.detail = apiErrorDetail(error, t('lineupCatalog_form.monitorEnableFailed'))
        }
    }
}
async function runAutoPipeline() {
    // 与左树同一设备过滤口径：聚焦某 PLC 设备（项目）时只处理该项目的块，未聚焦时处理全部已绑定块
    const focusProject = workspace.focusProjectId.value
    const boundBlocks = workspace.blocks.value.filter(b =>
        b.bindings.length > 0
        && (!focusProject || String(b.plcProjectId) === String(focusProject)))
    if (boundBlocks.length === 0) {
        ElMessage.warning(t('lineupCatalog_form.noBoundBlocks') + (focusProject ? t('lineupCatalog_form.filteredByFocus') : ''))
        return
    }
    const focusLabel = focusProject
        ? (workspace.projects.value.find(p => String(p.id) === String(focusProject))?.name ?? '')
        : ''
    const stepsText: string[] = []
    if (autoGenerate.value) {
        stepsText.push(t('lineupCatalog_form.autoStepGenerate') + (enableMonitoring.value ? t('lineupCatalog_form.withMonitoring') : t('lineupCatalog_form.withoutMonitoring')))
    }
    if (!autoGenerate.value && enableMonitoring.value) {
        stepsText.push(t('lineupCatalog_form.autoStepStandaloneMonitor'))
    }
    if (autoRegister.value) {
        stepsText.push(t('lineupCatalog_form.autoStepRegister'))
    }
    try {
        await ElMessageBox.confirm(
            t('lineupCatalog_form.autoConfirmMessage', {
                count: boundBlocks.length,
                scope: focusLabel ? t('lineupCatalog_form.focusDeviceScope', { name: focusLabel }) : t('lineupCatalog_form.allDevicesScope'),
                steps: stepsText.join(t('lineupCatalog_form.listSep')),
            }),
            t('lineupCatalog_form.autoConfirmTitle'),
            { type: 'info', confirmButtonText: t('lineupCatalog_form.confirmExecute'), cancelButtonText: t('common.cancel') },
        )
    }
    catch {
        return
    }

    autoRunning.value = true
    autoRegistration.value = { status: 'skipped', detail: '' }
    autoRows.value = boundBlocks.map(b => ({
        blockName: b.blockName,
        stationCodes: b.bindings.map(x => x.stationCode).filter(Boolean).join(' / '),
        step: 'pending',
        detail: '',
    }))

    const projectsToRegister = new Set<string>()
    let workspaceImports: { sourceFileName: string | null, status: string }[] = []
    try {
        const lineId = props.initialLineId || workspace.scope.value.lineId
        if (lineId) {
            const data = await lineUpImportApi.getWorkspace(lineId).catch(() => null)
            workspaceImports = data?.recentImports ?? []
        }
    }
    catch {
        // 工作台批次列表拿不到时退化为不跳过（提交幂等守卫兜底）
    }

    try {
        if (autoGenerate.value) {
        for (const block of boundBlocks) {
            const row = autoRows.value.find(r => r.blockName === block.blockName)
            if (!row) {
                continue
            }
            try {
                const device = deviceForBlock(block)
                if (!device) {
                    row.step = 'skipped'
                    row.detail = t('lineupCatalog_form.deviceNotFound')
                    continue
                }
                projectsToRegister.add(String(block.plcProjectId))

                const alreadyCommitted = workspaceImports.some(i => i.status === 'Committed' && (i.sourceFileName || '').includes(block.blockName))
                if (alreadyCommitted && !enableMonitoring.value) {
                    row.step = 'skipped'
                    row.detail = t('lineupCatalog_form.alreadyCommitted')
                    continue
                }
                // 勾选监控时已提交块不跳过：幂等重提交（ReadyForMonitoring）补跑
                // Ensure→Prepare→Validate→SetMonitoring，为历史 StructureOnly 块补开监控

                row.step = 'preview'
                row.detail = t('lineupCatalog_form.previewingNoUpload')
                // 永远生成新预览：旧 PreviewReady 快照可能早于最近的工站绑定/解析规则变化，
                // 复用会在提交时被置信度骨架守卫拦下（PreviewStale 422）。新预览自带最新桥接结果。
                const started = await lineUpImportApi.startPreviewFromBlock!(block.blockId, device.lineId, device.id)
                const batchId = String(started.batchId)
                await waitForPreviewReady(batchId)

                row.step = 'commit'
                const preview = await lineUpImportApi.getPreview(batchId)
                if (!preview.canCommitStructure) {
                    throw new Error(t('lineupCatalog_form.blockingIssue'))
                }
                // 已提交块只补监控：结构没必要重存，InstanceDB 未确认时直接跳过（重提交也开不了监控）
                if (alreadyCommitted && enableMonitoring.value && !preview.canCommitForMonitoring) {
                    row.step = 'skipped'
                    row.detail = t('lineupCatalog_form.alreadyCommittedNoMonitor')
                    continue
                }
                // 预判监控能力：勾选且 preview 明确可监控才发 ReadyForMonitoring，
                // InstanceDB 未确认（canCommitForMonitoring=false）直接降级只存结构，不发注定失败的请求
                const wantMonitoring = enableMonitoring.value && Boolean(preview.canCommitForMonitoring)
                const commitMode = wantMonitoring ? 'ReadyForMonitoring' : 'StructureOnly'
                row.detail = wantMonitoring ? t('lineupCatalog_form.savingWithMonitor') : t('lineupCatalog_form.savingNoMonitor')
                const commitPayload = {
                    previewRevision: preview.previewRevision,
                    previewHash: preview.previewHash,
                    diffToken: preview.diffToken,
                    hierarchyDecisions: [],
                    displayNameChanges: [],
                }
                try {
                    await lineUpImportApi.commit(batchId, { commitMode, ...commitPayload })
                    row.step = 'done'
                    if (wantMonitoring) {
                        row.detail = alreadyCommitted ? t('lineupCatalog_form.structureUnchangedMonitorOn') : t('lineupCatalog_form.structureSavedMonitorOn')
                    }
                    else {
                        row.detail = enableMonitoring.value ? t('lineupCatalog_form.structureSavedMonitorBlocked') : t('lineupCatalog_form.structureSavedNoMonitor')
                    }
                }
                catch (commitError) {
                    // 竞态兜底：预览后元数据变化导致 InstanceDB 校验失败 → 降级只存结构
                    if (wantMonitoring && errorCodeOf(commitError) === 'RuntimeBindingUnresolved') {
                        await lineUpImportApi.commit(batchId, { commitMode: 'StructureOnly', ...commitPayload })
                        row.step = 'done'
                        row.detail = t('lineupCatalog_form.structureSavedMonitorBlocked')
                        continue
                    }
                    throw commitError
                }
            }
            catch (error) {
                row.step = 'failed'
                row.detail = apiErrorDetail(error, t('lineupCatalog_form.autoPipelineFailed'))
            }
        }
        }
        else {
            // 未勾「保存结构」：不跑预览/提交，仅收集项目（步位注册入队用），监控走独立路径
            for (const block of boundBlocks) {
                projectsToRegister.add(String(block.plcProjectId))
            }
        }

        // ③独立路径：勾监控但没勾保存结构 → 对已保存结构的块直接 Prepare→Validate→Monitoring 开监控
        if (enableMonitoring.value && !autoGenerate.value) {
            await runStandaloneMonitoring(boundBlocks)
        }

        if (autoRegister.value && projectsToRegister.size > 0) {
            autoRegistration.value = { status: 'running', detail: t('lineupCatalog_form.enqueuingRegistration') }
            const parts: string[] = []
            let allOk = true
            for (const projectId of projectsToRegister) {
                try {
                    const resp = await plcUploadApi.rerunGraphStepRegistration(projectId) as { isSuccess?: boolean, backResult?: string }
                    if (resp?.isSuccess === false) {
                        allOk = false
                        parts.push(t('lineupCatalog_form.projectResult', { id: projectId, result: resp.backResult || t('lineupCatalog_form.notQueued') }))
                    }
                    else {
                        parts.push(t('lineupCatalog_form.projectQueued', { id: projectId }))
                    }
                }
                catch {
                    allOk = false
                    parts.push(t('lineupCatalog_form.projectQueueFailed', { id: projectId }))
                }
            }
            autoRegistration.value = { status: allOk ? 'done' : 'failed', detail: parts.join(t('lineupCatalog_form.listSep')) + t('lineupCatalog_form.backgroundNote') }
        }

        const okCount = autoRows.value.filter(r => r.step === 'done').length
        const failCount = autoRows.value.filter(r => r.step === 'failed').length
        const summaryParts: string[] = []
        if (autoGenerate.value) {
            summaryParts.push(t('lineupCatalog_form.summaryStructure', { count: okCount }))
        }
        if (enableMonitoring.value) {
            summaryParts.push(t('lineupCatalog_form.summaryMonitor'))
        }
        if (autoRegister.value && projectsToRegister.size > 0) {
            summaryParts.push(t('lineupCatalog_form.summaryRegister'))
        }
        const summary = summaryParts.length > 0 ? summaryParts.join(t('lineupCatalog_form.commaSep')) : t('lineupCatalog_form.noStepsExecuted')
        if (failCount === 0) {
            ElMessage.success(t('lineupCatalog_form.autoDone', { summary }))
        }
        else {
            ElMessage.warning(t('lineupCatalog_form.autoFinishedWithFailures', { ok: okCount, fail: failCount }))
        }
        emit('changed')
    }
    finally {
        autoRunning.value = false
    }
}
</script>

<template>
    <el-dialog
        :model-value="modelValue"
        :title="$t('lineupCatalog_form.dialogTitle')"
        width="1180px"
        top="4vh"
        append-to-body
        :close-on-click-modal="false"
        @update:model-value="handleUpdateModelValue"
        @closed="handleClose"
    >
        <div v-loading="workspace.bootLoading.value" class="binding-workspace">
            <aside class="tree-pane">
                <div class="pane-head">
                    <span>{{ $t('lineupCatalog_form.treeHead') }}</span>
                    <el-button link type="primary" size="small" :loading="workspace.blocksLoading.value" @click="refresh">
                        {{ $t('lineupCatalog_form.refresh') }}
                    </el-button>
                </div>
                <el-select
                    :model-value="workspace.focusProjectId.value"
                    class="device-filter"
                    size="small"
                    filterable
                    clearable
                    :placeholder="$t('lineupCatalog_form.allPlcDevices')"
                    @update:model-value="workspace.setFocusProject(($event ?? '') as string)"
                >
                    <el-option
                        v-for="project in workspace.projects.value"
                        :key="project.id"
                        :value="project.id"
                        :label="project.name"
                    />
                </el-select>
                <el-tree
                    ref="treeRef"
                    :data="workspace.tree.value"
                    node-key="key"
                    highlight-current
                    default-expand-all
                    :expand-on-click-node="true"
                    @node-click="onNodeClick"
                >
                    <template #default="{ data }">
                        <div class="tree-node" :class="'kind-' + data.kind">
                            <span class="tree-node-label">
                                <el-tag v-if="data.kind === 'station'" size="small" effect="plain" class="kind-tag">
                                    {{ $t('lineupCatalog_form.station') }}
                                </el-tag>
                                <el-tag v-else-if="data.kind === 'substation'" size="small" type="info" effect="plain" class="kind-tag">
                                    {{ data.plcCode || $t('lineupCatalog_form.subStation') }}
                                </el-tag>
                                <el-tag v-else-if="data.kind === 'block'" size="small" type="success" effect="plain" class="kind-tag">
                                    GRAPH
                                </el-tag>
                                <span class="node-text" :title="data.label">{{ data.label }}</span>
                            </span>
                            <span class="tree-node-actions">
                                <el-tag v-if="(data.kind === 'station' || data.kind === 'substation') && data.count" size="small" type="success" effect="light">
                                    {{ data.count }}
                                </el-tag>
                                <el-tooltip v-if="data.kind === 'block'" :content="bindingTooltip(data)" placement="top">
                                    <el-tag
                                        size="small"
                                        :type="data.bindingStatus === 'Manual' ? 'primary' : 'warning'"
                                        effect="light"
                                        class="status-tag"
                                    >
                                        {{ data.bindingStatus === 'Manual' ? $t('lineupCatalog_form.bindingManualTag') : $t('lineupCatalog_form.bindingSuggestedTag') }}
                                    </el-tag>
                                </el-tooltip>
                                <template v-if="data.kind === 'block' && data.bindingStatus === 'Suggested'">
                                    <el-button
                                        class="unbind-button"
                                        type="primary"
                                        link
                                        size="small"
                                        :disabled="workspace.actionLoading.value"
                                        @click.stop="acceptSuggestion(data)"
                                    >
                                        {{ $t('lineupCatalog_form.acceptSuggestionAction') }}
                                    </el-button>
                                    <el-button
                                        class="unbind-button"
                                        type="danger"
                                        link
                                        size="small"
                                        :disabled="workspace.actionLoading.value"
                                        @click.stop="confirmReject(data)"
                                    >
                                        {{ $t('lineupCatalog_form.rejectSuggestionAction') }}
                                    </el-button>
                                </template>
                                <el-button
                                    v-else-if="data.kind === 'block'"
                                    class="unbind-button"
                                    type="danger"
                                    link
                                    size="small"
                                    :disabled="workspace.actionLoading.value"
                                    @click.stop="confirmUnbind(data)"
                                >
                                    {{ $t('lineupCatalog_form.unbindAction') }}
                                </el-button>
                            </span>
                        </div>
                    </template>
                </el-tree>

                <p class="tree-tip">
                    {{ $t('lineupCatalog_form.treeTip') }}
                </p>
            </aside>

            <main class="candidate-pane">
                <div class="pane-head candidate-head">
                    <div class="scope-summary">
                        <span>{{ scopeSummary }}</span>
                        <el-tag v-if="workspace.scope.value.stationCode" size="small" type="success">
                            {{ $t('lineupCatalog_form.bindable') }}
                        </el-tag>
                    </div>
                    <el-input
                        v-model="workspace.keyword.value"
                        clearable
                        :placeholder="$t('lineupCatalog_form.filterByBlockName')"
                        class="keyword-input"
                    />
                </div>

                <el-table
                    v-loading="workspace.blocksLoading.value"
                    :data="workspace.unboundBlocks.value"
                    height="430"
                    stripe
                    border
                    :empty-text="$t('lineupCatalog_form.noUnboundBlocks')"
                    @selection-change="onSelectionChange"
                >
                    <el-table-column type="selection" width="42" />
                    <el-table-column :label="$t('lineupCatalog_form.graphBlockColumn')" min-width="240">
                        <template #default="{ row }">
                            <div class="block-cell">
                                <strong>{{ row.blockName }}</strong>
                                <span>{{ row.blockType }}{{ row.blockNumber ?? '' }}</span>
                            </div>
                        </template>
                    </el-table-column>
                    <el-table-column :label="$t('lineupCatalog_form.plcProjectColumn')" min-width="200" show-overflow-tooltip>
                        <template #default="{ row }">
                            {{ projectLabel(row) }}
                        </template>
                    </el-table-column>
                    <el-table-column :label="$t('lineupCatalog_form.blockNumberColumn')" width="90" align="center">
                        <template #default="{ row }">
                            {{ row.blockNumber ?? '—' }}
                        </template>
                    </el-table-column>
                </el-table>

                <div class="candidate-foot">
                    <span>{{ $t('lineupCatalog_form.selectedCount', { count: workspace.selectedBlockIds.value.length }) }}</span>
                    <el-button
                        type="primary"
                        :disabled="!workspace.canBind.value"
                        :loading="workspace.actionLoading.value"
                        @click="bindNow"
                    >
                        {{ workspace.scope.value.stationCode ? $t('lineupCatalog_form.bindTo', { code: workspace.scope.value.stationCode }) : $t('lineupCatalog_form.selectStationFirst') }}
                    </el-button>
                </div>
            </main>
        </div>

        <!-- 自动化链路进度：① 免上传生成预览 → ② 保存结构（按勾选定监控） → ③ GRAPH 步位注册 -->
        <section v-if="autoRows.length" class="auto-pipeline" :aria-label="$t('lineupCatalog_form.autoProgressAria')">
            <div class="auto-pipeline__head">
                {{ $t('lineupCatalog_form.autoProgressTitle') }}
                <el-tag v-if="autoRunning" size="small" type="primary">{{ $t('lineupCatalog_form.executing') }}</el-tag>
                <el-tag v-else size="small" type="info">{{ $t('lineupCatalog_form.finished') }}</el-tag>
            </div>
            <ul class="auto-pipeline__list">
                <li v-for="row in autoRows" :key="row.blockName" class="auto-pipeline__row">
                    <span class="auto-dot" :class="'is-' + row.step"></span>
                    <span class="auto-name" :title="row.blockName">{{ row.blockName }}</span>
                    <span class="auto-station">{{ row.stationCodes }}</span>
                    <span class="auto-step" :class="'is-' + row.step">{{ autoStepText(row.step) }}</span>
                    <span v-if="row.detail" class="auto-detail" :title="row.detail">{{ row.detail }}</span>
                </li>
                <li v-if="autoRegistration.status !== 'skipped'" class="auto-pipeline__row">
                    <span class="auto-dot" :class="'is-' + autoRegistration.status"></span>
                    <span class="auto-name">{{ $t('lineupCatalog_form.graphStepRegistration') }}</span>
                    <span class="auto-step" :class="'is-' + autoRegistration.status">{{ autoRegistration.status === 'running' ? $t('lineupCatalog_form.enqueueing') : (autoRegistration.status === 'done' ? $t('lineupCatalog_form.enqueued') : $t('lineupCatalog_form.stepDone')) }}</span>
                    <span v-if="autoRegistration.detail" class="auto-detail" :title="autoRegistration.detail">{{ autoRegistration.detail }}</span>
                </li>
            </ul>
        </section>

        <template #footer>
            <div class="dialog-footer">
                <div class="footer-tip">
                    {{ $t('lineupCatalog_form.footerTip') }}
                </div>
                <div class="footer-main">
                    <div class="monitor-block">
                        <el-checkbox v-model="autoGenerate" :disabled="autoRunning">
                            {{ $t('lineupCatalog_form.generatePreviewSaveStructure') }}
                        </el-checkbox>
                        <el-checkbox v-model="autoRegister" :disabled="autoRunning">
                            {{ $t('lineupCatalog_form.graphStepRegisterShort') }}
                        </el-checkbox>
                        <el-checkbox v-model="enableMonitoring" class="monitor-switch" :disabled="autoRunning">
                            {{ $t('lineupCatalog_form.stepMonitor') }}
                        </el-checkbox>
                    </div>
                    <div class="footer-actions">
                        <el-button @click="handleClose">
                            {{ $t('common.close') }}
                        </el-button>
                        <el-tooltip :content="$t('lineupCatalog_form.rerunTooltip')" :disabled="canRerun" placement="top">
                            <span>
                                <el-button :disabled="!canRerun" :loading="workspace.actionLoading.value" @click="rerunRegistration">
                                    {{ $t('lineupCatalog_form.rerunTitle') }}
                                </el-button>
                            </span>
                        </el-tooltip>
                        <el-button type="primary" :disabled="!canRunAuto" :loading="autoRunning" @click="runAutoPipeline">
                            {{ executeLabel }}
                        </el-button>
                    </div>
                </div>
            </div>
        </template>
    </el-dialog>
</template>

<style scoped>
.binding-workspace {
    display: grid;
    grid-template-columns: 380px minmax(0, 1fr);
    gap: 12px;
    min-height: 520px;
}

/* 自动化链路进度面板 */
.auto-pipeline {
    margin-top: 10px;
    padding: 8px 12px;
    border: 1px solid var(--el-border-color-light);
    border-radius: 8px;
    background: var(--el-fill-color-lighter);
}

.auto-pipeline__head {
    display: flex;
    gap: 8px;
    align-items: center;
    font-size: 12px;
    font-weight: 600;
    color: var(--el-text-color-primary);
    margin-bottom: 6px;
}

.auto-pipeline__list {
    margin: 0;
    padding: 0;
    list-style: none;
    max-height: 150px;
    overflow: auto;
}

.auto-pipeline__row {
    display: flex;
    gap: 8px;
    align-items: center;
    font-size: 12px;
    padding: 2px 0;
    min-width: 0;
}

.auto-dot {
    flex: none;
    width: 8px;
    height: 8px;
    border-radius: 50%;
    background: var(--el-text-color-placeholder);
}

.auto-dot.is-preview,
.auto-dot.is-commit,
.auto-dot.is-monitor {
    background: var(--el-color-primary);
    animation: auto-pulse 1.2s ease-in-out infinite;
}

.auto-dot.is-done {
    background: var(--el-color-success);
}

.auto-dot.is-failed {
    background: var(--el-color-danger);
}

.auto-dot.is-skipped {
    background: var(--el-border-color);
}

@keyframes auto-pulse {
    0%, 100% { opacity: 1; }
    50% { opacity: 0.35; }
}

.auto-name {
    flex: none;
    max-width: 220px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-family: Consolas, monospace;
    color: var(--el-text-color-primary);
}

.auto-station {
    flex: none;
    color: var(--el-text-color-secondary);
}

.auto-step {
    flex: none;
    color: var(--el-text-color-secondary);
}

.auto-step.is-done {
    color: var(--el-color-success);
}

.auto-step.is-failed {
    color: var(--el-color-danger);
}

.auto-detail {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    color: var(--el-text-color-placeholder);
}

.tree-pane,
.candidate-pane {
    display: flex;
    flex-direction: column;
    gap: 8px;
    min-width: 0;
    padding: 10px;
    background: var(--el-fill-color-lighter);
    border: 1px solid var(--el-border-color-light);
    border-radius: 8px;
}

.pane-head {
    display: flex;
    gap: 10px;
    align-items: center;
    justify-content: space-between;
    font-size: 13px;
    color: var(--el-text-color-secondary);
}

.device-filter {
    width: 100%;
    flex-shrink: 0;
}

.tree-pane :deep(.el-tree) {
    flex: 1;
    overflow: auto;
    background: transparent;
    --el-tree-node-content-height: 30px;
}

.tree-node {
    display: flex;
    flex: 1;
    gap: 6px;
    align-items: center;
    justify-content: space-between;
    min-width: 0;
}

.tree-node-label {
    display: flex;
    gap: 4px;
    align-items: center;
    min-width: 0;
}

.kind-tag {
    flex: none;
}

.node-text {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}

.unbind-button {
    flex: none;
}

.unbind-button + .unbind-button {
    margin-left: 0;
}

.status-tag {
    flex: none;
}

.kind-block .node-text {
    font-size: 12px;
    color: var(--el-text-color-regular);
}

.tree-node-actions {
    display: flex;
    flex: none;
    gap: 4px;
    align-items: center;
}

.tree-tip {
    margin: 0;
    font-size: 12px;
    line-height: 1.5;
    color: var(--el-text-color-secondary);
}

.candidate-head {
    align-items: center;
}

.scope-summary {
    display: flex;
    gap: 8px;
    align-items: center;
    font-weight: 500;
    color: var(--el-text-color-primary);
}

.keyword-input {
    width: 220px;
}

.candidate-pane :deep(.el-table) {
    flex: 1;
}

.block-cell {
    display: grid;
    gap: 2px;
}

.block-cell span {
    font-size: 12px;
    color: var(--el-text-color-secondary);
}

.candidate-foot {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding-top: 4px;
    font-size: 13px;
    color: var(--el-text-color-secondary);
}

.dialog-footer {
    display: flex;
    flex-direction: column;
    gap: 8px;
    align-items: stretch;
}

.footer-tip {
    max-width: 640px;
    font-size: 12px;
    line-height: 1.5;
    text-align: left;
    color: var(--el-text-color-secondary);
}

.footer-main {
    display: flex;
    gap: 16px;
    align-items: center;
    justify-content: space-between;
}

.monitor-block {
    display: flex;
    flex-direction: column;
    gap: 2px;
    align-items: flex-start;
}

.footer-actions {
    display: flex;
    gap: 8px;
    flex: none;
    align-items: center;
}

.monitor-switch {
    flex: none;
    height: auto;
}

.monitor-hint {
    font-size: 12px;
    line-height: 1.4;
    color: var(--el-text-color-secondary);
}

@media (width <= 1100px) {
    .binding-workspace {
        grid-template-columns: 1fr;
    }
}
</style>
