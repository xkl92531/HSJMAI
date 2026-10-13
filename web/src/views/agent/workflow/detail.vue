<route lang="yaml">
meta:
  title: 工作流详情
  icon: i-carbon:flow
</route>

<script setup lang="ts">
import { ElMessage } from 'element-plus'
import { useRoute, useRouter } from 'vue-router'
import * as signalR from '@microsoft/signalr'
import apiWorkflow from '@/api/modules/agent/workflow'
import { useI18n } from 'vue-i18n'
import messages from './components/lang'

const { t, mergeLocaleMessage } = useI18n()
// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

defineOptions({
  name: 'WorkflowDetail',
})

const route = useRoute()
const router = useRouter()

// 基础状态
const loading = ref(false)
const workflowId = ref<string>('')
const workflow = ref<any>(null)
const tasks = ref<any[]>([])

// SignalR 连接状态
const connectionStatus = ref('Disconnected')
let connection: signalR.HubConnection | null = null

// 实时进度状态
const currentStage = ref('')
const currentMessage = ref('')
const executionLogs = ref<any[]>([])

// 计算属性
const progressPercentage = computed(() => {
  const stageProgress: Record<string, number> = {
    Started: 10,
    Planning: 15,
    PlanGenerated: 25,
    TasksCreated: 40,
    Executing: 60,
    Completed: 100,
    Failed: 100,
  }
  return stageProgress[currentStage.value] || 0
})

const progressStatus = computed(() => {
  if (currentStage.value === 'Failed') return 'exception'
  if (currentStage.value === 'Completed') return 'success'
  return undefined
})

// 生命周期
onMounted(async () => {
  workflowId.value = route.query.id as string
  if (!workflowId.value) {
    ElMessage.error(t('agentWorkflow.missingWorkflowId'))
    router.push('/agent/workflow/list')
    return
  }
  await loadWorkflowDetail()
  await connectSignalR()
})

onUnmounted(() => {
  disconnectSignalR()
})

// 加载工作流详情
async function loadWorkflowDetail() {
  loading.value = true
  try {
    const res = await apiWorkflow.getDetail(workflowId.value)
    workflow.value = res.workflow
    tasks.value = res.tasks || []
    // 根据当前状态设置进度
    if (workflow.value) {
      currentStage.value = getStageFromStatus(workflow.value.status)
    }
  } catch (error: any) {
    ElMessage.error(error.message || t('agentWorkflow.loadFailed'))
  } finally {
    loading.value = false
  }
}

function getStageFromStatus(status: number): string {
  const stageMap: Record<number, string> = {
    0: 'Pending',
    1: 'Planning',
    2: 'Executing',
    3: 'Completed',
    4: 'Failed',
    5: 'Cancelled',
  }
  return stageMap[status] || 'Unknown'
}

// SignalR 连接
async function connectSignalR() {
  try {
    const apiUrl = import.meta.env.DEV
      ? import.meta.env.VITE_APP_API_BASEURL
      : (window as any).apiConfig?.apiUrl || ''

    connection = new signalR.HubConnectionBuilder()
      .withUrl(`${apiUrl}/hubs/agent-streaming`, {
        withCredentials: false,
        skipNegotiation: false,
      })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(signalR.LogLevel.Information)
      .build()

    // 监听重连事件
    connection.onreconnecting(() => {
      connectionStatus.value = 'Reconnecting'
    })

    connection.onreconnected(() => {
      connectionStatus.value = 'Connected'
      subscribeToWorkflow()
    })

    connection.onclose(() => {
      connectionStatus.value = 'Disconnected'
    })

    // 注册事件处理器
    registerSignalRHandlers()

    // 启动连接
    await connection.start()
    connectionStatus.value = 'Connected'

    // 订阅工作流
    subscribeToWorkflow()
  } catch (error) {
    console.error('SignalR连接失败:', error)
    connectionStatus.value = 'Error'
  }
}

