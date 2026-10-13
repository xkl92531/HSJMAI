<route lang="yaml">
meta:
  title: AI能量流诊断
  icon: i-carbon:energy
</route>

<script setup lang="ts">
import { Connection, CopyDocument, DArrowLeft, DArrowRight, Refresh, Service, User } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { computed, onMounted, ref } from 'vue'
import { BubbleList, Sender, Thinking, XMarkdown } from 'vue-element-plus-x'
import { useI18n } from 'vue-i18n'
import i18nMessages from './components/lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in i18nMessages) {
  mergeLocaleMessage(locale, i18nMessages[locale])
}

const apiBaseUrl = (import.meta.env.DEV && import.meta.env.VITE_OPEN_PROXY)
  ? '/proxy'
  : (import.meta.env.VITE_APP_API_BASEURL || '')

interface StreamEventData {
  type?: string
  message?: string
  diagnosticId?: number
  result?: string
  data?: {
    content?: string
    step?: string
    [key: string]: any
  }
  [key: string]: any
}

function buildApiUrl(path: string) {
  const normalizedBase = apiBaseUrl.endsWith('/') ? apiBaseUrl.slice(0, -1) : apiBaseUrl
  const normalizedPath = path.startsWith('/') ? path : `/${path}`
  return `${normalizedBase}${normalizedPath}`
}

async function streamDiagnoseEnergyFlow(
  payload: Record<string, any>,
  onEvent: (data: StreamEventData) => void,
) {
  const response = await fetch(buildApiUrl('/DiagnosticStrategyFactory/DiagnoseEnergyFlowStream'), {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'Accept': 'text/event-stream',
    },
    body: JSON.stringify(payload),
  })

  if (!response.ok) {
    throw new Error(`Request failed: ${response.status} ${response.statusText}`)
  }

  if (!response.body) {
    throw new Error('Streaming response is not supported in this browser.')
  }

  const reader = response.body.getReader()
  const decoder = new TextDecoder('utf-8')
  let buffer = ''

  const processEventBlock = (block: string) => {
    const lines = block
      .split(/\r?\n/)
      .filter(line => line.startsWith('data:'))

    if (!lines.length) {
      return
    }

    const payloadText = lines
      .map(line => line.slice(5).trimStart())
      .join('\n')
      .trim()

    if (!payloadText || payloadText === '[DONE]') {
      return
    }

    try {
      onEvent(JSON.parse(payloadText))
    }
    catch {
      onEvent({
        type: 'chunk',
        data: { content: payloadText },
      })
    }
  }

  while (true) {
    const { done, value } = await reader.read()
    buffer += decoder.decode(value || new Uint8Array(), { stream: !done })

    const parts = buffer.split(/\r?\n\r?\n/)
    buffer = parts.pop() || ''
    parts.forEach(processEventBlock)

    if (done) {
      break
    }
  }

  if (buffer.trim()) {
    processEventBlock(buffer)
  }
}

const isConfigCollapsed = ref(false)

const selectedProductionLine = ref<number>(1)

// 聊天相关
interface ChatMessage {
  role: 'user' | 'assistant'
  content: any
  time: string
  loading?: boolean
}

interface ChatRecord {
  id: string
  title: string
  createTime: string
  messages: ChatMessage[]
}

const messages = ref<ChatMessage[]>([])
const chatHistory = ref<ChatRecord[]>([])
const currentChatId = ref<string | null>(null)
const inputText = ref('')
const isLoading = ref(false)

// 当前对话标题
const currentChatTitle = computed(() => {
  if (currentChatId.value) {
    const chat = chatHistory.value.find(c => c.id === currentChatId.value)
    return chat?.title || t('energyFlowChat.newChat')
  }
  return t('energyFlowChat.newChat')
})

// 转换为 BubbleList 格式
const bubbleListData = computed(() => {
  return messages.value.map((msg, index) => ({
    key: `msg-${index}`,
    role: msg.role,
    content: msg.content || '',
    time: formatTime(msg.time),
    loading: msg.loading || false,
    placement: (msg.role === 'user' ? 'end' : 'start') as 'end' | 'start',
  }))
})

// 加载聊天历史
function loadChatHistory() {
  const history = localStorage.getItem('diagnostic_chat_history')
  if (history) {
    try {
      chatHistory.value = JSON.parse(history)
    }
    catch {
      chatHistory.value = []
    }
  }
}

