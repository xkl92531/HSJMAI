<script setup lang="ts">
import { ref, onUnmounted, computed } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import type { UploadInstance, UploadProps, UploadUserFile } from 'element-plus'
import UploadAPI from '@/api/modules/PLC/upload'
import GraphStationBindingDialog from '@/views/LineUp/catalog/components/GraphStationBindingDialog.vue'
import {
    graphSummaryPresentation,
    postProcessStatusText,
    postProcessTagType,
    uploadPollAction
} from './projectUploadPresentation'
import type { UploadGraphSummary, UploadPostProcess } from './projectUploadPresentation'
import { useI18n } from 'vue-i18n'
import messages from './projectUpload.lang'

const props = defineProps<{
    modelValue: boolean
    projectId: string | number
}>()

const emit = defineEmits(['update:modelValue', 'success'])

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}

// 进度状态接口
interface UploadProgress {
    taskId: string
    projectId: number
    fileName: string
    status: number // 0=Pending, 1=Processing, 2=Saving, 3=Completed, 4=Failed
    totalFiles: number
    processedFiles: number
    currentFile: string | null
    percentage: number
    message: string
    startTime: string
    endTime: string | null
    errorDetails: string | null
    resultProjectId: string | null
    graphSummary?: UploadGraphSummary | null
    // 上传完成后的后处理任务状态（标签增量注册等）：完成态继续轮询直到全部终态
    postProcesses?: UploadPostProcess[] | null
}

const uploadRef = ref<UploadInstance>()
const fileList = ref<UploadUserFile[]>([])
const uploading = ref(false)
const taskId = ref<string | null>(null)
const progress = ref<UploadProgress | null>(null)
const pollTimer = ref<number | null>(null)

// 完成态后处理轮询（路线 A 2026-09-11）：上传主体 Completed 但 postProcesses 还有 Running 时
// 继续轮询，直到全部终态或超过截止时间；完成侧效果（成功提示/绑定弹框）只执行一次。
// 2026-10-09 上传约束后台化：约束生成（xmlv2-constraint 槽）是 XMLV2 整项目发布，AI 缓存冷时
// 为分钟级——轮询上限从 3 分钟放宽到 30 分钟；超时停止轮询但界面保持显示（后端进度 2 小时内可重开查看）。
const completionHandled = ref(false)
const postProcessDeadline = ref<number | null>(null)
const postProcessNotified = new Set<string>()
const POST_PROCESS_POLL_LIMIT_MS = 1_800_000

// 上传完成后弹出「批量绑定 GRAPH 与工站」弹框：有 GRAPH 产出时自动弹，也可手动打开
const bindingVisible = ref(false)
const bindingProjectId = computed(() => {
    const id = progress.value?.resultProjectId || String(props.projectId || '')
    return id || ''
})

// 状态文本映射
const statusText = computed(() => {
    if (!progress.value) return ''
    const statusMap: Record<number, string> = {
        0: t('projectUpload.statusPending'),
        1: t('projectUpload.statusParsing'),
        2: t('projectUpload.statusSaving'),
        3: t('projectUpload.statusCompleted'),
        4: t('projectUpload.statusFailed')
    }
    return statusMap[progress.value.status] || t('projectUpload.statusUnknown')
})

// 状态类型（用于 el-progress）
const progressStatus = computed(() => {
    if (!progress.value) return undefined
    if (progress.value.status === 3) return 'success'
    if (progress.value.status === 4) return 'exception'
    return undefined
})

// 是否正在处理中
const isProcessing = computed(() => {
    return progress.value && [0, 1, 2].includes(progress.value.status)
})

const graphSummaryView = computed(() => {
    return progress.value?.graphSummary
        ? graphSummaryPresentation(progress.value.graphSummary)
        : null
})

const handleClose = () => {
    stopPolling()
    emit('update:modelValue', false)
    fileList.value = []
    taskId.value = null
    progress.value = null
    uploading.value = false
    // 危险选项不跨会话残留：每次关闭对话框后复位为默认不勾选
    fullProjectUpdate.value = false
    // 后处理轮询状态复位
    completionHandled.value = false
    postProcessDeadline.value = null
    postProcessNotified.clear()
}

const stopPolling = () => {
    if (pollTimer.value) {
        clearInterval(pollTimer.value)
        pollTimer.value = null
    }
}