function subscribeToWorkflow() {
  if (connection && workflowId.value) {
    // 使用 JoinWorkflowGroup 方法（后端 AgentStreamingHub 中的方法名）
    connection.invoke('JoinWorkflowGroup', workflowId.value)
      .catch(err => console.error('订阅失败:', err))
  }
}

function disconnectSignalR() {
  if (connection) {
    connection.stop()
    connection = null
  }
}

function registerSignalRHandlers() {
  if (!connection) return

  // 工作流进度
  connection.on('WorkflowProgress', (data: any) => {
    currentStage.value = data.stage
    currentMessage.value = data.message
    addLog('progress', data.stage, data.message, data.data)
    refreshTasks()
  })

  // 工作流开始
  connection.on('WorkflowStarted', (data: any) => {
    currentStage.value = 'Started'
    currentMessage.value = t('agentWorkflow.workflowStarted')
    addLog('info', 'Started', t('agentWorkflow.workflowStarted'), data)
  })

  // 规划开始
  connection.on('PlanningStarted', (data: any) => {
    currentStage.value = 'Planning'
    currentMessage.value = t('agentWorkflow.planningTask')
    addLog('info', 'Planning', t('agentWorkflow.planningTaskLog'), data)
  })

  // 规划完成
  connection.on('PlanGenerated', (data: any) => {
    currentStage.value = 'PlanGenerated'
    currentMessage.value = t('agentWorkflow.stepsGenerated', { n: data.stepCount || 0 })
    addLog('success', 'PlanGenerated', t('agentWorkflow.stepsGenerated', { n: data.stepCount || 0 }), data)
    refreshTasks()
  })

  // 步骤开始
  connection.on('StepStarted', (data: any) => {
    currentStage.value = 'Executing'
    currentMessage.value = t('agentWorkflow.executingStep', { name: data.stepName || data.taskName || t('agentWorkflow.taskFallback') })
    addLog('info', 'StepStarted', t('agentWorkflow.startExecuting', { name: data.stepName || data.taskName }), data)
    refreshTasks()
  })

  // 步骤完成
  connection.on('StepCompleted', (data: any) => {
    addLog('success', 'StepCompleted', t('agentWorkflow.completedStep', { name: data.stepName || data.taskName }), data)
    refreshTasks()
  })

  // 任务状态变更
  connection.on('TaskStatusChanged', (data: any) => {
    addLog('info', 'TaskStatus', `${data.taskName}: ${data.statusText}`, data)
    refreshTasks()
  })

  // 工作流完成
  connection.on('WorkflowCompleted', (data: any) => {
    currentStage.value = 'Completed'
    currentMessage.value = t('agentWorkflow.workflowExecCompleted')
    addLog('success', 'Completed', t('agentWorkflow.workflowExecCompleted'), data)
    loadWorkflowDetail() // 刷新完整数据
  })

  // 工作流失败
  connection.on('WorkflowFailed', (data: any) => {
    currentStage.value = 'Failed'
    currentMessage.value = t('agentWorkflow.execFailed', { msg: data.error })
    addLog('error', 'Failed', t('agentWorkflow.execFailed', { msg: data.error }), data)
    loadWorkflowDetail()
  })

  // 智能体思考过程
  connection.on('AgentThinking', (data: any) => {
    const agentName = data.agentName || t('agentWorkflow.agentFallback')
    const thought = data.thought || t('agentWorkflow.thinkingFallback')
    addLog('info', 'Thinking', `${agentName}: ${thought}`, data)
  })

  // 智能体决策
  connection.on('AgentDecision', (data: any) => {
    const decision = data.decision || t('agentWorkflow.decidingFallback')
    const reasoning = data.reasoning || ''
    addLog('info', 'Decision', `${t('agentWorkflow.decisionLabel')} ${decision}${reasoning ? ` - ${reasoning}` : ''}`, data)
  })

  // ReAct 相关事件
  connection.on('ReActThought', (data: any) => {
    const thought = data.thought || data.content || t('agentWorkflow.emptyThought')
    addLog('info', 'Thought', `${t('agentWorkflow.thoughtLabel')} ${thought}`, data)
  })

  connection.on('ReActAction', (data: any) => {
    const toolName = data.toolName || data.action || t('agentWorkflow.unknownTool')
    const params = data.parameters || data.input || null
    addLog('info', 'Action', `${t('agentWorkflow.actionLabel')} ${toolName}`, params ? { toolName, parameters: params } : { toolName })
  })

  connection.on('ReActObservation', (data: any) => {
    const observation = data.observation || data.result || data.output || t('agentWorkflow.noObservation')
    const decoded = decodeUnicode(observation)
    const preview = decoded.length > 100 ? `${decoded.substring(0, 100)}...` : decoded
    addLog('info', 'Observation', `${t('agentWorkflow.observationLabel')} ${preview}`, { observation: decoded })
  })

  // 工具调用
  connection.on('ToolCalling', (data: any) => {
    const toolName = data.toolName || t('agentWorkflow.unknownTool')
    addLog('info', 'ToolCall', `${t('agentWorkflow.toolCallLabel')} ${toolName}`, data.parameters || null)
  })

  connection.on('ToolResult', (data: any) => {
    const toolName = data.toolName || t('agentWorkflow.unknownTool')
    const result = data.result || data.output || t('agentWorkflow.noResult')
    const decodedResult = typeof result === 'string' ? decodeUnicode(result) : result
    const resultPreview = typeof decodedResult === 'string'
      ? (decodedResult.length > 100 ? `${decodedResult.substring(0, 100)}...` : decodedResult)
      : t('agentWorkflow.objectResult')
    const durationInfo = data.durationMs ? ` (${data.durationMs.toFixed(0)}ms)` : ''
    addLog('success', 'ToolResult', t('agentWorkflow.toolResultLog', { name: toolName, duration: durationInfo, result: resultPreview }), { result: decodedResult })
  })
}

