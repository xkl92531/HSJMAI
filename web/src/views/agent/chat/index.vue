<route lang="yaml">
meta:
  title: AI对话
  icon: i-ri:chat-smile-2-line
</route>

<template>
  <div class="chat-container">
    <!-- 左侧：会话列表 -->
    <div class="session-panel" :class="{ collapsed: isSessionCollapsed }">
      <SessionList
        v-model:current-session="currentSession"
        :sessions="sessions"
        :loading="sessionsLoading"
        @create="handleCreateSession"
        @delete="handleDeleteSession"
        @refresh="loadSessions(false)"
        @load-more="handleSessionLoadMore"
      />
      <div class="collapse-btn" @click="isSessionCollapsed = !isSessionCollapsed">
        <el-icon>
          <component :is="isSessionCollapsed ? 'DArrowRight' : 'DArrowLeft'" />
        </el-icon>
      </div>
    </div>

    <!-- 中间：聊天主区域 -->
    <div class="chat-main">
      <!-- 执行过程切换按钮 -->
      <div class="thinking-toggle" @click="isThinkingCollapsed = !isThinkingCollapsed">
        <el-badge :value="executionLogs.length || undefined" :hidden="executionLogs.length === 0" type="primary">
          <el-button :type="isThinkingCollapsed ? 'default' : 'primary'" circle size="small">
            <el-icon><Monitor /></el-icon>
          </el-button>
        </el-badge>
        <span class="toggle-text">{{ isThinkingCollapsed ? $t('agentChat.viewExecution') : $t('agentChat.hideExecution') }}</span>
      </div>
      <ChatPanel
        ref="chatPanelRef"
        :session="currentSession"
        :messages="messages"
        :loading="messagesLoading"
        :sending="isSending"
        :connection-status="connectionStatus"
        @send="handleSendMessage"
        @upload="handleFileUpload"
        @stop="handleStopWorkflow"
        @load-more="handleLoadMore"
      />
    </div>

    <!-- 右侧：思考过程（可折叠） -->
    <div class="thinking-panel" :class="{ collapsed: isThinkingCollapsed }">
      <div class="collapse-btn left" @click="isThinkingCollapsed = !isThinkingCollapsed">
        <el-icon>
          <component :is="isThinkingCollapsed ? 'DArrowLeft' : 'DArrowRight'" />
        </el-icon>
      </div>
      <ThinkingProcess
        v-if="!isThinkingCollapsed"
        :execution-logs="executionLogs"
        :tool-calls="toolCalls"
        :agent-thoughts="agentThoughts"
        :current-stage="currentStage"
      />
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, onUnmounted, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { Monitor } from '@element-plus/icons-vue'
import * as signalR from '@microsoft/signalr'
import SessionList from './components/SessionList.vue'
import ChatPanel from './components/ChatPanel.vue'
import ThinkingProcess from './components/ThinkingProcess.vue'
import { useI18n } from 'vue-i18n'
import langMessages from './components/lang'
import api from '@/api'

const { t, mergeLocaleMessage } = useI18n()
// 遍历所有的语言和它们的消息
for (const locale in langMessages) {
  mergeLocaleMessage(locale, langMessages[locale])
}

// API 基础路径（与测试页保持一致的获取方式）
const apiUrl = import.meta.env.DEV
  ? import.meta.env.VITE_APP_API_BASEURL
  : (window as any).apiConfig?.apiUrl || ''

// 面板折叠状态
const isSessionCollapsed = ref(false)
const isThinkingCollapsed = ref(true)

// 会话相关
const sessions = ref<any[]>([])
const currentSession = ref<any>(null)
const sessionsLoading = ref(false)

// 消息相关
const messages = ref<any[]>([])
const messagesLoading = ref(false)
const isSending = ref(false)

// 思考过程
const executionLogs = ref<any[]>([])
const toolCalls = ref<any[]>([])
const agentThoughts = ref<any[]>([])
const currentStage = ref('')

// 上传文件路径存储
const uploadedFiles = ref<{ name: string, path: string }[]>([])

// SignalR 连接
const connectionStatus = ref('Disconnected')
let connection: signalR.HubConnection | null = null
let currentWorkflowId: string | null = null