const pollProgress = async () => {
    if (!taskId.value) return

    try {
        const res = await UploadAPI.getProgress(taskId.value) as any
        if (res.isSuccess && res.backResult) {
            progress.value = res.backResult

            // 后处理转失败时即时提醒一次（如 标签增量注册 失败）
            for (const item of progress.value?.postProcesses ?? []) {
                if (item.status === 'Failed' && !postProcessNotified.has(item.key)) {
                    postProcessNotified.add(item.key)
                    ElMessage.error(t('projectUpload.postProcessFailed', { title: item.title, detail: item.detail || t('projectUpload.seeBackendLog') }))
                }
            }

            // 处理完成或失败时停止轮询（除非后处理仍在跑——见下）。
            // 2026-10-09 上传约束后台化：上传 Completed 时约束生成（xmlv2-constraint 槽）可能仍在
            // 后台执行，约束发布失败以槽 Failed 呈现并弹错（保存包保留，可用「重置规则」重试）。
            const action = uploadPollAction(progress.value?.status, progress.value?.postProcesses)
            if (action === 'continue' || !progress.value) {
                return
            }
            if (action === 'completed-wait') {
                // 路线 A：完成态但后处理（标签增量注册等）仍在后台执行——继续轮询直到终态/超时
                notifyCompleted()
                if (postProcessDeadline.value === null) {
                    postProcessDeadline.value = Date.now() + POST_PROCESS_POLL_LIMIT_MS
                }
                if (Date.now() > postProcessDeadline.value) {
                    // 超时仍在跑：停止轮询，界面保持显示"进行中"（后端 2 小时内可重开进度）
                    stopPolling()
                }
                return
            }

            stopPolling()
            uploading.value = false
            if (action === 'completed') {
                // 成功后不再自动关闭：保留进度区展示最终结果，由用户确认后手动关闭
                notifyCompleted()
            } else {
                ElMessage.error(progress.value.errorDetails || t('projectUpload.uploadHandleFailed'))
            }
        }
    } catch (error) {
        console.error('轮询进度失败:', error)
    }
}

// 完成侧效果（成功提示 / 刷新 / GRAPH 绑定弹框）只执行一次
const notifyCompleted = () => {
    if (completionHandled.value || !progress.value) return
    completionHandled.value = true
    uploading.value = false
    ElMessage.success(t('projectUpload.uploadParseDone'))
    emit('success')
    // 识别到 GRAPH 块时自动弹出工站绑定弹框，让用户完成/确认绑定关系
    if (progress.value.graphSummary && progress.value.graphSummary.detectedBlockCount > 0) {
        bindingVisible.value = true
    }
}

const startPolling = () => {
    stopPolling()
    // 立即查询一次
    pollProgress()
    // 每 1.5 秒查询一次
    pollTimer.value = window.setInterval(pollProgress, 1500)
}

// 全项目更新模式（默认不勾选）：勾选后对比数据库与 zip 文件，
// 数据库中存在但 zip 中缺少的程序块视为已删除，其全部关联数据将被清理。
const fullProjectUpdate = ref(false)

const submitUpload = async () => {
    if (fileList.value.length === 0) {
        ElMessage.warning(t('projectUpload.selectFileFirst'))
        return
    }
    if (!props.projectId) {
        ElMessage.warning(t('projectUpload.projectIdMissing'))
        return 
    }

    // 全项目更新模式：危险操作二次确认（默认不勾选，勾选必须显式确认）
    if (fullProjectUpdate.value) {
        try {
            await ElMessageBox.confirm(
                t('projectUpload.fullUpdateConfirmMsg'),
                t('projectUpload.fullUpdateConfirmTitle'),
                {
                    confirmButtonText: t('projectUpload.fullUpdateConfirmBtn'),
                    cancelButtonText: t('common.cancel'),
                    type: 'warning',
                    confirmButtonClass: 'el-button--danger'
                }
            )
        } catch {
            return // 用户取消
        }
    }

    uploading.value = true
    progress.value = null
    taskId.value = null
    // 后处理轮询状态复位（新一轮上传）
    completionHandled.value = false
    postProcessDeadline.value = null
    postProcessNotified.clear()

    try {
        const formData = new FormData()
        const rawFile = fileList.value[0].raw
        if (rawFile) {
            formData.append('file', rawFile)
            formData.append('ProjectId', String(props.projectId))
            formData.append('FullProjectUpdate', String(fullProjectUpdate.value))

            // 使用异步后台上传
            const res = await UploadAPI.startBackgroundUpload(formData) as any
            if (res.isSuccess && res.backResult) {
                taskId.value = res.backResult.taskId
                ElMessage.info(t('projectUpload.taskStarted'))
                
                // 初始化进度
                progress.value = {
                    taskId: taskId.value!,
                    projectId: Number(props.projectId),
                    fileName: rawFile.name,
                    status: 0,
                    totalFiles: 0,
                    processedFiles: 0,
                    currentFile: null,
                    percentage: 0,
                    message: t('projectUpload.waitingProcess'),
                    startTime: new Date().toISOString(),
                    endTime: null,
                    errorDetails: null,
                    resultProjectId: null,
                    graphSummary: null
                }
                
                // 开始轮询进度
                startPolling()
            } else {
                ElMessage.error(res.errorMessage || t('projectUpload.startUploadFailed'))
                uploading.value = false
            }
        }
    } catch (error) {
        console.error(error)
        ElMessage.error(t('projectUpload.uploadError'))
        uploading.value = false
    }
}

