<script setup lang="ts">
import { ref, onUnmounted, computed } from 'vue'
import { ElMessage } from 'element-plus'
import UploadAPI from '@/api/modules/PLC/upload'
import variableAccess from '@/api/modules/AI_PLC/variableAccess'
import { useI18n } from 'vue-i18n'
import messages from './analyzeProgress.lang'

const props = defineProps<{
    modelValue: boolean
    projectId: string | number
}>()

const emit = defineEmits(['update:modelValue', 'success'])

interface AnalyzeProgress {
    taskId: string
    projectId: number
    projectName: string
    status: number
    totalTags: number
    processedTags: number
    currentTag: string | null
    percentage: number
    message: string
    startTime: string
    endTime: string | null
    errorDetails: string | null
    currentFile: string | null
}

const { t, mergeLocaleMessage } = useI18n()

// 遍历语言包并合并到当前实例
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}

const analyzing = ref(false)
const taskId = ref<string | null>(null)
const progress = ref<AnalyzeProgress | null>(null)
const pollTimer = ref<number | null>(null)

const statusText = computed(() => {
    if (!progress.value) return ''
    const statusMap: Record<number, string> = {
        0: t('analyzeProgress.statusPending'),
        1: t('analyzeProgress.statusAnalyzing'),
        2: t('analyzeProgress.statusSaving'),
        3: t('analyzeProgress.statusCompleted'),
        4: t('analyzeProgress.statusFailed')
    }
    return statusMap[progress.value.status] || t('analyzeProgress.statusUnknown')
})

const progressStatus = computed(() => {
    if (!progress.value) return undefined
    if (progress.value.status === 3) return 'success'
    if (progress.value.status === 4) return 'exception'
    return undefined
})

const isProcessing = computed(() => {
    return progress.value && [0, 1, 2].includes(progress.value.status)
})

const handleClose = () => {
    stopPolling()
    emit('update:modelValue', false)
    taskId.value = null
    progress.value = null
    analyzing.value = false
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

            if (progress.value && [3, 4].includes(progress.value.status)) {
                stopPolling()
                analyzing.value = false

                if (progress.value.status === 3) {
                    ElMessage.success(t('analyzeProgress.analyzeDone'))
                    setTimeout(() => {
                        emit('success')
                        handleClose()
                    }, 1500)
                } else if (progress.value.status === 4) {
                    ElMessage.error(progress.value.errorDetails || t('analyzeProgress.analyzeFailed'))
                }
            }
        }
    } catch (error) {
        console.error('轮询进度失败:', error)
    }
}

const startPolling = () => {
    stopPolling()
    pollProgress()
    pollTimer.value = window.setInterval(pollProgress, 1500)
}

const startAnalyze = async () => {
    if (!props.projectId) {
        ElMessage.warning(t('analyzeProgress.projectIdMissing'))
        return
    }

    analyzing.value = true
    progress.value = null
    taskId.value = null

    try {
        const res = await variableAccess.analyzeTagAttributes(props.projectId) as any
        if (res.isSuccess && res.backResult) {
          console.log("xxxxxxxxxxxxxxxx",res.backResult)
            taskId.value = res.backResult.taskId
            ElMessage.info(t('analyzeProgress.taskStarted'))
            progress.value = {
                taskId: taskId.value!,
                projectId: Number(props.projectId),
                projectName: res.backResult.projectName || '',
                status: 0,
                totalTags: 0,
                processedTags: 0,
                currentFile: null,
                percentage: 0,
                message: t('analyzeProgress.waitingProcess'),
                startTime: new Date().toISOString(),
                endTime: null,
                errorDetails: null
            }

            startPolling()
        } else {
            ElMessage.error(res.errorMessage || t('analyzeProgress.startFailed'))
            analyzing.value = false
        }
    } catch (error) {
        console.error(error)
        ElMessage.error(t('analyzeProgress.startError'))
        analyzing.value = false
    }
}

const initAndStart = () => {
    startAnalyze()
}

onUnmounted(() => {
    stopPolling()
})
</script>

<template>
    <el-dialog
        :model-value="modelValue"
        :title="$t('analyzeProgress.title')"
        width="550px"
        :close-on-click-modal="!isProcessing"
        :close-on-press-escape="!isProcessing"
        :show-close="!isProcessing"
        @close="handleClose"
        @open="initAndStart"
    >
        <div class="flex flex-col gap-4">
            <el-alert
                :title="$t('analyzeProgress.asyncTip')"
                type="info"
                show-icon
                :closable="false"
            />

            <div v-if="progress" class="progress-container">
                <div class="progress-header">
                    <span class="project-name">{{ progress.projectName || $t('analyzeProgress.projectFallback') }}</span>
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
                    <div v-if="progress.totalTags > 0" class="tag-count">
                        {{ $t('analyzeProgress.tagCount', { processed: progress.processedTags, total: progress.totalTags }) }}
                    </div>
                    <div v-if="progress.currentFile" class="current-tag">
                        {{ $t('analyzeProgress.currentFile', { name: progress.currentFile }) }}
                    </div>
                </div>

                <el-alert
                    v-if="progress.status === 4 && progress.errorDetails"
                    :title="progress.errorDetails"
                    type="error"
                    show-icon
                    :closable="false"
                    class="mt-3"
                />
            </div>

            <div v-else-if="analyzing" class="progress-container">
                <div class="text-center text-gray-500">
                    {{ $t('analyzeProgress.starting') }}
                </div>
            </div>
        </div>

        <template #footer>
            <span class="dialog-footer">
                <el-button :disabled="isProcessing" @click="handleClose">
                    {{ isProcessing ? $t('analyzeProgress.processing') : $t('analyzeProgress.cancel') }}
                </el-button>
            </span>
        </template>
    </el-dialog>
</template>

<style scoped>
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

.project-name {
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

.progress-details .tag-count,
.progress-details .current-tag {
    margin-top: 4px;
}
</style>