// 聊天面板引用
const chatPanelRef = ref()

// 初始化 SignalR 连接
async function initSignalR() {
  try {
    connection = new signalR.HubConnectionBuilder()
      .withUrl(`${apiUrl}/hubs/agent-streaming`, {
        withCredentials: false, // 禁用凭证以避免CORS问题
        skipNegotiation: false,
      })
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: () => 2000, // 永久重试机制：每2秒重试一次
      })
      .configureLogging(signalR.LogLevel.Information)
      .build()

    // 连接状态监听
    connection.onreconnecting((error) => {
      console.warn('[Chat] SignalR 连接断开，正在重连...', error)
      connectionStatus.value = 'Reconnecting'
      ElMessage.warning(t('agentChat.connReconnecting'))
    })
    connection.onreconnected((connectionId) => {
      console.log('[Chat] SignalR 重连成功，ConnectionId:', connectionId)
      connectionStatus.value = 'Connected'
      ElMessage.success(t('agentChat.connRestored'))
      if (currentWorkflowId) {
        connection?.invoke('JoinWorkflowGroup', currentWorkflowId)
          .then(() => console.log('[Chat] 重连后重新加入分组:', currentWorkflowId))
      }
    })
    connection.onclose((error) => {
      console.error('[Chat] SignalR 连接关闭', error)
      connectionStatus.value = 'Disconnected'
    })

    // 注册事件监听
    registerSignalREvents()

    await connection.start()
    connectionStatus.value = 'Connected'
    console.log('[Chat] SignalR 连接成功, ConnectionId:', connection.connectionId)
    ElMessage.success(t('agentChat.connSuccess'))
  }
  catch (err) {
    console.error('[Chat] SignalR 连接失败:', err)
    connectionStatus.value = 'Error'
    ElMessage.error(t('agentChat.connFailed'))
  }
}