// 保存聊天历史
function saveChatHistory() {
  localStorage.setItem('diagnostic_chat_history', JSON.stringify(chatHistory.value))
}

// 加载指定聊天
function loadChat(chat: ChatRecord) {
  currentChatId.value = chat.id
  // 加载历史消息，确保每个 AI 消息的 loading 为 false
  messages.value = chat.messages.map(msg => ({
    ...msg,
    loading: msg.role === 'assistant' ? false : msg.loading,
    content: msg.role === 'assistant' ? msg.content.replace(/```markdown/g, '').replace('```', '') : msg.content,
  }))
}

// 新建对话
function newChat() {
  currentChatId.value = null
  messages.value = []
  inputText.value = ''
}

// 清空历史
async function clearHistory() {
  try {
    await ElMessageBox.confirm(t('energyFlowChat.clearHistoryConfirm'), t('common.tip'), {
      type: 'warning',
    })
    chatHistory.value = [] 
    localStorage.removeItem('diagnostic_chat_history')
    newChat()
    ElMessage.success(t('energyFlowChat.historyCleared'))
  }
  catch {
    // 用户取消
  }
}

// 发送建议问题
function sendSuggestion(text: string) {
  inputText.value = text
  handleSend()
}

// 发送消息

async function handleSend() {
  if (!inputText.value.trim() || isLoading.value || !selectedProductionLine.value) {
    return
  }

  const userInput = inputText.value.trim()
  inputText.value = ''
  isLoading.value = true

  const userMessage: ChatMessage = {
    role: 'user',
    content: userInput,
    time: new Date().toISOString(),
  }

  messages.value.push(userMessage)

  const aiMessage: ChatMessage = {
    role: 'assistant',
    content: '',
    time: new Date().toISOString(),
    loading: true,
  }
  messages.value.push(aiMessage)

  saveToHistory(userInput)

  try {
    const progressLines: string[] = []
    let streamedContent = ''
    let finalContent = ''

    const syncAiMessage = () => {
      const sections: string[] = []

      if (progressLines.length) {
        sections.push(progressLines.join('\n'))
      }

      if (finalContent) {
        sections.push(finalContent)
      }
      else if (streamedContent) {
        sections.push(streamedContent)
      }

      aiMessage.content = sections.join('\n\n').trim() || 'Processing diagnosis...'

      const lastIndex = messages.value.length - 1
      if (lastIndex >= 0) {
        messages.value[lastIndex].content = aiMessage.content
      }
      messages.value = [...messages.value]
    }

    await streamDiagnoseEnergyFlow({
      productionLineId: selectedProductionLine.value,
      userDescription: userInput,
      forceDeepAnalysis: false,
    }, (data) => {
      if (!data) {
        return
      }

      if (data.type === 'start') {
        progressLines.length = 0
        streamedContent = ''
        finalContent = ''
        progressLines.push('Processing diagnosis...')
      }
      else if (data.type === 'progress') {
        progressLines.push(`- ${data.message}`)
      }
      else if (data.type === 'chunk') {
        const chunkText = data.data?.content || data.message || ''
        if (chunkText) {
          streamedContent += chunkText
        }
      }
      else if (data.type === 'complete') {
        if (data.result) {
          finalContent = data.result
        }
        else if (!finalContent && !streamedContent) {
          progressLines.push('Completed')
        }
      }
      else if (data.type === 'error') {
        throw new Error(data.message || 'Completed')
      }

      syncAiMessage()
    })
  }
  catch (err: any) {
    aiMessage.content = t('energyFlowChat.diagnoseFailed', { message: err.message || t('energyFlowChat.unknownError') })
  }
  finally {
    aiMessage.loading = false
    isLoading.value = false
    messages.value = messages.value.map(msg => ({ ...msg }))
    updateHistory()
  }
}

function saveToHistory(userInput: string) {
  // 如果没有当前会话，则创建新会话
  if (!currentChatId.value) {
    const newChat: ChatRecord = {
      id: `chat_${Date.now()}`,
      title: userInput.substring(0, 20) + (userInput.length > 20 ? '...' : ''),
      createTime: new Date().toISOString(),
      messages: [...messages.value],
    }
    // 使用展开运算符创建新数组以触发响应式更新
    chatHistory.value = [newChat, ...chatHistory.value]
    currentChatId.value = newChat.id
  }
  else {
    // 更新当前会话，替换整个数组以触发响应式更新
    const index = chatHistory.value.findIndex(c => c.id === currentChatId.value)
    if (index !== -1) {
      const updatedHistory = [...chatHistory.value]
      updatedHistory[index] = {
        ...updatedHistory[index],
        messages: [...messages.value],
      }
      chatHistory.value = updatedHistory
    }
  }

  saveChatHistory()
}

// 更新历史记录
function updateHistory() {
  const index = chatHistory.value.findIndex(c => c.id === currentChatId.value)
  if (index !== -1) {
    // 使用展开运算符创建新数组以触发响应式更新
    const updatedHistory = [...chatHistory.value]
    updatedHistory[index] = {
      ...updatedHistory[index],
      messages: [...messages.value],
    }
    chatHistory.value = updatedHistory
    saveChatHistory()
  }
}

// 复制消息
function copyMessage(content: string) {
  navigator.clipboard.writeText(content)
  ElMessage.success(t('energyFlowChat.copiedToClipboard'))
}

// 格式化时间
function formatTime(time: string) {
  const date = new Date(time)
  const now = new Date()
  const isToday = date.toDateString() === now.toDateString()

  if (isToday) {
    return date.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })
  }
  return `${date.toLocaleDateString('zh-CN', { month: 'short', day: 'numeric' })} ${
    date.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })}`
}