const handleExceed: UploadProps['onExceed'] = (files) => {
    uploadRef.value!.clearFiles()
    const file = files[0] as UploadUserFile
    file.uid = Date.now()
    uploadRef.value!.handleStart(file)
}

// 组件卸载时清理
onUnmounted(() => {
    stopPolling()
})
</script>

<template>
    <el-dialog
        :model-value="modelValue"
        :title="$t('projectUpload.uploadDialogTitle')"
        width="550px"
        :close-on-click-modal="!isProcessing"
        :close-on-press-escape="!isProcessing"
        :show-close="!isProcessing"
        @close="handleClose"
    >
        <div class="flex flex-col gap-4">
            <el-alert
                :title="$t('projectUpload.asyncParseTip')"
                type="info"
                show-icon
                :closable="false"
            />
            
            <!-- 文件选择区域 -->
            <el-upload
                v-if="!isProcessing"
                ref="uploadRef"
                v-model:file-list="fileList"
                class="upload-demo"
                action="#"
                :auto-upload="false"
                :limit="1"
                :on-exceed="handleExceed"
                accept=".zip"
                drag
            >
                <el-icon class="el-icon--upload"><i-ep-upload-filled /></el-icon>
                <div class="el-upload__text">
                    {{ $t('projectUpload.dragHere') }}<em>{{ $t('projectUpload.clickUpload') }}</em>
                </div>
                <template #tip>
                    <div class="el-upload__tip">
                        {{ $t('projectUpload.zipTip') }}
                    </div>
                </template>
            </el-upload>

            <!-- 全项目更新模式（默认不勾选）：勾选后对比清理缺失块 -->
            <div v-if="!isProcessing" class="full-update-option">
                <el-checkbox v-model="fullProjectUpdate">
                    {{ $t('projectUpload.fullUpdateMode') }}
                </el-checkbox>
                <el-alert
                    v-if="fullProjectUpdate"
                    :title="$t('projectUpload.dangerTitle')"
                    type="warning"
                    show-icon
                    :closable="false"
                >
                    <div>{{ $t('projectUpload.fullUpdateDangerDesc') }}</div>
                    <div style="font-weight: bold; margin-top: 4px;">{{ $t('projectUpload.fullUpdateConfirmWarning') }}</div>
                </el-alert>
                <div v-else class="el-upload__tip" style="margin-top: 2px;">
                    {{ $t('projectUpload.incrementalModeTip') }}
                </div>
            </div>

            <!-- 进度显示区域 -->
            <div v-if="progress" class="progress-container">
                <div class="progress-header">
                    <span class="file-name">{{ progress.fileName }}</span>
                    <el-tag :type="progress.status === 4 ? 'danger' : progress.status === 3 ? 'success' : 'primary'" size="small">
                        {{ statusText }}
                    </el-tag>
                </div>
                
                <el-progress 
                    :percentage="progress.percentage" 
                    :status="progressStatus"
                    :stroke-width="18"
                    :text-inside="true"
                />
                
                <div class="progress-details">
                    <div class="message">{{ progress.message }}</div>
                    <div v-if="progress.totalFiles > 0" class="file-count">
                        {{ $t('projectUpload.fileCount', { processed: progress.processedFiles, total: progress.totalFiles }) }}
                    </div>
                    <div v-if="progress.currentFile" class="current-file">
                        {{ $t('projectUpload.currentFileLabel', { name: progress.currentFile }) }}
                    </div>
                </div>

                <el-alert
                    v-if="graphSummaryView"
                    :title="graphSummaryView.text"
                    :type="graphSummaryView.type"
                    show-icon
                    :closable="false"
                    class="mt-3"
                />

                <!-- 错误信息 -->
                <el-alert
                    v-if="progress.status === 4 && progress.errorDetails"
                    :title="progress.errorDetails"
                    type="error"
                    show-icon
                    :closable="false"
                    class="mt-3"
                />

                <!-- 完成结果摘要（后端拼好的增量结果：跳过/更新/清理统计） -->
                <el-alert
                    v-if="progress.status === 3 && progress.message"
                    :title="progress.message"
                    type="success"
                    show-icon
                    :closable="false"
                    class="mt-3"
                />

                <!-- 后处理任务状态（标签增量注册等）：完成态继续轮询推进到终态 -->
                <div
                    v-if="progress.status === 3 && progress.postProcesses?.length"
                    class="post-process"
                >
                    <div
                        v-for="item in progress.postProcesses"
                        :key="item.key"
                        class="post-process-item"
                        :class="{ 'is-failed': item.status === 'Failed' }"
                    >
                        <el-tag size="small" :type="postProcessTagType(item.status)">
                            {{ postProcessStatusText(item.status) }}
                        </el-tag>
                        <span class="post-process-title">{{ item.title }}</span>
                        <span v-if="item.detail" class="post-process-detail">{{ item.detail }}</span>
                    </div>
                </div>
            </div>
        </div>

        <template #footer>
            <span class="dialog-footer">
                <el-button v-if="progress && progress.status === 3" type="primary" plain @click="bindingVisible = true">
                    {{ $t('projectUpload.bindGraphStation') }}
                </el-button>
                <el-button :disabled="isProcessing" @click="handleClose">
                    {{ isProcessing ? $t('projectUpload.processingBtn') : (progress && progress.status === 3 ? $t('common.close') : $t('common.cancel')) }}
                </el-button>
                <el-button 
                    type="primary" 
                    :loading="uploading && !progress" 
                    :disabled="isProcessing"
                    @click="submitUpload"
                >
                    {{ isProcessing ? $t('projectUpload.uploadingBtn') : $t('projectUpload.uploadParseBtn') }}
                </el-button>
            </span>
        </template>
    </el-dialog>

    <GraphStationBindingDialog
        v-model="bindingVisible"
        :initial-project-id="bindingProjectId"
    />