// 注册 SignalR 事件
function registerSignalREvents() {
  if (!connection) return

  // 工作流开始
  connection.on('WorkflowStarted', (data: any) => {
    console.log('[WorkflowStarted]', data)
    currentStage.value = t('agentChat.stageWorkflowStarted')
    executionLogs.value.push({
      stage: 'Started',
      message: t('agentChat.workflowStartedLog', { id: data.workflowId }),
      timestamp: new Date().toLocaleTimeString(),
      type: 'primary',
    })
  })

  // 规划开始
  connection.on('PlanningStarted', (data: any) => {
    console.log('[PlanningStarted]', data)
    currentStage.value = t('agentChat.stagePlanning')
    executionLogs.value.push({
      stage: 'Planning',
      message: t('agentChat.analyzingTask'),
      timestamp: new Date().toLocaleTimeString(),
      type: 'info',
    })
  })

  // ReAct 思考
  connection.on('ReActThought', (data: any) => {
    console.log('[ReActThought]', data)
    const thought = data.thought || data.content || t('agentChat.thinkingFallback')
    agentThoughts.value.push({
      taskId: data.taskId,
      thought: `${t('agentChat.roundPrefix', { n: data.loopCount || 1 })}${thought}`,
      timestamp: new Date().toLocaleTimeString(),
      type: 'thought'
    })
    currentStage.value = t('agentChat.stageAgentThinking')
  })

  // ReAct 反思
  connection.on('ReActReflection', (data: any) => {
    console.log('[ReActReflection]', data)
    const content = data.content || data.thought || t('agentChat.reflectingFallback')
    agentThoughts.value.push({
      taskId: data.taskId,
      thought: `${t('agentChat.roundPrefix', { n: data.loopCount || 1 })}${content}`,
      timestamp: new Date().toLocaleTimeString(),
      type: 'reflection'
    })
    currentStage.value = t('agentChat.stageAgentReflecting')
  })

  // 工具调用
  connection.on('ToolCalling', (data: any) => {
    console.log('[ToolCalling]', data)
    toolCalls.value.push({
      ...data,
      status: 'calling',
      timestamp: new Date().toLocaleTimeString(),
    })
    currentStage.value = t('agentChat.callingTool', { name: data.toolName })
  })

  // 工具结果
  connection.on('ToolResult', (data: any) => {
    console.log('[ToolResult]', data)
    const call = toolCalls.value.find(c =>
      c.taskId === data.taskId && c.toolName === data.toolName && c.status === 'calling',
    )
    if (call) {
      call.status = data.success ? 'success' : 'failed'
      call.result = data.result
      call.durationMs = data.durationMs
    }
  })

  // 流式输出开始
  connection.on('ExpertStreamStart', (data: any) => {
    console.log('[ExpertStreamStart]', data)
    currentStage.value = t('agentChat.stageGenerating')
  })

  // 流式 token
  connection.on('ExpertStreamToken', (data: any) => {
    // 追加到当前助手消息
    if (chatPanelRef.value) {
      chatPanelRef.value.appendToken(data.token)
    }
  })

  // 流式输出完成
  connection.on('ExpertStreamComplete', (data: any) => {
    console.log('[ExpertStreamComplete]', data)
  })

  // 工作流完成
  connection.on('WorkflowCompleted', (data: any) => {
    console.log('[WorkflowCompleted]', data)
    currentStage.value = t('agentChat.stageCompleted')
    isSending.value = false
    // 不自动收起执行过程面板，让用户可以查看执行详情

    // 如果有总结，添加到消息（强制显示，不管是否有流式内容）
    if (chatPanelRef.value) {
      const summary = data.summary || data.result || ''
      chatPanelRef.value.finishAssistantMessage(summary)
    }

    // 添加完成日志
    executionLogs.value.push({
      stage: 'Completed',
      message: t('agentChat.workflowCompletedLog', { duration: data.duration || 'N/A' }),
      timestamp: new Date().toLocaleTimeString(),
      type: 'success',
    })

    // 刷新消息列表
    if (currentSession.value) {
      loadMessages(currentSession.value.id)
      loadSessions() // 刷新会话列表（更新时间、消息数等）
    }
  })

  // 工作流失败
  connection.on('WorkflowFailed', (data: any) => {
    console.log('[WorkflowFailed]', data)
    currentStage.value = t('agentChat.stageFailed')
    isSending.value = false
    
    let errorMsg = data.error || data.reason || t('agentChat.unknownError')
    let isNeedHumanHelp = false

    // 🆕 检查是否为 NeedHumanHelp
    if (errorMsg.startsWith('NEED_HUMAN_HELP:')) {
      isNeedHumanHelp = true
      errorMsg = errorMsg.replace('NEED_HUMAN_HELP:', '').trim()
      currentStage.value = t('agentChat.stageAwaitingHuman')
      
      // 使用 Info/Warning 提示而不是 Error
      ElMessage.warning(t('agentChat.needHumanHelpTip'))
    } else {
      ElMessage.error(t('agentChat.execFailed', { msg: errorMsg }))
    }

    if (chatPanelRef.value) {
      // 如果是人工协助，作为正常消息结束（isError=false），但内容是问题
      // 如果是真错误，isError=true
      const displayMsg = isNeedHumanHelp ? t('agentChat.needHumanHelpMsg', { msg: errorMsg }) : t('agentChat.execFailed', { msg: errorMsg })
      chatPanelRef.value.finishAssistantMessage(displayMsg, !isNeedHumanHelp)
    }
  })

  // 任务失败
  connection.on('TaskFailed', (data: any) => {
    console.log('[TaskFailed]', data)
    const errorMsg = data.reason || data.error || t('agentChat.unknownError')
    executionLogs.value.push({
      stage: 'TaskFailed',
      message: t('agentChat.taskFailedLog', { msg: errorMsg }),
      timestamp: new Date().toLocaleTimeString(),
      type: 'danger',
    })
  })

  // 工作流取消
  connection.on('WorkflowCancelled', (data: any) => {
    console.log('[WorkflowCancelled]', data)
    currentStage.value = t('agentChat.stageCancelled')
    isSending.value = false
    ElMessage.warning(t('agentChat.taskCancelledMsg'))

    if (chatPanelRef.value) {
      chatPanelRef.value.finishAssistantMessage(t('agentChat.taskCancelledMsg'), true)
    }

    executionLogs.value.push({
      stage: 'Cancelled',
      message: data.message || t('agentChat.workflowCancelled'),
      timestamp: new Date().toLocaleTimeString(),
      type: 'warning',
    })
  })
}