onMounted(async () => {
  loadChatHistory()
  // 自动加载最新聊天记录
  if (chatHistory.value.length > 0) {
    const latestChat = chatHistory.value[0]
    loadChat(latestChat)
  }
})
</script>

<template>
  <div class="diagnostic-chat-container">
    <!-- 左侧：会话管理 -->
    <div class="config-panel" :class="{ collapsed: isConfigCollapsed }">
      <div class="panel-header">
        <h3>{{ $t('energyFlowChat.diagnosticHistory') }}</h3>
        <el-button text @click="isConfigCollapsed = !isConfigCollapsed">
          <el-icon><DArrowLeft v-if="!isConfigCollapsed" /><DArrowRight v-else /></el-icon>
        </el-button>
      </div>
      <div v-show="!isConfigCollapsed" class="panel-content">
        <div class="history-list">
          <div
            v-for="item in chatHistory"
            :key="item.id"
            class="history-item"
            :class="{ active: currentChatId === item.id }"
            @click="loadChat(item)"
          >
            <div class="history-title">
              {{ item.title || $t('energyFlowChat.newChat') }}
            </div>
            <div class="history-time">
              {{ formatTime(item.createTime) }}
            </div>
          </div>
          <el-empty v-if="chatHistory.length === 0" :description="$t('energyFlowChat.noHistory')" :image-size="60" />
        </div>
        <div class="panel-footer">
          <el-button type="danger" text @click="clearHistory">
            {{ $t('energyFlowChat.clearHistory') }}
          </el-button>
        </div>
      </div>
    </div>

    <!-- 中间：聊天主区域 -->
    <div class="chat-main">
      <div class="chat-header">
        <div class="header-left">
          <span class="chat-title">{{ currentChatTitle }}</span>
          <el-tag :type="isLoading ? 'warning' : 'success'" size="small">
            {{ isLoading ? $t('energyFlowChat.diagnosing') : $t('energyFlowChat.ready') }}
          </el-tag>
        </div>
        <div class="header-right">
          <el-button text :icon="Refresh" :title="$t('energyFlowChat.newChat')" @click="newChat" />
        </div>
      </div>

      <!-- 消息列表 -->
      <div class="message-container">
        <el-scrollbar>
          <div class="message-list">
            <!-- 欢迎消息 -->
            <div v-if="messages.length === 0" class="welcome-message">
              <div class="welcome-icon">
                <el-icon size="48">
                  <Connection />
                </el-icon>
              </div>
              <h3>{{ $t('energyFlowChat.assistantTitle') }}</h3>
              <p>{{ $t('energyFlowChat.assistantDesc') }}</p>
              <div v-if="selectedProductionLine" class="suggestions">
                <p>{{ $t('energyFlowChat.suggestionTip') }}</p>
                <ul>
                  <li @click="sendSuggestion($t('energyFlowChat.suggestion1'))">
                    "{{ $t('energyFlowChat.suggestion1') }}"
                  </li>
                  <li @click="sendSuggestion($t('energyFlowChat.suggestion2'))">
                    "{{ $t('energyFlowChat.suggestion2') }}"
                  </li>
                  <li @click="sendSuggestion($t('energyFlowChat.suggestion3'))">
                    "{{ $t('energyFlowChat.suggestion3') }}"
                  </li>
                </ul>
              </div>
            </div>

            <!-- 消息列表 -->
            <BubbleList :list="bubbleListData" max-width="'100%'">
              <template #avatar="{ item }">
                <el-avatar :size="36" :style="{ background: item.role === 'user' ? 'var(--el-color-primary)' : 'var(--el-color-success)' }">
                  <el-icon v-if="item.role === 'user'">
                    <User />
                  </el-icon>
                  <el-icon v-else>
                    <Service />
                  </el-icon>
                </el-avatar>
              </template>
              <template #content="{ item }">
                <Thinking v-if="item.loading" />
                <XMarkdown v-else :markdown="item.content" class="vp-raw" />
              </template>
              <template #footer="{ item }">
                <span class="message-time">{{ item.time }}</span>
                <el-button v-if="item.role === 'assistant' && !item.loading" text size="small" :icon="CopyDocument" @click="copyMessage(item.content)">
                  {{ $t('energyFlowChat.copy') }}
                </el-button>
              </template>
            </BubbleList>
          </div>
        </el-scrollbar>
      </div>

      <!-- 输入区域 -->
      <div class="sender-wrapper">
        <Sender
          v-model="inputText"
          :loading="isLoading"
          :disabled="isLoading || !selectedProductionLine"
          :placeholder="$t('energyFlowChat.inputPlaceholder')"
          :clearable="true"
          submit-type="enter"
          class="custom-sender"
          @submit="handleSend"
        />
      </div>
    </div>
  </div>