function addLog(type: string, stage: string, message: string, data?: any) {
  executionLogs.value.unshift({
    type,
    stage,
    message,
    data,
    timestamp: new Date().toLocaleTimeString('zh-CN'),
  })
  // 限制日志数量
  if (executionLogs.value.length > 100) {
    executionLogs.value = executionLogs.value.slice(0, 100)
  }
}

async function refreshTasks() {
  try {
    const res = await apiWorkflow.getTasks(workflowId.value)
    tasks.value = res || []
  } catch (error) {
    console.error('刷新任务列表失败:', error)
  }
}

// 状态辅助函数
function getStatusLabel(status: number | null | undefined) {
  const statusMap: Record<number, string> = {
    0: t('agentWorkflow.statusPending'),
    1: t('agentWorkflow.statusPlanning'),
    2: t('agentWorkflow.statusExecuting'),
    3: t('agentWorkflow.statusCompleted'),
    4: t('agentWorkflow.statusFailed'),
    5: t('agentWorkflow.statusCancelled'),
  }
  return statusMap[status ?? -1] || t('agentWorkflow.statusUnknown')
}

function getStatusColor(status: number | null | undefined) {
  const colors: Record<number, string> = {
    0: 'info',
    1: 'warning',
    2: 'primary',
    3: 'success',
    4: 'danger',
    5: 'info',
  }
  return colors[status ?? -1] || 'info'
}

function getTaskStatusLabel(status: number | null | undefined) {
  const statusMap: Record<number, string> = {
    0: t('agentWorkflow.taskStatusPending'),
    1: t('agentWorkflow.taskStatusReady'),
    2: t('agentWorkflow.taskStatusExecuting'),
    3: t('agentWorkflow.taskStatusDone'),
    4: t('agentWorkflow.taskStatusFailed'),
    5: t('agentWorkflow.taskStatusBlocked'),
    6: t('agentWorkflow.taskStatusConfirm'),
  }
  return statusMap[status ?? -1] || t('agentWorkflow.taskStatusUnknown')
}