// 分页状态
const sessionPage = ref(1)
const sessionPageSize = 20
const sessionHasMore = ref(true)

// 加载会话列表
async function loadSessions(isLoadMore = false) {
  if (isLoadMore && !sessionHasMore.value) return
  if (sessionsLoading.value) return

  sessionsLoading.value = true
  try {
    const page = isLoadMore ? sessionPage.value + 1 : 1
    const res: any = await api.get(`${apiUrl}/api/chat/sessions/active`, {
      params: {
        pageIndex: page,
        pageSize: sessionPageSize
      }
    })
    
    if (res.success) {
      const newSessions = res.sessions || []
      if (isLoadMore) {
        sessions.value = [...sessions.value, ...newSessions]
        sessionPage.value = page
      } else {
        sessions.value = newSessions
        sessionPage.value = 1
      }
      
      sessionHasMore.value = newSessions.length >= sessionPageSize
    }
  }
  catch (err) {
    console.error('加载会话失败:', err)
  }
  finally {
    sessionsLoading.value = false
  }
}

// 监听会话列表加载更多
function handleSessionLoadMore() {
  loadSessions(true)
}

// 加载会话消息
async function loadMessages(sessionId: string, beforeId?: string) {
  if (!sessionId)
    return
  
  // 如果是加载更多（有beforeId），不显示全屏loading
  if (!beforeId) {
    messagesLoading.value = true
  }

  try {
    const url = `${apiUrl}/api/chat/session/${sessionId}/history${beforeId ? `?beforeId=${beforeId}` : ''}`
    const res: any = await api.get(url)
    if (res.success) {
      if (beforeId) {
        // 加载更多：添加到头部
        const newMessages = res.messages || []
        if (newMessages.length > 0) {
          // 记录当前滚动高度
          const oldScrollParams = chatPanelRef.value?.getScrollParams()
          
          messages.value = [...newMessages, ...messages.value]
          
          // 等待DOM更新后恢复滚动位置
          nextTick(() => {
            if (chatPanelRef.value && oldScrollParams) {
              const newScrollParams = chatPanelRef.value.getScrollParams()
              const scrollDiff = newScrollParams.scrollHeight - oldScrollParams.scrollHeight
              chatPanelRef.value.setScrollTop(scrollDiff)
            }
          })
        } else {
          // 没有更多了
          //ElMessage.info('没有更多历史消息')
        }
      } else {
        // 初始加载
        messages.value = res.messages || []
        // 初始加载需要滚动到底部
        nextTick(() => {
          chatPanelRef.value?.scrollToBottom()
        })
      }
    }
  }
  catch (err) {
    console.error('加载消息失败:', err)
  }
  finally {
    messagesLoading.value = false
  }
}

// 加载更多消息
function handleLoadMore() {
  if (messages.value.length > 0 && !messagesLoading.value) {
    const oldestMessage = messages.value[0]
    if (oldestMessage && oldestMessage.id) {
      loadMessages(currentSession.value.id, oldestMessage.id)
    }
  }
}

// 创建新会话
async function handleCreateSession() {
  // 检查是否已有空会话（MessageCount为0 或 标题为“新对话”且无消息）
  // 注意：这里假设 sessions 列表中的 MessageCount 数据是准确的
  const emptySession = sessions.value.find(s => 
    s.messageCount === 0 || (s.title === '新对话' && s.messageCount === 0)
  )

  if (emptySession) {
    if (currentSession.value?.id !== emptySession.id) {
      currentSession.value = emptySession
    }
    return
  }

  try {
    const res: any = await api.post(`${apiUrl}/api/chat/session/create`, {
      title: '新对话',
      sessionType: 'DirectChat',
    })
    if (res.success) {
      await loadSessions()
      // 选中新创建的会话
      const newSession = sessions.value.find(s => s.id === res.sessionId)
      if (newSession) {
        currentSession.value = newSession
      }
    }
  }
  catch (err) {
    ElMessage.error(t('agentChat.createSessionFailed'))
  }
}

