<template>
  <div class="workflow-test-page">
    <!-- <div class="page-header">
      <h1>🤖 智能体工作流平台</h1>
      <p class="subtitle">输入任务需求，AI自动规划并执行</p>
    </div> -->

    <div class="content-layout">
      <!-- 左侧：输入区 -->
      <div class="config-panel">
        <el-card shadow="hover">
          <template #header>
            <div class="card-header">
              <span>{{ $t('workflowTest.taskInput') }}</span>
              <el-tag :type="connectionStatus === 'Connected' ? 'success' : 'danger'">
                {{ connectionStatus }}
              </el-tag>
            </div>
          </template>

          <el-form label-width="0">
            <!-- 任务输入 -->
            <el-form-item>
              <el-input
                v-model="userInput"
                type="textarea"
                :rows="8"
                :placeholder="$t('workflowTest.inputPlaceholder')"
              />
            </el-form-item>

            <!-- 执行按钮 -->
            <el-form-item>
              <el-button
                type="primary"
                :loading="isExecuting"
                :disabled="!userInput || connectionStatus !== 'Connected'"
                @click="executeWorkflow"
                style="width: 100%"
                size="large"
              >
                <span v-if="!isExecuting">{{ $t('workflowTest.startExecution') }}</span>
                <span v-else>{{ $t('workflowTest.executing') }}</span>
              </el-button>
            </el-form-item>
          </el-form>
        </el-card>

        <!-- 快速测试 -->
        <!-- <el-card shadow="hover" style="margin-top: 16px">
          <template #header>⚡ 快速测试案例</template>
          <el-button size="small" @click="loadTestCase('plc')" style="width: 100%; margin-bottom: 8px">
            PLC故障诊断
          </el-button>
          <el-button size="small" @click="loadTestCase('parser')" style="width: 100%; margin-bottom: 8px">
            生成PLC解析器
          </el-button>
          <el-button size="small" @click="loadTestCase('optimization')" style="width: 100%">
            生产流程优化
          </el-button>
        </el-card> -->

        <!-- 🆕 PLC文件上传（自动分析） -->
        <el-card shadow="hover" style="margin-top: 16px">
          <template #header>
            <div class="card-header">
              <span>{{ $t('workflowTest.plcFileUpload') }}</span>
              <el-tag v-if="uploadedFile" type="success" size="small">{{ $t('workflowTest.uploaded') }}</el-tag>
            </div>
          </template>

          <!-- 上传提示 -->
          <div style="margin-bottom: 12px">
            <el-alert type="info" :closable="false" show-icon>
              <template #title>
                {{ $t('workflowTest.uploadTip') }}
              </template>
            </el-alert>
          </div>

          <el-upload
            class="upload-demo"
            drag
            :action="`${apiUrl}/api/WorkflowTest/upload-plc-file`"
            :on-success="handleQuickUploadSuccess"
            :on-error="handleUploadError"
            :before-upload="beforeUpload"
            :show-file-list="false"
            accept=".awl,.xml,.gx3,.gxw,.asc,.txt,.cxp,.cxt,.xef,.stu,.acd"
          >
            <el-icon class="el-icon--upload"><upload-filled /></el-icon>
            <div class="el-upload__text">
              {{ $t('workflowTest.uploadDrag') }} <em>{{ $t('workflowTest.uploadClick') }}</em>
            </div>
            <template #tip>
              <div class="el-upload__tip">
                {{ $t('workflowTest.uploadFormats') }}<br />
                {{ $t('workflowTest.uploadSizeLimit') }}
              </div>
            </template>
          </el-upload>

          <!-- 最近上传文件信息 -->
          <div v-if="uploadedFile" class="file-info" style="margin-top: 16px">
            <el-divider>{{ $t('workflowTest.recentUpload') }}</el-divider>
            <p><strong>{{ $t('workflowTest.fileNameLabel') }}</strong> {{ uploadedFile.fileName }}</p>
            <p><strong>{{ $t('workflowTest.fileSizeLabel') }}</strong> {{ formatFileSize(uploadedFile.size) }}</p>
            <p style="font-size: 12px; color: #909399;">
              {{ $t('workflowTest.autoDetectTip') }}
            </p>
          </div>
        </el-card>
      </div>

      <!-- 右侧：执行结果 -->
      <div class="result-panel">
        <!-- 工作流进度 -->
        <el-card shadow="hover">
          <template #header>
            <div class="card-header">
              <span>{{ $t('workflowTest.workflowProgress') }}</span>
              <el-tag v-if="currentWorkflow" type="info">WorkflowId: {{ currentWorkflow.workflowId }}</el-tag>
            </div>
          </template>

          <div v-if="!currentWorkflow" class="empty-state">
            <el-empty :description="$t('workflowTest.noRecords')" />
          </div>

          <div v-else class="workflow-progress">
            <!-- 进度条 -->
            <el-progress
              :percentage="progressPercentage"
              :status="progressStatus"
              :stroke-width="20"
            />

            <!-- 当前阶段 -->
            <div class="current-stage">
              <el-tag :type="getStageType(currentStage)" size="large">
                {{ currentStage }}
              </el-tag>
              <span class="stage-message">{{ currentMessage }}</span>
            </div>

            <!-- 执行日志 -->
            <div class="execution-log">
              <h4>{{ $t('workflowTest.executionLog') }}</h4>
              <el-timeline>
                <el-timeline-item
                  v-for="(log, index) in executionLogs"
                  :key="index"
                  :timestamp="log.timestamp"
                  :type="log.type"
                  :icon="log.icon"
                >
                  <div class="log-content">
                    <strong>{{ log.stage }}</strong>
                    <p>{{ log.message }}</p>
                    <pre v-if="log.data && Object.keys(log.data).length > 0" class="log-data">{{ formatCallData(log.data) }}</pre>
                  </div>
                </el-timeline-item>
              </el-timeline>
            </div>
          </div>
        </el-card>

        <!-- 任务列表 -->
        <el-card shadow="hover" style="margin-top: 16px">
          <template #header>{{ $t('workflowTest.taskStatus') }}</template>

          <div v-if="tasks.length === 0" class="empty-state">
            <el-empty :description="$t('workflowTest.noTasks')" />
          </div>

          <el-table v-else :data="tasks" border stripe>
            <el-table-column prop="taskName" :label="$t('workflowTest.taskName')" width="200" />
            <el-table-column :label="$t('workflowTest.status')" width="120">
              <template #default="{ row }">
                <el-tag :type="getTaskStatusType(row.status)">
                  {{ row.statusText }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column :label="$t('workflowTest.executionResult')">
              <template #default="{ row }">
                <el-collapse v-if="row.output" accordion>
                  <el-collapse-item :title="$t('workflowTest.viewResult', { count: row.output.length })">
                    <div class="task-output">{{ row.output }}</div>
                  </el-collapse-item>
                </el-collapse>
                <span v-else class="text-muted">-</span>
              </template>
            </el-table-column>
            <el-table-column prop="timestamp" :label="$t('workflowTest.updateTime')" width="180" />
          </el-table>
        </el-card>

        <!-- 🆕 过程可视化 - 工具调用链 -->
        <el-card v-if="toolCalls.length > 0" shadow="hover" style="margin-top: 16px">
          <template #header>
            <div class="card-header">
              <span>{{ $t('workflowTest.toolCallChain') }}</span>
              <el-tag type="info">{{ $t('workflowTest.callCount', { count: toolCalls.length }) }}</el-tag>
            </div>
          </template>

          <el-timeline>
            <el-timeline-item
              v-for="(call, index) in toolCalls"
              :key="index"
              :timestamp="call.timestamp"
              :type="call.status === 'success' ? 'success' : call.status === 'failed' ? 'danger' : 'primary'"
              :icon="call.status === 'success' ? 'CircleCheck' : call.status === 'failed' ? 'CircleClose' : 'Loading'"
            >
              <div class="tool-call-item">
                <div style="display: flex; justify-content: space-between; align-items: center">
                  <strong>{{ call.toolName }}</strong>
                  <el-tag v-if="call.durationMs" size="small" type="info">{{ call.durationMs.toFixed(0) }}ms</el-tag>
                </div>
                <el-collapse v-if="call.parameters || call.result" accordion style="margin-top: 8px">
                  <el-collapse-item v-if="call.parameters" :title="$t('workflowTest.callParameters')">
                    <pre class="code-block">{{ formatCallData(call.parameters) }}</pre>
                  </el-collapse-item>
                  <el-collapse-item v-if="call.result" :title="$t('workflowTest.returnResult')">
                    <pre class="code-block">{{ formatCallData(call.result) }}</pre>
                  </el-collapse-item>
                </el-collapse>
              </div>
            </el-timeline-item>
          </el-timeline>
        </el-card>

        <!-- 🆕 过程可视化 - 智能体思考 -->
        <el-card v-if="agentThoughts.length > 0" shadow="hover" style="margin-top: 16px">
          <template #header>
            <div class="card-header">
              <span>{{ $t('workflowTest.agentThinkingProcess') }}</span>
              <el-tag type="info">{{ $t('workflowTest.thoughtCount', { count: agentThoughts.length }) }}</el-tag>
            </div>
          </template>

          <div class="thoughts-container">
            <div
              v-for="(thought, index) in agentThoughts"
              :key="index"
              class="thought-item"
            >
              <div class="thought-header">
                <el-avatar :size="30" style="background-color: #67c23a">
                  <el-icon><ChatDotRound /></el-icon>
                </el-avatar>
                <span class="thought-agent">{{ thought.agentName || $t('workflowTest.agent') }}</span>
                <span class="thought-time">{{ thought.timestamp }}</span>
              </div>
              <div class="thought-content">
                {{ thought.thought || $t('workflowTest.emptyThought') }}
              </div>
            </div>
          </div>
        </el-card>

        <!-- 🆕 过程可视化 - 重试历史 -->
        <el-card v-if="retryHistory.length > 0" shadow="hover" style="margin-top: 16px">
          <template #header>
            <div class="card-header">
              <span>{{ $t('workflowTest.retryHistory') }}</span>
              <el-tag type="warning">{{ $t('workflowTest.retryCount', { count: retryHistory.length }) }}</el-tag>
            </div>
          </template>

          <el-collapse accordion>
            <el-collapse-item
              v-for="(retry, index) in retryHistory"
              :key="index"
              :title="$t('workflowTest.retryTitle', { count: retry.retryCount, reason: retry.reason })"
            >
              <p><strong>{{ $t('workflowTest.taskIdLabel') }}</strong> {{ retry.taskId }}</p>
              <p><strong>{{ $t('workflowTest.retryReasonLabel') }}</strong> {{ retry.reason }}</p>
              <p><strong>{{ $t('workflowTest.nextActionLabel') }}</strong> {{ retry.nextAction }}</p>
              <p><strong>{{ $t('workflowTest.timeLabel') }}</strong> {{ retry.timestamp }}</p>
            </el-collapse-item>
          </el-collapse>
        </el-card>

        <!-- 对话式结果展示 -->
        <el-card shadow="hover" style="margin-top: 16px">
          <template #header>
            <div class="card-header">
              <span>{{ $t('workflowTest.chatRecords') }}</span>
              <el-tag :type="isStreaming ? 'warning' : 'info'">
                {{ isStreaming ? $t('workflowTest.streaming') : $t('workflowTest.done') }}
              </el-tag>
            </div>
          </template>

          <div class="chat-container">
            <!-- 用户消息 -->
            <div v-if="userInput" class="message-item user-message">
              <div class="message-avatar">
                <el-avatar :size="40" style="background-color: #409eff">
                  <el-icon><User /></el-icon>
                </el-avatar>
              </div>
              <div class="message-content">
                <div class="message-header">
                  <span class="message-sender">{{ $t('workflowTest.user') }}</span>
                  <span class="message-time">{{ startTime }}</span>
                </div>
                <div class="message-body user-body">
                  {{ userInput }}
                </div>
              </div>
            </div>

            <!-- AI 助手消息 -->
            <div v-if="displayedResult || isStreaming" class="message-item assistant-message">
              <div class="message-avatar">
                <el-avatar :size="40" style="background-color: #67c23a">
                  <el-icon><ChatDotRound /></el-icon>
                </el-avatar>
              </div>
              <div class="message-content">
                <div class="message-header">
                  <span class="message-sender">{{ $t('workflowTest.aiAgent') }}</span>
                  <span class="message-time">{{ endTime }}</span>
                  <el-tag v-if="isStreaming" type="success" size="small" effect="plain">{{ $t('workflowTest.generating') }}</el-tag>
                </div>
                <div class="message-body assistant-body">
                  <!-- 自定义渲染，避免重复叠加问题 -->
                  <div
                    class="markdown-body"
                    v-html="renderedContent"
                  ></div>
                </div>
              </div>
            </div>

            <!-- 空状态 -->
            <div v-if="!userInput && !displayedResult && !isStreaming" class="empty-chat">
              <el-empty :description="$t('workflowTest.noChatContent')" />
            </div>
          </div>
        </el-card>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { ElMessage } from 'element-plus'
import * as signalR from '@microsoft/signalr'
import { marked } from 'marked'
import { useI18n } from 'vue-i18n'
import messages from './WorkflowTest.lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

// ========== 状态管理 ==========
const connectionStatus = ref('Disconnected')
const userInput = ref('')
const isExecuting = ref(false)

// 工作流状态
const currentWorkflow = ref<any>(null)
const currentStage = ref('')
const currentMessage = ref('')
const executionLogs = ref<any[]>([])
const tasks = ref<any[]>([])
const finalResult = ref('')
const currentWorkflowId = ref<string>('')

// 流式输出状态
const displayedResult = ref('')
const renderedContent = computed(() => {
  return marked.parse(displayedResult.value)
})
const isStreaming = ref(false)
const currentStreamingTask = ref<string>('')
const expertResults = ref<Map<number, string>>(new Map())
const startTime = ref('')
const endTime = ref('')

// 🆕 过程可视化状态
const agentThoughts = ref<any[]>([]) // 智能体思考过程
const toolCalls = ref<any[]>([]) // 工具调用链
const agentDecisions = ref<any[]>([]) // 智能体决策
const retryHistory = ref<any[]>([]) // 重试历史
const dependencyWaits = ref<any[]>([]) // 依赖等待

// 🆕 文件上传状态
const uploadedFile = ref<any>(null)
const uploadTask = ref<string>('detect') // 上传后的任务类型: detect, parse, diagnose
const apiUrl = import.meta.env.DEV
  ? import.meta.env.VITE_APP_API_BASEURL
  : (window as any).apiConfig?.apiUrl || ''

// 🆕 主输入区的附件管理
const attachedFiles = ref<any[]>([])

// SignalR连接
let connection: signalR.HubConnection | null = null


// ========== 计算属性 ==========
const progressPercentage = computed(() => {
  const stageProgress: Record<string, number> = {
    Started: 10,
    Planning: 15,
    PlanGenerated: 25,
    RouterSelected: 20,
    ExpertsLoaded: 25,
    RouterAnalyzing: 30,
    ExpertsActivated: 40,
    TasksCreated: 40,
    Step1_Planning: 33,
    Step2_Executing: 66,
    Step3_Reviewing: 90,
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

// ========== SignalR连接 ==========
async function connectSignalR() {
  try {
    const apiUrl = import.meta.env.DEV
      ? import.meta.env.VITE_APP_API_BASEURL
      : (window as any).apiConfig?.apiUrl || ''
debugger
    connection = new signalR.HubConnectionBuilder()
      .withUrl(`${apiUrl}/hubs/agent-streaming`, {
        withCredentials: false, // 禁用凭证以避免CORS问题
        skipNegotiation: false,
      })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000]) // 自定义重连间隔，最后无限重试
      .configureLogging(signalR.LogLevel.Information)
      .build()

    // 监听重连事件
    connection.onreconnecting((error) => {
      console.warn('[SignalR] 连接断开，正在重连...', error)
      ElMessage.warning(t('workflowTest.reconnecting'))
    })

    connection.onreconnected((connectionId) => {
      console.log('[SignalR] 重连成功，ConnectionId:', connectionId)
      ElMessage.success(t('workflowTest.reconnected'))
      // 如果有正在执行的工作流，重新加入分组
      if (currentWorkflowId.value) {
        connection.invoke('JoinWorkflowGroup', currentWorkflowId.value)
          .then(() => console.log('[SignalR] 重连后重新加入分组:', currentWorkflowId.value))
      }
    })

    connection.onclose((error) => {
      console.error('[SignalR] 连接关闭', error)
      ElMessage.error(t('workflowTest.connectionClosed'))
    })


    // 监听工作流进度
    connection.on('WorkflowProgress', (data) => {
      console.log('[WorkflowProgress] 收到消息:', data)
      console.log('[WorkflowProgress] workflowId类型:', typeof data.workflowId, '值:', data.workflowId)

      currentStage.value = data.stage
      currentMessage.value = data.message

      executionLogs.value.push({
        stage: data.stage,
        message: data.message,
        data: data.data,
        timestamp: new Date(data.timestamp).toLocaleTimeString(),
        type: getLogType(data.stage),
        icon: getLogIcon(data.stage),
      })

      if (data.stage === 'Completed' || data.stage === 'Failed') {
        isExecuting.value = false
        isStreaming.value = false
        endTime.value = new Date().toLocaleTimeString()
        finalResult.value = displayedResult.value
      }
    })

    // 监听工作流启动
    connection.on('WorkflowStarted', (data) => {
      console.log('[WorkflowStarted]', data)
      currentStage.value = 'Started'
      currentMessage.value = t('workflowTest.workflowStarted')
      executionLogs.value.push({
        stage: 'Started',
        message: t('workflowTest.workflowStartedLog', { id: data.workflowId }),
        timestamp: new Date().toLocaleTimeString(),
        type: 'primary',
        icon: 'VideoPlay'
      })
    })

    // 监听规划开始
    connection.on('PlanningStarted', (data) => {
      console.log('[PlanningStarted]', data)
      currentStage.value = 'Planning'
      currentMessage.value = t('workflowTest.plannerPlanning', { id: data.planner })
      executionLogs.value.push({
        stage: 'Planning',
        message: t('workflowTest.planningTask'),
        timestamp: new Date().toLocaleTimeString(),
        type: 'info',
        icon: 'Edit'
      })
    })

    // 监听规划完成
    connection.on('PlanGenerated', (data) => {
      console.log('[PlanGenerated]', data)
      currentStage.value = 'PlanGenerated'
      currentMessage.value = t('workflowTest.planCompleted', { count: data.stepCount })
      executionLogs.value.push({
        stage: 'PlanGenerated',
        message: t('workflowTest.planGeneratedLog', { count: data.stepCount }),
        data: data.steps,
        timestamp: new Date().toLocaleTimeString(),
        type: 'success',
        icon: 'List'
      })

      // 预填充任务列表
      if (tasks.value.length === 0 && data.steps && Array.isArray(data.steps)) {
         tasks.value = data.steps.map((s: any, i: number) => ({
            taskId: -1 * (i + 1), // 临时ID
            taskName: s.stepName,
            status: 0,
            statusText: t('workflowTest.pendingStatus'),
            timestamp: new Date().toLocaleTimeString()
         }))
      }
    })

    // 监听工作流完成
    connection.on('WorkflowCompleted', (data) => {
      console.log('[WorkflowCompleted]', data)
      currentStage.value = 'Completed'
      currentMessage.value = t('workflowTest.workflowSuccess')

      executionLogs.value.push({
        stage: 'Completed',
        message: t('workflowTest.totalDuration', { duration: parseFloat(data.duration).toFixed(2) }),
        timestamp: new Date().toLocaleTimeString(),
        type: 'success',
        icon: 'CircleCheck'
      })

      isExecuting.value = false
      isStreaming.value = false
      endTime.value = new Date().toLocaleTimeString()

      if (data.summary) {
         if (!displayedResult.value) {
            displayedResult.value = data.summary
         } else {
             displayedResult.value += `\n\n${t('workflowTest.executionSummaryMarkdown')}\n${data.summary}`
         }
      }
    })

    // 监听工作流失败
    connection.on('WorkflowFailed', (data) => {
       console.log('[WorkflowFailed]', data)
       currentStage.value = 'Failed'
       currentMessage.value = t('workflowTest.failedMessage', { error: data.error })
       executionLogs.value.push({
        stage: 'Failed',
        message: data.error,
        timestamp: new Date().toLocaleTimeString(),
        type: 'danger',
        icon: 'CircleClose'
      })
      isExecuting.value = false
      isStreaming.value = false
      ElMessage.error(t('workflowTest.workflowFailed', { error: data.error }))
    })

    // 监听专家流式输出开始
    connection.on('ExpertStreamStart', (data) => {
      console.log('[ExpertStreamStart] 专家开始生成:', data)
      isStreaming.value = true
      currentStreamingTask.value = data.taskName

      // 初始化该专家的结果
      expertResults.value.set(data.taskId, '')

      // 添加分隔标题
      if (displayedResult.value) {
        displayedResult.value += '\n\n'
      }
      displayedResult.value += `## ${data.taskName}\n\n`
    })

    // 监听专家流式 token（真正的流式输出）
    connection.on('ExpertStreamToken', (data) => {
      // 实时追加到当前专家的结果
      const currentContent = expertResults.value.get(data.taskId) || ''
      expertResults.value.set(data.taskId, currentContent + data.token)

      // 实时追加到总显示结果
      displayedResult.value += data.token
    })

    // 监听专家流式输出完成
    connection.on('ExpertStreamComplete', (data) => {
      console.log('[ExpertStreamComplete] 专家生成完成:', data)
      currentStreamingTask.value = ''
    })

    // 监听任务状态变更
    connection.on('TaskStatusChanged', (data) => {
      console.log('[TaskStatusChanged]', data)
      // 尝试匹配已有任务（包括临时ID）
      let existingTask = tasks.value.find((t) => t.taskId === data.taskId)

      if (!existingTask) {
        // 尝试通过名称匹配临时任务
        existingTask = tasks.value.find(t => t.taskId < 0 && t.taskName === data.taskName)
        if (existingTask) {
            existingTask.taskId = data.taskId // 更新为真实ID
        }
      }

      if (existingTask) {
        Object.assign(existingTask, {
          status: data.status,
          statusText: data.statusText,
          output: data.output,
          timestamp: new Date(data.timestamp).toLocaleTimeString(),
        })
      } else {
        tasks.value.push({
          taskId: data.taskId,
          taskName: data.taskName,
          status: data.status,
          statusText: data.statusText,
          output: data.output,
          timestamp: new Date(data.timestamp).toLocaleTimeString(),
        })
      }
    })

    // 🆕 监听智能体思考过程
    connection.on('AgentThinking', (data) => {
      console.log('[AgentThinking]', data)
      const agentName = data.agentName || t('workflowTest.agent')
      const thought = data.thought || t('workflowTest.thinking')
      agentThoughts.value.push({
        ...data,
        agentName,
        thought,
        timestamp: new Date().toLocaleTimeString()
      })
      executionLogs.value.push({
        stage: t('workflowTest.agentThinkingStage'),
        message: `${agentName}: ${thought}`,
        timestamp: new Date().toLocaleTimeString(),
        type: 'info',
        icon: 'ChatDotRound'
      })
    })

    // 🆕 监听 ReAct 循环开始
    connection.on('ReActLoopStarted', (data) => {
        console.log('[ReActLoopStarted]', data)
        executionLogs.value.push({
            stage: t('workflowTest.loopStart'),
            message: t('workflowTest.loopStartMessage', { count: data.loopCount }),
            timestamp: new Date().toLocaleTimeString(),
            type: 'primary',
            icon: 'Refresh'
        })
    })

    // 🆕 监听 ReAct 思考
    connection.on('ReActThought', (data) => {
        console.log('[ReActThought]', data)
        const agentName = tasks.value.find(item => item.taskId === data.taskId)?.taskName || t('workflowTest.agent')
        const thought = data.thought || data.content || t('workflowTest.emptyThought')
        agentThoughts.value.push({
            taskId: data.taskId,
            agentName: agentName,
            thought: `${t('workflowTest.thoughtRound', { count: data.loopCount || 1 })} ${thought}`,
            timestamp: new Date().toLocaleTimeString()
        })
        const thoughtPreview = thought.length > 50 ? `${thought.substring(0, 50)}...` : thought
        executionLogs.value.push({
            stage: t('workflowTest.agentThinkingStage'),
            message: `${t('workflowTest.reactLoop', { count: data.loopCount || 1 })} ${thoughtPreview}`,
            data: thought.length > 50 ? { fullThought: thought } : null,
            timestamp: new Date().toLocaleTimeString(),
            type: 'info',
            icon: 'ChatDotRound'
        })
    })

    // 🆕 监听 ReAct 动作执行
    connection.on('ReActAction', (data) => {
        console.log('[ReActAction]', data)
        const toolName = data.toolName || data.action || t('workflowTest.unknownTool')
        const parameters = data.parameters || data.input || null
        const callId = `${data.taskId}-${toolName}-${Date.now()}`
        toolCalls.value.push({
            taskId: data.taskId,
            toolName,
            parameters,
            callId,
            status: 'calling', // ReActAction表示开始执行
            timestamp: new Date().toLocaleTimeString()
        })
        executionLogs.value.push({
            stage: t('workflowTest.toolCallStage'),
            message: t('workflowTest.loopCallTool', { count: data.loopCount || 1, tool: toolName }),
            data: parameters && Object.keys(parameters).length > 0 ? parameters : null,
            timestamp: new Date().toLocaleTimeString(),
            type: 'warning',
            icon: 'Tools'
        })
    })

    // 🆕 监听 ReAct 观察结果
    connection.on('ReActObservation', (data) => {
        console.log('[ReActObservation]', data)
        // 查找最近一个该工具的 calling 状态记录
        // 注意：这里简化逻辑，取最后一个匹配的记录，实际场景可能需要更精确的匹配ID
        const callIndex = toolCalls.value.map(c => c).reverse().findIndex(c =>
            c.taskId === data.taskId && c.status === 'calling'
        );

        if (callIndex !== -1) {
            const actualIndex = toolCalls.value.length - 1 - callIndex;
            const call = toolCalls.value[actualIndex];
            call.status = data.success ? 'success' : 'failed';
            call.result = data.observation || data.result || t('workflowTest.noResult');
            call.endTime = new Date().toLocaleTimeString();
        }

        const observation = data.observation || data.result || t('workflowTest.noObservation')
        // 🆕 解码 Unicode 转义后再预览
        const decodedObservation = decodeUnicode(observation)
        const observationPreview = decodedObservation.length > 100 ? `${decodedObservation.substring(0, 100)}...` : decodedObservation
        executionLogs.value.push({
            stage: t('workflowTest.toolResultStage'),
            message: `${t('workflowTest.toolReturn', { count: data.loopCount || 1, result: data.success ? t('workflowTest.success') : t('workflowTest.fail') })}: ${observationPreview}`,
            data: observation.length > 100 ? { fullObservation: decodedObservation } : null,
            timestamp: new Date().toLocaleTimeString(),
            type: data.success ? 'success' : 'danger',
            icon: data.success ? 'CircleCheck' : 'CircleClose'
        })
    })

    // 🆕 监听 ReAct 反思/决策
    connection.on('ReActReflection', (data) => {
        console.log('[ReActReflection]', data)
        const decisionMap: Record<number, string> = {
            0: t('workflowTest.decisionContinue'),
            1: t('workflowTest.decisionTaskCompleted'),
            2: t('workflowTest.decisionNeedHumanHelp'),
            3: t('workflowTest.decisionSwitchStrategy')
        };

        // 兼容两种数据格式：
        // 格式1: { decision: number, reason: string, confidence: number }
        // 格式2: { phase: string, content: string, success: boolean }
        let decision = t('workflowTest.deciding')
        let reasoning = ''
        let confidence = 0

        if (data.decision !== undefined && data.decision !== null) {
            // 格式1: 带有 decision 字段
            decision = decisionMap[data.decision] || t('workflowTest.unknownDecision', { decision: data.decision })
            reasoning = data.reason || ''
            confidence = data.confidence || 0
        } else if (data.content) {
            // 格式2: 带有 content 字段（JSON内容）
            try {
                // 尝试从 content 中提取信息
                const contentStr = typeof data.content === 'string' ? data.content : JSON.stringify(data.content)
                // 去除 markdown 代码块标记
                const jsonStr = contentStr.replace(/```json\n?/g, '').replace(/```\n?/g, '').trim()
                const parsed = JSON.parse(jsonStr)
                reasoning = parsed.notes || parsed.reasoning || contentStr.substring(0, 100)
                decision = data.success ? t('workflowTest.execSuccess') : t('workflowTest.execFailed')
            } catch {
                reasoning = typeof data.content === 'string' ? data.content.substring(0, 200) : t('workflowTest.parseFailed')
                decision = data.success ? t('workflowTest.execSuccess') : t('workflowTest.execFailed')
            }
        }

        agentDecisions.value.push({
            taskId: data.taskId,
            decision,
            reasoning,
            alternatives: [],
            timestamp: new Date().toLocaleTimeString()
        })

        executionLogs.value.push({
            stage: t('workflowTest.agentDecision'),
            message: t('workflowTest.loopDecision', { count: data.loopCount || 1, decision }),
            data: reasoning ? { reason: reasoning, confidence } : null,
            timestamp: new Date().toLocaleTimeString(),
            type: 'primary',
            icon: 'Share'
        })
    })

     // 🆕 监听人工介入请求
    connection.on('HumanReviewRequired', (data) => {
        console.log('[HumanReviewRequired]', data)
        executionLogs.value.push({
            stage: t('workflowTest.humanReview'),
            message: t('workflowTest.needConfirm', { reason: data.reason }),
            timestamp: new Date().toLocaleTimeString(),
            type: 'danger',
            icon: 'User'
        })
        // 可以在这里触发弹窗或提示
        ElMessage.warning(t('workflowTest.taskNeedHuman', { reason: data.reason }))
    })

    // 🆕 监听任务完成
    connection.on('TaskCompleted', (data) => {
        console.log('[TaskCompleted]', data)
        executionLogs.value.push({
            stage: t('workflowTest.taskCompletedStage'),
            message: t('workflowTest.taskDone'),
            data: data.result,
            timestamp: new Date().toLocaleTimeString(),
            type: 'success',
            icon: 'CircleCheckFilled'
        })
    })

    // 🆕 监听任务失败
    connection.on('TaskFailed', (data) => {
        console.log('[TaskFailed]', data)
        const errorMsg = data.reason || data.error || data.message || data.errorMessage || t('workflowTest.unknownError')
        executionLogs.value.push({
            stage: t('workflowTest.taskFailedStage'),
            message: t('workflowTest.taskError', { error: decodeUnicode(errorMsg) }),
            timestamp: new Date().toLocaleTimeString(),
            type: 'danger',
            icon: 'CircleCloseFilled'
        })
    })

    // 🆕 监听工具调用
    connection.on('ToolCalling', (data) => {
      console.log('[ToolCalling]', data)
      const toolName = data.toolName || t('workflowTest.unknownTool')
      const parameters = data.parameters || data.input || null
      const callId = `${data.taskId}-${toolName}-${Date.now()}`
      toolCalls.value.push({
        ...data,
        toolName,
        parameters,
        callId,
        status: 'calling',
        timestamp: new Date().toLocaleTimeString()
      })
      executionLogs.value.push({
        stage: t('workflowTest.toolCallStage'),
        message: t('workflowTest.callTool', { tool: toolName }),
        data: parameters && Object.keys(parameters).length > 0 ? parameters : null,
        timestamp: new Date().toLocaleTimeString(),
        type: 'warning',
        icon: 'Tools'
      })
    })

    // 🆕 监听工具结果
    connection.on('ToolResult', (data) => {
      console.log('[ToolResult]', data)
      const toolName = data.toolName || t('workflowTest.unknownTool')
      const result = data.result || data.output || t('workflowTest.noReturnResult')
      const durationMs = data.durationMs ?? 0

      // 更新对应的工具调用记录
      const call = toolCalls.value.find(c =>
        c.taskId === data.taskId &&
        c.toolName === toolName &&
        c.status === 'calling'
      )
      if (call) {
        call.status = data.success ? 'success' : 'failed'
        call.result = result
        call.durationMs = durationMs
        call.endTime = new Date().toLocaleTimeString()
      }

      // 🆕 解码 Unicode 转义序列后再截取预览
      const decodedResult = typeof result === 'string' ? decodeUnicode(result) : result
      const resultPreview = typeof decodedResult === 'string'
        ? (decodedResult.length > 80 ? `${decodedResult.substring(0, 80)}...` : decodedResult)
        : t('workflowTest.objectTypeResult')
      executionLogs.value.push({
        stage: t('workflowTest.toolResultStage'),
        message: `${toolName} ${data.success ? '✅' : '❌'} (${durationMs.toFixed(0)}ms): ${resultPreview}`,
        data: typeof result === 'string' && result.length > 80 ? { fullResult: decodeUnicode(result) } : null,
        timestamp: new Date().toLocaleTimeString(),
        type: data.success ? 'success' : 'danger',
        icon: data.success ? 'CircleCheck' : 'CircleClose'
      })
    })

    // 🆕 监听智能体决策
    connection.on('AgentDecision', (data) => {
      console.log('[AgentDecision]', data)
      agentDecisions.value.push({
        ...data,
        timestamp: new Date().toLocaleTimeString()
      })
      executionLogs.value.push({
        stage: t('workflowTest.agentDecision'),
        message: t('workflowTest.decisionMessage', { decision: data.decision, reasoning: data.reasoning }),
        data: data.alternatives,
        timestamp: new Date().toLocaleTimeString(),
        type: 'primary',
        icon: 'Share'
      })
    })

    // 🆕 监听任务重试
    connection.on('TaskRetrying', (data) => {
      console.log('[TaskRetrying]', data)
      retryHistory.value.push({
        ...data,
        timestamp: new Date().toLocaleTimeString()
      })
      executionLogs.value.push({
        stage: t('workflowTest.taskRetryStage'),
        message: t('workflowTest.retryLog', { count: data.retryCount, reason: data.reason }),
        data: { nextAction: data.nextAction },
        timestamp: new Date().toLocaleTimeString(),
        type: 'warning',
        icon: 'Refresh'
      })
    })

    // 🆕 监听依赖等待
    connection.on('TaskDependencyWaiting', (data) => {
      console.log('[TaskDependencyWaiting]', data)
      dependencyWaits.value.push({
        ...data,
        timestamp: new Date().toLocaleTimeString()
      })
      executionLogs.value.push({
        stage: t('workflowTest.dependencyWaitStage'),
        message: t('workflowTest.waitDependency', { task: data.taskName, deps: data.waitingFor.join(', ') }),
        data: { estimatedSeconds: data.estimatedSeconds },
        timestamp: new Date().toLocaleTimeString(),
        type: 'info',
        icon: 'Clock'
      })
    })

    await connection.start()
    connectionStatus.value = 'Connected'
    console.log('[SignalR] ✅ 连接成功, ConnectionId:', connection.connectionId)
    ElMessage.success(t('workflowTest.signalrConnected'))
  } catch (error) {
    console.error('SignalR连接失败:', error)
    connectionStatus.value = 'Failed'
    ElMessage.error(t('workflowTest.signalrConnectFailed'))
  }
}

// ========== 工作流执行 ==========
async function executeWorkflow() {
  if (!userInput.value.trim()) {
    ElMessage.warning(t('workflowTest.inputTaskRequired'))
    return
  }

  // 🆕 如果有附件，将文件信息融合到任务描述中
  let finalUserInput = userInput.value
  if (attachedFiles.value.length > 0) {
    const fileContext = attachedFiles.value.map((file, index) =>
      t('workflowTest.attachmentContext', { index: index + 1, name: file.fileName, path: file.filePath, size: formatFileSize(file.size) })
    ).join('')

    finalUserInput = `${userInput.value}${fileContext}\n\n${t('workflowTest.analyzeAttachmentsPrompt')}`
    console.log('[任务执行] 融合附件信息:', finalUserInput)
  }

  // 重置状态
  currentWorkflow.value = null
  currentStage.value = ''
  currentMessage.value = ''
  executionLogs.value = []
  tasks.value = []
  finalResult.value = ''
  displayedResult.value = ''
  isStreaming.value = false
  currentStreamingTask.value = ''
  expertResults.value.clear()
  isExecuting.value = true
  startTime.value = new Date().toLocaleTimeString()
  endTime.value = ''

  // 🆕 重置可视化状态
  agentThoughts.value = []
  toolCalls.value = []
  agentDecisions.value = []
  retryHistory.value = []
  dependencyWaits.value = []

  try {
    const apiUrl = import.meta.env.DEV
      ? import.meta.env.VITE_APP_API_BASEURL
      : (window as any).apiConfig?.apiUrl || ''

    // 统一调用智能编排接口
    const response = await fetch(`${apiUrl}/api/WorkflowTest/simple-run`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        userInput: finalUserInput,
        plannerRoleId: null, // 明确传null，由系统根据需求自动选择Planner
      }),
    })

    const result = await response.json()
    console.log('工作流创建结果:', result)

    if (result.workflowId) {
      const workflowIdString = String(result.workflowId)

      currentWorkflow.value = {
        workflowId: workflowIdString,
        mode: 'agent-orchestration',
      }

      // 加入SignalR分组
      if (connection) {
        console.log('[SignalR] 准备加入分组:', workflowIdString)
        currentWorkflowId.value = workflowIdString
        try {
          await connection.invoke('JoinWorkflowGroup', workflowIdString)
          console.log('[SignalR] ✅ 成功加入分组:', workflowIdString)
          ElMessage.success(t('workflowTest.taskSubmitted', { id: workflowIdString }))
        } catch (error) {
          console.error('[SignalR] ❌ 加入分组失败:', error)
          ElMessage.error(t('workflowTest.joinGroupFailed'))
        }
      }
    } else {
      throw new Error(result.errorMessage || t('workflowTest.createFailed'))
    }
  } catch (error: any) {
    console.error('执行失败:', error)
    ElMessage.error(error.message || t('workflowTest.execFailedConfig'))
    isExecuting.value = false
  }
}