function getTaskStatusColor(status: number | null | undefined) {
  const colors: Record<number, string> = {
    0: 'info',
    1: 'warning',
    2: 'primary',
    3: 'success',
    4: 'danger',
    5: 'warning',
    6: 'warning',
  }
  return colors[status ?? -1] || 'info'
}

function getLogIcon(type: string) {
  const icons: Record<string, string> = {
    success: 'i-ep:circle-check',
    error: 'i-ep:circle-close',
    info: 'i-ep:info-filled',
    progress: 'i-ep:loading',
  }
  return icons[type] || 'i-ep:info-filled'
}

function getLogColor(type: string) {
  const colors: Record<string, string> = {
    success: '#67c23a',
    error: '#f56c6c',
    info: '#409eff',
    progress: '#e6a23c',
  }
  return colors[type] || '#909399'
}

// 🆕 解码 Unicode 转义序列（如 \u661F\u671F -> 星期）
function decodeUnicode(str: string): string {
  if (!str) return str
  return str.replace(/\\u([0-9a-fA-F]{4})/g, (_, hex) =>
    String.fromCharCode(parseInt(hex, 16))
  )
}

function formatDateTime(dateStr: string | null | undefined) {
  if (!dateStr) return '-'
  return new Date(dateStr).toLocaleString('zh-CN')
}

function formatDuration(seconds: number | null | undefined) {
  if (!seconds) return '-'
  if (seconds < 60) return t('agentWorkflow.durationSeconds', { n: seconds })
  if (seconds < 3600) return t('agentWorkflow.durationMinutes', { m: Math.floor(seconds / 60), s: seconds % 60 })
  return t('agentWorkflow.durationHours', { h: Math.floor(seconds / 3600), m: Math.floor((seconds % 3600) / 60) })
}

function goBack() {
  router.push('/workflow')
}
</script>