// 删除会话
async function handleDeleteSession(sessionId: string) {
  try {
    await api.post(`${apiUrl}/api/chat/session/${sessionId}/archive`)
    await loadSessions()
    if (currentSession.value?.id === sessionId) {
      currentSession.value = sessions.value[0] || null
    }
  }
  catch (err) {
    ElMessage.error(t('agentChat.deleteSessionFailed'))
  }
}

// 发送消息
async function handleSendMessage(content: string) {
  if (!content.trim() || isSending.value)
    return

  isSending.value = true
  isThinkingCollapsed.value = false
  executionLogs.value = []
  toolCalls.value = []
  agentThoughts.value = []
  currentStage.value = t('agentChat.stageSending')

  // 构建完整消息内容（包含上传的文件路径）
  let fullContent = content
  if (uploadedFiles.value.length > 0) {
    const filePaths = uploadedFiles.value.map(f => f.path).filter(p => p)
    if (filePaths.length > 0) {
      fullContent = `${content}\n\n【附件文件】\n${filePaths.join('\n')}`
    }
  }

  // 添加用户消息到界面
  if (chatPanelRef.value) {
    chatPanelRef.value.addUserMessage(content)
    chatPanelRef.value.addAssistantMessage('') // 预创建助手消息占位
  }

  try {
    const res: any = await api.post(`${apiUrl}/api/chat/message/send`, {
      sessionId: currentSession.value?.id || null,
      content: fullContent,
    })

    if (res.success) {
      const workflowId = res.workflowId
      const sessionId = res.sessionId

      // 清空上传文件列表
      uploadedFiles.value = []
      if (chatPanelRef.value) {
        chatPanelRef.value.clearAttachments()
      }

      // 更新当前会话
      if (!currentSession.value && sessionId) {
        await loadSessions()
        currentSession.value = sessions.value.find(s => s.id === sessionId)
      }

      // 如果是新会话（标题为默认值），根据第一条消息自动重命名
        if (currentSession.value && currentSession.value.title === '新对话') {
          // 实际上UI显示通常较短，可以取20个字符
          const displayTitle = content.trim().substring(0, 20) + (content.length > 20 ? '...' : '')
        
        try {
          // 调用重命名接口
          await api.post(`${apiUrl}/api/chat/session/${currentSession.value.id}/rename`, {
            title: displayTitle
          })
          
          // 更新本地状态
          currentSession.value.title = displayTitle
          const sessionInList = sessions.value.find(s => s.id === currentSession.value.id)
          if (sessionInList) {
            sessionInList.title = displayTitle
          }
        }
        catch (renameErr) {
          console.warn('[Chat] 自动重命名失败:', renameErr)
        }
      }

      // 订阅工作流消息
      if (workflowId && connection?.state === signalR.HubConnectionState.Connected) {
        currentWorkflowId = workflowId
        await connection.invoke('JoinWorkflowGroup', workflowId)
        console.log('[Chat] 已订阅工作流:', workflowId)
      }
    }
    else {
      throw new Error(res.errorMessage || t('agentChat.sendFailed'))
    }
  }
  catch (err: any) {
    isSending.value = false
    ElMessage.error(err.message || t('agentChat.sendMsgFailed'))
    if (chatPanelRef.value) {
      chatPanelRef.value.finishAssistantMessage(t('agentChat.sendFailedRetry'), true)
    }
  }
}

// 停止工作流执行
async function handleStopWorkflow() {
  if (!currentWorkflowId) {
    ElMessage.warning(t('agentChat.noRunningTask'))
    return
  }

  try {
    ElMessage.info(t('agentChat.cancellingTask'))
    const res: any = await api.post(`${apiUrl}/api/chat/workflow/${currentWorkflowId}/cancel`)
    
    if (res.success) {
      ElMessage.success(t('agentChat.taskCancelled'))
      isSending.value = false
      currentStage.value = t('agentChat.stageCancelled')
      
      if (chatPanelRef.value) {
        chatPanelRef.value.finishAssistantMessage(t('agentChat.taskCancelledByUser'), true)
      }
      
      // 添加取消日志
      executionLogs.value.push({
        stage: 'Cancelled',
        message: t('agentChat.userCancelledLog'),
        timestamp: new Date().toLocaleTimeString(),
        type: 'warning',
      })
    }
    else {
      ElMessage.warning(res.message || t('agentChat.cancelFailed'))
    }
  }
  catch (err) {
    ElMessage.error(t('agentChat.cancelRequestFailed'))
    console.error('[Chat] 取消工作流失败:', err)
  }
}