// ========== 辅助方法 ==========
function getStageType(stage: string) {
  if (stage === 'Failed') return 'danger'
  if (stage === 'Completed') return 'success'
  if (stage.includes('Analyzing') || stage.includes('Executing')) return 'warning'
  return 'info'
}

function getTaskStatusType(status: number) {
  const typeMap: Record<number, string> = {
    0: 'info',
    1: 'info',
    2: 'warning',
    3: 'success',
    4: 'danger',
    5: 'warning',
    6: 'warning',
  }
  return typeMap[status] || 'info'
}

function getLogType(stage: string) {
  if (stage === 'Failed') return 'danger'
  if (stage === 'Completed') return 'success'
  if (stage === 'Started') return 'primary'
  return 'info'
}

function getLogIcon(stage: string) {
  if (stage === 'Failed') return 'CircleClose'
  if (stage === 'Completed') return 'CircleCheck'
  if (stage === 'Started') return 'VideoPlay'
  return 'Clock'
}

// 🆕 辅助函数：解码 Unicode 转义序列（如 \u661F\u671F -> 星期）
function decodeUnicode(str: string): string {
  if (!str) return str
  return str.replace(/\\u([0-9a-fA-F]{4})/g, (_, hex) =>
    String.fromCharCode(parseInt(hex, 16))
  )
}