</template>

<style lang="scss" scoped>
.diagnostic-chat-container {
  display: flex;
  height: calc(100vh - 120px);
  background: linear-gradient(135deg, #f5f7fa 0%, #e4e8ec 100%);
  border-radius: 12px;
  overflow: hidden;
  box-shadow: 0 4px 20px rgba(0, 0, 0, 0.08);
}

.config-panel {
  width: 260px;
  min-width: 260px;
  border-right: 1px solid rgba(0, 0, 0, 0.06);
  background: rgba(255, 255, 255, 0.85);
  backdrop-filter: blur(10px);
  display: flex;
  flex-direction: column;
  transition: all 0.3s ease;

  &.collapsed {
    width: 60px;
    min-width: 60px;
  }

  .panel-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 16px;
    border-bottom: 1px solid rgba(0, 0, 0, 0.06);

    h3 {
      margin: 0;
      font-size: 15px;
      font-weight: 600;
      color: #333;
    }
  }

  .panel-content {
    flex: 1;
    overflow-y: auto;
    padding: 16px;
  }

  .config-item {
    margin-bottom: 20px;

    label {
      display: block;
      margin-bottom: 8px;
      font-size: 14px;
      font-weight: 500;
      color: var(--el-text-color-primary);
    }

    .config-tip {
      font-size: 12px;
      color: var(--el-text-color-secondary);
      margin-top: 4px;
    }
  }

  .history-list {
    flex: 1;
    overflow-y: auto;
    padding: 0 8px;
  }

  .history-item {
    padding: 12px;
    border-radius: 10px;
    cursor: pointer;
    margin-bottom: 6px;
    transition: all 0.2s ease;
    background: transparent;

    &:hover {
      background: rgba(64, 158, 255, 0.08);
    }

    &.active {
      background: linear-gradient(135deg, rgba(64, 158, 255, 0.15) 0%, rgba(103, 194, 58, 0.1) 100%);
      border-left: 3px solid #409eff;
    }

    .history-title {
      font-size: 14px;
      color: #333;
      margin-bottom: 4px;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      font-weight: 500;
    }

    .history-time {
      font-size: 12px;
      color: var(--el-text-color-secondary);
    }
  }

  .panel-footer {
    padding: 16px;
    border-top: 1px solid var(--el-border-color-light);
    text-align: center;
  }
}