// 文件上传
async function handleFileUpload(file: File) {
  const formData = new FormData()
  formData.append('file', file)

  try {
    const res: any = await api.post(`${apiUrl}/api/WorkflowTest/upload-plc-file`, formData, {
      headers: { 'Content-Type': 'multipart/form-data' },
    })
    if (res.success) {
      ElMessage.success(t('agentChat.fileUploadSuccess', { name: file.name }))
      // 存储上传成功的文件路径（后端返回格式：res.file.filePath）
      const filePath = res.file?.filePath || res.filePath || ''
      if (filePath) {
        uploadedFiles.value.push({
          name: file.name,
          path: filePath,
        })
      }
      return res
    }
  }
  catch (err) {
    ElMessage.error(t('agentChat.fileUploadFailed'))
  }
  return null
}

// 监听会话切换
watch(currentSession, (newSession) => {
  if (newSession?.id) {
    loadMessages(newSession.id)
  }
  else {
    messages.value = []
  }
})

onMounted(async () => {
  await initSignalR()
  await loadSessions()
  // 默认选中第一个会话
  if (sessions.value.length > 0) {
    currentSession.value = sessions.value[0]
  }
})

onUnmounted(() => {
  if (connection) {
    connection.stop()
  }
})
</script>

<style lang="scss" scoped>
.chat-container {
  display: flex;
  height: calc(100vh - 120px);
  background: var(--el-bg-color);
  border-radius: 8px;
  overflow: hidden;
}

.session-panel {
  position: relative;
  width: 280px;
  min-width: 280px;
  border-right: 1px solid var(--el-border-color-light);
  transition: all 0.3s ease;
  background: var(--el-bg-color-page);

  &.collapsed {
    width: 0;
    min-width: 0;
    border-right: none;

    // 收缩时内容隐藏但按钮可见
    > :not(.collapse-btn) {
      opacity: 0;
      visibility: hidden;
    }
  }

  .collapse-btn {
    position: absolute;
    right: -12px;
    top: 50%;
    transform: translateY(-50%);
    width: 24px;
    height: 48px;
    background: var(--el-color-primary-light-9);
    border-radius: 0 4px 4px 0;
    display: flex;
    align-items: center;
    justify-content: center;
    cursor: pointer;
    z-index: 10;
    transition: background 0.2s;

    &:hover {
      background: var(--el-color-primary-light-7);
    }
  }
}

.chat-main {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
  position: relative;

  .thinking-toggle {
    position: absolute;
    top: 12px;
    right: 12px;
    z-index: 100;
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 6px 12px;
    background: var(--el-bg-color);
    border: 1px solid var(--el-border-color-light);
    border-radius: 20px;
    cursor: pointer;
    transition: all 0.2s;
    box-shadow: 0 2px 8px rgba(0, 0, 0, 0.08);

    &:hover {
      background: var(--el-color-primary-light-9);
      border-color: var(--el-color-primary-light-5);
    }

    .toggle-text {
      font-size: 12px;
      color: var(--el-text-color-regular);
      white-space: nowrap;
    }
  }
}

.thinking-panel {
  position: relative;
  width: 360px;
  min-width: 360px;
  border-left: 1px solid var(--el-border-color-light);
  transition: all 0.3s ease;
  background: var(--el-bg-color-page);

  &.collapsed {
    width: 0;
    min-width: 0;
    overflow: hidden;
  }

  .collapse-btn.left {
    position: absolute;
    left: -12px;
    top: 50%;
    transform: translateY(-50%);
    width: 24px;
    height: 48px;
    background: var(--el-color-primary-light-9);
    border-radius: 4px 0 0 4px;
    display: flex;
    align-items: center;
    justify-content: center;
    cursor: pointer;
    z-index: 10;
    transition: background 0.2s;

    &:hover {
      background: var(--el-color-primary-light-7);
    }
  }
}
</style>