<template>
  <div class="workflow-detail-page">
    <FaPageMain>
      <!-- 页面头部 -->
      <div class="page-header">
        <div class="header-left">
          <ElButton @click="goBack">
            <template #icon>
              <FaIcon name="i-ep:arrow-left" />
            </template>
            {{ $t('agentWorkflow.backToList') }}
          </ElButton>
          <h2>{{ $t('agentWorkflow.detailTitle') }}</h2>
          <ElTag :type="connectionStatus === 'Connected' ? 'success' : 'danger'" size="small">
            {{ connectionStatus }}
          </ElTag>
        </div>
        <div class="header-right">
          <ElButton type="primary" @click="loadWorkflowDetail">
            <template #icon>
              <FaIcon name="i-ep:refresh" />
            </template>
            {{ $t('agentWorkflow.refresh') }}
          </ElButton>
        </div>
      </div>

      <div v-loading="loading" class="content-layout">
        <!-- 左侧：基本信息和任务列表 -->
        <div class="left-panel">
          <!-- 基本信息 -->
          <ElCard shadow="hover" class="info-card">
            <template #header>
              <span>{{ $t('agentWorkflow.basicInfo') }}</span>
            </template>

            <ElDescriptions :column="2" border>
              <ElDescriptionsItem label="ID">
                {{ workflow?.id || '-' }}
              </ElDescriptionsItem>
              <ElDescriptionsItem :label="$t('agentWorkflow.statusLabel')">
                <ElTag :type="getStatusColor(workflow?.status)" size="small">
                  {{ getStatusLabel(workflow?.status) }}
                </ElTag>
              </ElDescriptionsItem>
              <ElDescriptionsItem :label="$t('agentWorkflow.nameLabel')" :span="2">
                {{ workflow?.name || $t('agentWorkflow.unnamed') }}
              </ElDescriptionsItem>
              <ElDescriptionsItem :label="$t('agentWorkflow.descriptionLabel')" :span="2">
                {{ workflow?.description || '-' }}
              </ElDescriptionsItem>
              <ElDescriptionsItem :label="$t('agentWorkflow.triggerTypeLabel')">
                {{ workflow?.triggerType || '-' }}
              </ElDescriptionsItem>
              <ElDescriptionsItem :label="$t('agentWorkflow.thinkingModeLabel')">
                {{ workflow?.thinkingMode || '-' }}
              </ElDescriptionsItem>
              <ElDescriptionsItem :label="$t('common.createTime')">
                {{ formatDateTime(workflow?.createTime) }}
              </ElDescriptionsItem>
              <ElDescriptionsItem :label="$t('agentWorkflow.durationLabel')">
                {{ formatDuration(workflow?.actualDuration) }}
              </ElDescriptionsItem>
            </ElDescriptions>

            <!-- 执行结果 -->
            <div v-if="workflow?.resultSummary" class="result-summary">
              <h4>{{ $t('agentWorkflow.resultTitle') }}</h4>
              <ElCard shadow="never" class="result-content">
                <pre>{{ workflow.resultSummary }}</pre>
              </ElCard>
            </div>
          </ElCard>

          <!-- 任务列表 -->
          <ElCard shadow="hover" class="task-card">
            <template #header>
              <div class="card-header">
                <span>{{ $t('agentWorkflow.taskBreakdown', { n: tasks.length }) }}</span>
                <ElButton size="small" @click="refreshTasks">
                  {{ $t('agentWorkflow.refresh') }}
                </ElButton>
              </div>
            </template>

            <ElTable :data="tasks" stripe border max-height="400">
              <ElTableColumn prop="taskName" :label="$t('agentWorkflow.taskNameCol')" min-width="150" show-overflow-tooltip />
              <ElTableColumn prop="status" :label="$t('agentWorkflow.statusLabel')" width="100" align="center">
                <template #default="{ row }">
                  <ElTag :type="getTaskStatusColor(row.status)" size="small">
                    {{ getTaskStatusLabel(row.status) }}
                  </ElTag>
                </template>
              </ElTableColumn>
              <ElTableColumn prop="taskType" :label="$t('agentWorkflow.typeCol')" width="100" align="center" />
              <ElTableColumn prop="reActLoopCount" :label="$t('agentWorkflow.reActLoopCol')" width="100" align="center">
                <template #default="{ row }">
                  {{ row.reActLoopCount || 0 }} / {{ row.maxLoopCount || 10 }}
                </template>
              </ElTableColumn>
              <ElTableColumn :label="$t('agentWorkflow.execDetailCol')" min-width="200">
                <template #default="{ row }">
                  <ElCollapse v-if="row.thoughtProcess || row.actionTaken || row.outputResult" accordion>
                    <ElCollapseItem :title="$t('agentWorkflow.viewDetail')">
                      <div v-if="row.thoughtProcess" class="task-detail-item">
                        <strong>{{ $t('agentWorkflow.thoughtProcessLabel') }}</strong>
                        <pre>{{ row.thoughtProcess }}</pre>
                      </div>
                      <div v-if="row.actionTaken" class="task-detail-item">
                        <strong>{{ $t('agentWorkflow.actionLabel') }}</strong>
                        <pre>{{ row.actionTaken }}</pre>
                      </div>
                      <div v-if="row.outputResult" class="task-detail-item">
                        <strong>{{ $t('agentWorkflow.outputResultLabel') }}</strong>
                        <pre>{{ row.outputResult }}</pre>
                      </div>
                      <div v-if="row.errorDetails" class="task-detail-item error">
                        <strong>{{ $t('agentWorkflow.errorInfoLabel') }}</strong>
                        <pre>{{ row.errorDetails }}</pre>
                      </div>
                    </ElCollapseItem>
                  </ElCollapse>
                  <span v-else class="text-muted">-</span>
                </template>
              </ElTableColumn>
            </ElTable>
          </ElCard>
        </div>

        <!-- 右侧：实时进度 -->
        <div class="right-panel">
          <!-- 进度条 -->
          <ElCard shadow="hover" class="progress-card">
            <template #header>
              <span>{{ $t('agentWorkflow.executionProgress') }}</span>
            </template>

            <ElProgress
              :percentage="progressPercentage"
              :status="progressStatus"
              :stroke-width="20"
            />

            <div class="current-stage">
              <ElTag :type="getStatusColor(workflow?.status)" size="large">
                {{ currentStage || getStageFromStatus(workflow?.status) }}
              </ElTag>
              <span class="stage-message">{{ currentMessage }}</span>
            </div>
          </ElCard>

          <!-- 实时日志 -->
          <ElCard shadow="hover" class="log-card">
            <template #header>
              <div class="card-header">
                <span>{{ $t('agentWorkflow.executionLogCount', { n: executionLogs.length }) }}</span>
                <ElButton size="small" @click="executionLogs = []">
                  {{ $t('agentWorkflow.clearLogs') }}
                </ElButton>
              </div>
            </template>

            <div class="log-container">
              <ElTimeline v-if="executionLogs.length > 0">
                <ElTimelineItem
                  v-for="(log, index) in executionLogs"
                  :key="index"
                  :timestamp="log.timestamp"
                  :color="getLogColor(log.type)"
                >
                  <div class="log-item">
                    <ElTag :type="log.type === 'error' ? 'danger' : log.type === 'success' ? 'success' : 'info'" size="small">
                      {{ log.stage }}
                    </ElTag>
                    <span class="log-message">{{ log.message }}</span>
                  </div>
                </ElTimelineItem>
              </ElTimeline>
              <ElEmpty v-else :description="$t('agentWorkflow.noLogs')" />
            </div>
          </ElCard>
        </div>
      </div>
    </FaPageMain>
  </div>