// 🆕 格式化调用数据，防止 undefined 显示，并解码 Unicode 转义
function formatCallData(data: any): string {
  if (data === undefined || data === null) {
    return t('workflowTest.noData')
  }

  let result: string

  if (typeof data === 'string') {
    try {
      // 如果是 JSON 字符串，先解析再格式化
      if (data.startsWith('{') || data.startsWith('[')) {
        const parsed = JSON.parse(data)
        result = JSON.stringify(parsed, null, 2)
      } else {
        result = data || t('workflowTest.emptyString')
      }
    } catch {
      result = data || t('workflowTest.emptyString')
    }
  } else {
    try {
      result = JSON.stringify(data, null, 2)
    } catch {
      result = String(data)
    }
  }

  // 统一解码所有 Unicode 转义序列
  return decodeUnicode(result)
}

function loadTestCase(type: string) {
  if (type === 'plc') {
    userInput.value = t('workflowTest.plcTestCase')
  } else if (type === 'parser') {
    userInput.value = t('workflowTest.parserTestCase')
  } else if (type === 'optimization') {
    userInput.value = t('workflowTest.optimizationTestCase')
  }
}

// ========== 🆕 文件上传相关方法 ==========

// 主输入区附件上传成功
function handleAttachmentSuccess(response: any) {
  console.log('[附件上传] 成功:', response)
  if (response.success) {
    attachedFiles.value.push(response.file)
    ElMessage.success(t('workflowTest.fileUploaded', { name: response.file.fileName }))
  } else {
    ElMessage.error(response.error || t('workflowTest.uploadFailed'))
  }
}