.chat-main {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
  background: transparent;
}

.chat-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 16px 24px;
  border-bottom: 1px solid rgba(0, 0, 0, 0.06);
  background: rgba(255, 255, 255, 0.6);
  backdrop-filter: blur(10px);

  .header-left {
    display: flex;
    align-items: center;
    gap: 12px;

    .chat-title {
      font-size: 16px;
      font-weight: 600;
      color: #333;
    }
  }
}

.message-container {
  flex: 1;
  overflow: hidden;
  background: transparent;
}

.message-list {
  padding: 24px;
  min-height: 100%;
  display: flex;
  flex-direction: column;
  gap: 20px;
}

.welcome-message {
  text-align: center;
  padding: 80px 40px;
  color: #666;

  .welcome-icon {
    margin-bottom: 24px;
    width: 80px;
    height: 80px;
    margin-inline: auto;
    background: linear-gradient(135deg, #11998e 0%, #38ef7d 100%);
    border-radius: 24px;
    display: flex;
    align-items: center;
    justify-content: center;
    color: white;
    box-shadow: 0 8px 24px rgba(56, 239, 125, 0.3);
    animation: float 3s ease-in-out infinite;

    .el-icon {
      font-size: 36px;
    }
  }

  h3 {
    font-size: 22px;
    font-weight: 600;
    color: #333;
    margin-bottom: 12px;
  }

  p {
    margin-bottom: 24px;
    font-size: 14px;
  }

  .suggestions {
    text-align: left;
    max-width: 360px;
    margin: 0 auto;

    p {
      font-size: 13px;
      color: #999;
      margin-bottom: 12px;
    }

    ul {
      list-style: none;
      padding: 0;

      li {
        padding: 14px 18px;
        margin-bottom: 10px;
        background: white;
        border: 1px solid rgba(0, 0, 0, 0.08);
        border-radius: 12px;
        cursor: pointer;
        transition: all 0.2s ease;
        font-size: 14px;
        color: #555;

        &:hover {
          background: linear-gradient(135deg, rgba(102, 126, 234, 0.1) 0%, rgba(118, 75, 162, 0.1) 100%);
          border-color: rgba(102, 126, 234, 0.3);
          transform: translateX(4px);
        }
      }
    }
  }

  .no-line-tip {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 8px;
    padding: 16px;
    background: var(--el-color-warning-light-9);
    border-radius: 8px;
    color: var(--el-color-warning);
  }
}

.message-item {
  display: flex;
  gap: 16px;
  align-items: flex-start;
  max-width: 85%;
  animation: fadeIn 0.3s ease;

  &.user-message {
    flex-direction: row-reverse;
    margin-left: auto;

    .message-bubble {
      background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
      color: white;
      border-radius: 18px 18px 4px 18px;
      box-shadow: 0 4px 12px rgba(102, 126, 234, 0.3);
    }

    .message-avatar {
      .el-avatar {
        background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
      }
    }
  }

  &.assistant-message {
    .message-bubble {
      background: white;
      border-radius: 18px 18px 18px 4px;
      box-shadow: 0 2px 12px rgba(0, 0, 0, 0.08);
    }

    .message-avatar {
      .el-avatar {
        background: linear-gradient(135deg, #11998e 0%, #38ef7d 100%);
      }
    }
  }

  .message-avatar {
    flex-shrink: 0;

    .el-avatar {
      width: 40px;
      height: 40px;
      border: 2px solid white;
      box-shadow: 0 2px 8px rgba(0, 0, 0, 0.1);
    }
  }

  .message-content {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }

  .message-bubble {
    padding: 14px 18px;
    line-height: 1.6;
    white-space: pre-wrap;
    font-size: 14px;
  }

  .message-time {
    font-size: 11px;
    color: #999;
    padding: 0 4px;
  }

  .message-footer {
    display: flex;
    align-items: center;
    gap: 12px;
    margin-top: 8px;
  }
}

.typing-indicator {
  display: flex;
  gap: 4px;
  padding: 12px;

  span {
    width: 8px;
    height: 8px;
    background: var(--el-color-primary);
    border-radius: 50%;
    animation: typing 1.4s infinite ease-in-out both;

    &:nth-child(1) { animation-delay: -0.32s; }
    &:nth-child(2) { animation-delay: -0.16s; }
  }
}

@keyframes typing {
  0%, 80%, 100% {
    transform: scale(0);
  }
  40% {
    transform: scale(1);
  }
}

.markdown-body {
  :deep(h1) { font-size: 1.5em; font-weight: 600; margin: 0 0 12px; color: var(--el-text-color-primary); }
  :deep(h2) { font-size: 1.3em; font-weight: 600; margin: 16px 0 10px; color: var(--el-text-color-primary); border-bottom: 1px solid var(--el-border-color); padding-bottom: 6px; }
  :deep(h3) { font-size: 1.1em; font-weight: 600; margin: 12px 0 8px; color: var(--el-text-color-primary); }
  :deep(h4) { font-size: 1em; font-weight: 600; margin: 10px 0 6px; color: var(--el-text-color-primary); }

  :deep(p) {
    margin: 0 0 8px;

    &:last-child {
      margin-bottom: 0;
    }
  }

  :deep(pre) {
    background: var(--el-fill-color-darker);
    padding: 12px;
    border-radius: 6px;
    overflow-x: auto;
  }

  :deep(code) {
    background: var(--el-fill-color);
    padding: 2px 6px;
    border-radius: 4px;
    font-size: 13px;
  }

  :deep(ul), :deep(ol) {
    padding-left: 20px;
    margin: 8px 0;
  }

  :deep(li) {
    margin: 4px 0;
  }

  :deep(strong) {
    font-weight: 600;
    color: var(--el-color-primary);
  }

  :deep(blockquote) {
    border-left: 3px solid var(--el-color-primary);
    padding-left: 12px;
    margin: 12px 0;
    color: var(--el-text-color-secondary);
  }
}

.input-container,
.sender-wrapper {
  display: flex;
  gap: 12px;
  padding: 16px 24px 20px;
  background: rgba(255, 255, 255, 0.9);
  backdrop-filter: blur(10px);
  border-top: 1px solid rgba(0, 0, 0, 0.06);

  .el-textarea {
    flex: 1;
  }
}

.sender-wrapper {
  :deep(.custom-sender) {
    flex: 1;
    border-radius: 24px;
    overflow: hidden;
    box-shadow: 0 2px 12px rgba(0, 0, 0, 0.08);

    .el-input__wrapper {
      border-radius: 24px;
      padding: 8px 20px;
    }
  }
}

@keyframes fadeIn {
  from {
    opacity: 0;
    transform: translateY(10px);
  }
  to {
    opacity: 1;
    transform: translateY(0);
  }
}

@keyframes float {
  0%, 100% {
    transform: translateY(0);
  }
  50% {
    transform: translateY(-10px);
  }
}

// BubbleList 鑷畾涔夋牱寮?
:deep(.el-bubble-list) {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

:deep(.el-bubble) {
  max-width: 85%;
  animation: fadeIn 0.3s ease;

  &.el-bubble-end {
    align-self: flex-end;

    .el-bubble__content {
      background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
      color: white;
      border-radius: 18px 18px 4px 18px;
      box-shadow: 0 4px 12px rgba(102, 126, 234, 0.3);
    }

    .el-avatar {
      background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
    }
  }

  &.el-bubble-start {
    align-self: flex-start;

    .el-bubble__content {
      background: white;
      border-radius: 18px 18px 18px 4px;
      box-shadow: 0 2px 12px rgba(0, 0, 0, 0.08);
    }

    .el-avatar {
      background: linear-gradient(135deg, #11998e 0%, #38ef7d 100%);
    }
  }

  .el-avatar {
    width: 40px;
    height: 40px;
    border: 2px solid white;
    box-shadow: 0 2px 8px rgba(0, 0, 0, 0.1);
  }

  .el-bubble__content {
    padding: 14px 18px;
    line-height: 1.6;
    font-size: 14px;
  }

  .el-bubble__footer {
    padding: 4px 4px 0;
    font-size: 11px;
    color: #999;
  }

  .el-bubble-content-wrapper .el-bubble-content-filled {
    width: 100% !important;
    max-width: 100% !important;
  }
}
</style>