</template>

<style scoped>
.workflow-detail-page {
  height: 100%;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 20px;
  padding-bottom: 16px;
  border-bottom: 1px solid var(--el-border-color);
}

.header-left {
  display: flex;
  align-items: center;
  gap: 16px;
}

.header-left h2 {
  margin: 0;
  font-size: 18px;
}

.content-layout {
  display: grid;
  grid-template-columns: 1fr 400px;
  gap: 20px;
  min-height: 600px;
}

.left-panel {
  display: flex;
  flex-direction: column;
  gap: 20px;
}

.right-panel {
  display: flex;
  flex-direction: column;
  gap: 20px;
}

.card-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.result-summary {
  margin-top: 20px;
}

.result-summary h4 {
  margin-bottom: 10px;
  color: var(--el-text-color-primary);
}

.result-content {
  background-color: var(--el-fill-color-light);
}

.result-content pre {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-word;
  font-size: 13px;
  line-height: 1.6;
}

.task-detail-item {
  margin-bottom: 12px;
}

.task-detail-item strong {
  display: block;
  margin-bottom: 4px;
  color: var(--el-text-color-primary);
}

.task-detail-item pre {
  margin: 0;
  padding: 8px;
  background-color: var(--el-fill-color-light);
  border-radius: 4px;
  white-space: pre-wrap;
  word-break: break-word;
  font-size: 12px;
  max-height: 150px;
  overflow-y: auto;
}

.task-detail-item.error pre {
  background-color: var(--el-color-danger-light-9);
  color: var(--el-color-danger);
}

.current-stage {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-top: 16px;
}

.stage-message {
  color: var(--el-text-color-secondary);
}

.log-container {
  max-height: 500px;
  overflow-y: auto;
}

.log-item {
  display: flex;
  align-items: flex-start;
  gap: 8px;
}

.log-message {
  flex: 1;
  word-break: break-word;
}

.text-muted {
  color: var(--el-text-color-placeholder);
}

@media (max-width: 1200px) {
  .content-layout {
    grid-template-columns: 1fr;
  }
}
</style>