// 附件上传失败
function handleAttachmentError(error: any) {
  console.error('[附件上传] 失败:', error)
  ElMessage.error(t('workflowTest.uploadRetry'))
}

// 移除附件
function removeAttachment(index: number) {
  const file = attachedFiles.value[index]
  attachedFiles.value.splice(index, 1)
  ElMessage.info(t('workflowTest.fileRemoved', { name: file.fileName }))
}

function beforeUpload(file: File) {
  const isLt10M = file.size / 1024 / 1024 < 10
  if (!isLt10M) {
    ElMessage.error(t('workflowTest.sizeLimitError'))
    return false
  }
  return true
}

// 快捷上传区的文件上传成功（只上传，不执行工作流）
function handleQuickUploadSuccess(response: any) {
  console.log('[快捷上传] 成功:', response)
  if (response.success) {
    uploadedFile.value = response.file
    // 也添加到附件列表，便于主输入区引用
    attachedFiles.value.push(response.file)

    ElMessage.success({
      message: t('workflowTest.fileUploadedDescribe', { name: response.file.fileName }),
      duration: 3000
    })
  } else {
    ElMessage.error(response.error || t('workflowTest.uploadFailed'))
  }
}

// 原 upload-and-diagnose 接口的成功回调（带工作流创建）
async function handleUploadSuccess(response: any) {
  console.log('[文件上传] 成功:', response)
  if (response.success) {
    uploadedFile.value = response.file

    ElMessage.success({
      message: t('workflowTest.uploadSuccessWorkflow'),
      duration: 3000
    })

    // 自动加入工作流分组并监听进度
    if (response.workflowId && connection) {
      const workflowIdString = String(response.workflowId)
      currentWorkflowId.value = workflowIdString
      currentWorkflow.value = {
        workflowId: workflowIdString,
        mode: 'agent-orchestration',
      }
      isExecuting.value = true
      startTime.value = new Date().toLocaleTimeString()

      try {
        await connection.invoke('JoinWorkflowGroup', workflowIdString)
        console.log('[SignalR] ✅ 成功加入工作流分组:', workflowIdString)
        ElMessage.info(t('workflowTest.analyzingFile', { id: workflowIdString }))
      } catch (error) {
        console.error('[SignalR] ❌ 加入分组失败:', error)
        ElMessage.error(t('workflowTest.joinGroupFailed'))
      }
    }
  } else {
    ElMessage.error(response.error || t('workflowTest.uploadFailed'))
  }
}