</template>

<style scoped>
.full-update-option {
    margin-top: 4px;
}

.full-update-option :deep(.el-alert) {
    margin-top: 8px;
}

.progress-container {
    background: var(--el-fill-color-light);
    border-radius: 8px;
    padding: 16px;
}

.progress-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 12px;
}

.file-name {
    font-weight: 500;
    color: var(--el-text-color-primary);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    max-width: 300px;
}

.progress-details {
    margin-top: 12px;
    font-size: 13px;
    color: var(--el-text-color-secondary);
}

.progress-details .message {
    font-weight: 500;
    color: var(--el-text-color-primary);
    margin-bottom: 4px;
}

.progress-details .file-count,
.progress-details .current-file {
    margin-top: 4px;
}

/* 后处理任务状态（标签增量注册等） */
.post-process {
    margin-top: 12px;
    display: flex;
    flex-direction: column;
    gap: 6px;
}

.post-process-item {
    display: flex;
    align-items: baseline;
    gap: 8px;
    font-size: 13px;
    line-height: 22px;
}

.post-process-item .el-tag {
    flex-shrink: 0;
    align-self: center;
}

.post-process-title {
    font-weight: 500;
    color: var(--el-text-color-primary);
    flex-shrink: 0;
}

.post-process-detail {
    color: var(--el-text-color-secondary);
    word-break: break-all;
}

.post-process-item.is-failed .post-process-detail {
    color: var(--el-color-danger);
}

.upload-demo {
    width: 100%;
}

:deep(.el-upload) {
    width: 100%;
}

:deep(.el-upload-dragger) {
    width: 100%;
}
</style>