function handleUploadError(error: any) {
  console.error('[文件上传] 失败:', error)
  ElMessage.error(t('workflowTest.uploadRetry'))
}

function clearFile() {
  uploadedFile.value = null
  ElMessage.info(t('workflowTest.fileCleared'))
}

function formatFileSize(bytes: number): string {
  if (bytes < 1024) return bytes + ' B'
  if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(2) + ' KB'
  return (bytes / 1024 / 1024).toFixed(2) + ' MB'
}

function changeUploadTask(task: string | number | boolean | undefined) {
  if (typeof task !== 'string') return
  console.log('[上传任务] 切换为:', task)
  const taskName = task === 'detect' ? t('workflowTest.modeDetect') : task === 'parse' ? t('workflowTest.modeParse') : t('workflowTest.modeDiagnose')
  ElMessage.info(t('workflowTest.switchedMode', { name: taskName }))
}

// ========== 生命周期 ==========
onMounted(() => {
  connectSignalR()
})

onUnmounted(() => {
  if (connection) {
    connection.stop()
  }
})
</script>

<style scoped lang="scss">
.workflow-test-page {
  padding: 24px;
  background: #f5f7fa;
  min-height: 100vh;

  .page-header {
    text-align: center;
    margin-bottom: 24px;

    h1 {
      font-size: 32px;
      font-weight: bold;
      margin: 0;
      background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
      -webkit-background-clip: text;
      -webkit-text-fill-color: transparent;
    }

    .subtitle {
      color: #909399;
      margin-top: 8px;
    }
  }

  .content-layout {
    display: grid;
    grid-template-columns: 400px 1fr;
    gap: 24px;

    @media (max-width: 1200px) {
      grid-template-columns: 1fr;
    }
  }

  .card-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
  }

  .hint {
    font-size: 12px;
    color: #909399;
    margin-top: 4px;
  }

  .expert-config {
    margin-top: 8px;
  }

  .expert-item {
    display: flex;
    gap: 8px;
    align-items: center;
    margin-top: 8px;
  }

  .empty-state {
    text-align: center;
    padding: 40px 0;
  }

  .workflow-progress {
    .current-stage {
      margin: 24px 0;
      display: flex;
      align-items: center;
      gap: 12px;

      .stage-message {
        font-size: 16px;
        font-weight: 500;
      }
    }

    .execution-log {
      h4 {
        margin: 24px 0 16px 0;
      }

      .log-content {
        strong {
          color: #409eff;
        }

        p {
          margin: 8px 0;
          color: #606266;
        }

        .log-data {
          background: #f5f7fa;
          padding: 12px;
          border-radius: 4px;
          font-size: 12px;
          overflow-x: auto;
        }
      }
    }
  }

  .task-output {
    white-space: pre-wrap;
    word-break: break-word;
    max-height: 300px;
    overflow-y: auto;
    padding: 12px;
    background: #f5f7fa;
    border-radius: 4px;
    font-family: 'Courier New', monospace;
    font-size: 13px;
  }

  // 对话框样式
  .chat-container {
    max-height: 600px;
    overflow-y: auto;
    padding: 16px;

    .empty-chat {
      padding: 40px 0;
    }

    .message-item {
      display: flex;
      gap: 12px;
      margin-bottom: 24px;
      animation: fadeInUp 0.3s ease-out;

      &.user-message {
        flex-direction: row;
      }

      &.assistant-message {
        flex-direction: row;
      }

      .message-avatar {
        flex-shrink: 0;
      }

      .message-content {
        flex: 1;
        min-width: 0;

        .message-header {
          display: flex;
          align-items: center;
          gap: 12px;
          margin-bottom: 8px;

          .message-sender {
            font-weight: 600;
            font-size: 14px;
            color: #303133;
          }

          .message-time {
            font-size: 12px;
            color: #909399;
          }
        }

        .message-body {
          padding: 16px;
          border-radius: 12px;
          line-height: 1.8;
          font-size: 14px;
          position: relative;

          &.user-body {
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            color: white;
            box-shadow: 0 2px 8px rgba(102, 126, 234, 0.3);
          }

          &.assistant-body {
            background: #f0f9ff;
            color: #303133;
            border: 1px solid #e0f2ff;
            box-shadow: 0 2px 8px rgba(64, 158, 255, 0.1);
            padding: 16px; // 恢复 padding

            // Markdown 样式
            :deep(.markdown-body) {
              font-size: 14px;
              line-height: 1.6;

              h1, h2, h3, h4, h5, h6 {
                margin-top: 24px;
                margin-bottom: 16px;
                font-weight: 600;
                line-height: 1.25;
              }

              h1 { font-size: 2em; padding-bottom: 0.3em; border-bottom: 1px solid #eaecef; }
              h2 { font-size: 1.5em; padding-bottom: 0.3em; border-bottom: 1px solid #eaecef; }
              h3 { font-size: 1.25em; }

              p, blockquote, ul, ol, dl, table, pre {
                margin-top: 0;
                margin-bottom: 16px;
              }

              ul, ol {
                padding-left: 2em;
              }

              blockquote {
                padding: 0 1em;
                color: #6a737d;
                border-left: 0.25em solid #dfe2e5;
              }

              pre {
                padding: 16px;
                overflow: auto;
                font-size: 85%;
                line-height: 1.45;
                background-color: #f6f8fa;
                border-radius: 6px;
              }

              code {
                padding: 0.2em 0.4em;
                margin: 0;
                font-size: 85%;
                background-color: rgba(27, 31, 35, 0.05);
                border-radius: 3px;
              }

              pre code {
                display: inline;
                max-width: auto;
                padding: 0;
                margin: 0;
                overflow: visible;
                line-height: inherit;
                word-wrap: normal;
                background-color: transparent;
                border: 0;
              }
            }
          }
        }
      }
    }
  }

  @keyframes fadeInUp {
    from {
      opacity: 0;
      transform: translateY(20px);
    }
    to {
      opacity: 1;
      transform: translateY(0);
    }
  }

  .text-muted {
    color: #c0c4cc;
  }

  // 🆕 过程可视化样式
  .tool-call-item {
    .code-block {
      background: #f6f8fa;
      padding: 12px;
      border-radius: 4px;
      font-size: 12px;
      overflow-x: auto;
      margin: 0;
      max-height: 300px;
      overflow-y: auto;
    }
  }

  .thoughts-container {
    .thought-item {
      padding: 16px;
      margin-bottom: 12px;
      background: #f9fafb;
      border-radius: 8px;
      border-left: 4px solid #67c23a;

      .thought-header {
        display: flex;
        align-items: center;
        gap: 12px;
        margin-bottom: 12px;

        .thought-agent {
          font-weight: 600;
          color: #303133;
          flex: 1;
        }

        .thought-time {
          font-size: 12px;
          color: #909399;
        }
      }

      .thought-content {
        color: #606266;
        line-height: 1.6;
        white-space: pre-wrap;
        word-break: break-word;
      }
    }
  }

  // 🆕 文件上传样式
  .upload-demo {
    :deep(.el-upload) {
      width: 100%;
    }

    :deep(.el-upload-dragger) {
      width: 100%;
      border: 2px dashed #d9d9d9;
      border-radius: 8px;
      transition: all 0.3s;

      &:hover {
        border-color: #409eff;
        background-color: #f0f9ff;
      }
    }

    .el-icon--upload {
      font-size: 48px;
      color: #409eff;
      margin: 20px 0 16px;
    }

    .el-upload__text {
      font-size: 14px;
      color: #606266;

      em {
        color: #409eff;
        font-style: normal;
      }
    }

    .el-upload__tip {
      font-size: 12px;
      color: #909399;
      margin-top: 8px;
      line-height: 1.5;
    }
  }

  .file-info {
    p {
      margin: 8px 0;
      font-size: 14px;
      color: #606266;

      strong {
        color: #303133;
        margin-right: 8px;
      }
    }
  }

  // 🆕 附件区域样式
  .attachment-section {
    display: flex;
    align-items: center;
  }

  .attached-files {
    padding: 12px;
    background: #f0f9ff;
    border: 1px dashed #409eff;
    border-radius: 4px;
  }
}

</style>
