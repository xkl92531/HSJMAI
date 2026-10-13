<template>
  <div class="chat-panel">
    <!-- 顶部标题栏 -->
    <div class="chat-header">
      <div class="header-left">
        <span class="chat-title">{{ session?.title || $t('agentChat.sessionTitleFallback') }}</span>
        <el-tag :type="connectionStatus === 'Connected' ? 'success' : 'danger'" size="small">
          {{ connectionStatus }}
        </el-tag>
      </div>
      <div class="header-right">
        <el-button text :icon="Setting" @click="showSettings = true" />
      </div>
    </div>

    <!-- 消息列表区域 -->
    <div ref="messageContainerRef" class="message-container">
      <el-scrollbar ref="scrollbarRef" class="message-scrollbar" @scroll="handleScroll">
        <div class="message-list">
          <!-- 欢迎消息 -->
          <div v-if="displayMessages.length === 0" class="welcome-message">
            <div class="welcome-icon">
              <el-icon size="48"><Service /></el-icon>
            </div>
            <h3>{{ $t('agentChat.welcomeTitle') }}</h3>
            <p>{{ $t('agentChat.welcomeIntro') }}</p>
            <ul>
              <li>{{ $t('agentChat.welcomeAnalyzePlc') }}</li>
              <li>{{ $t('agentChat.welcomeDiagnose') }}</li>
              <li>{{ $t('agentChat.welcomeOptimize') }}</li>
              <li>{{ $t('agentChat.welcomeAnswer') }}</li>
            </ul>
          </div>

          <!-- 使用 BubbleList 组件 -->
          <BubbleList
            v-if="displayMessages.length > 0"
            :list="bubbleList"
            :max-width="'100%'"
          >
            <template #avatar="{ item }">
              <el-avatar
                :size="36"
                :style="{
                  background: item.role === 'user' ? 'var(--el-color-primary)' : 'var(--el-color-success)',
                }"
              >
                <el-icon v-if="item.role === 'user'"><User /></el-icon>
                <el-icon v-else><Service /></el-icon>
              </el-avatar>
            </template>

            <template #content="{ item }">
              <div class="message-content" :class="{ error: item.isError }">
                <div v-if="item.role === 'assistant' && item.loading" class="typing-indicator">
                  <span></span><span></span><span></span>
                </div>
                <div v-else v-html="renderMarkdown(item.content)" class="markdown-body" />
              </div>
            </template>

            <template #footer="{ item }">
              <div class="message-footer">
                <span class="message-time">{{ item.time }}</span>
                <div v-if="item.role === 'assistant' && !item.loading" class="message-actions">
                  <el-button text size="small" :icon="CopyDocument" @click="copyMessage(item.content)">
                    {{ $t('agentChat.copy') }}
                  </el-button>
                </div>
              </div>
            </template>
          </BubbleList>
        </div>
      </el-scrollbar>
    </div>

    <!-- 底部输入区域 -->
    <div class="input-container">
      <!-- 附件预览 -->
      <div v-if="attachments.length > 0" class="attachments-preview">
        <div v-for="(file, index) in attachments" :key="index" class="attachment-item">
          <el-icon><Document /></el-icon>
          <span>{{ file.name }}</span>
          <el-icon class="remove-btn" @click="removeAttachment(index)"><Close /></el-icon>
        </div>
      </div>

      <!-- 输入框使用 Sender 组件 -->
      <div class="sender-wrapper">
        <Sender
          v-model="inputText"
          :placeholder="sending ? $t('agentChat.aiThinking') : $t('agentChat.inputHint')"
          :disabled="connectionStatus !== 'Connected'"
          :loading="sending"
          :clearable="true"
          :submit-type="'enter'"
          class="custom-sender"
          @submit="handleSend"
        >
          <template #prefix>
            <el-upload
              :show-file-list="false"
              :before-upload="handleBeforeUpload"
            >
              <el-button text :icon="Paperclip" :title="$t('agentChat.uploadAttachment')" />
            </el-upload>
          </template>
          
          <!-- 将停止按钮放入 action-list 插槽 -->
          <template #action-list>
             <el-tooltip :content="$t('agentChat.stopResponse')" placement="top" v-if="sending">
                <el-button 
                  type="danger" 
                  link 
                  :icon="VideoPause" 
                  @click="handleStop"
                >
                  {{ $t('agentChat.stop') }}
                </el-button>
             </el-tooltip>
          </template>
        </Sender>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, nextTick, watch } from 'vue'
import { BubbleList, Sender } from 'vue-element-plus-x'
import { User, Service, Setting, CopyDocument, Document, Close, Paperclip, VideoPause, ArrowDownBold } from '@element-plus/icons-vue'
import { ElMessage, ElScrollbar } from 'element-plus'
import { marked } from 'marked'
import { useI18n } from 'vue-i18n'
import messages from './lang'

const { t, mergeLocaleMessage } = useI18n()
// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const props = defineProps<{
  session: any
  messages: any[]
  loading: boolean
  sending: boolean
  connectionStatus: string
}>()

const emit = defineEmits<{
  (e: 'send', content: string): void
  (e: 'upload', file: File): void
  (e: 'stop'): void
  (e: 'load-more'): void
}>()

// 输入内容
const inputText = ref('')
const attachments = ref<File[]>([])
const showSettings = ref(false)

// 滚动相关
const scrollbarRef = ref()
const messageContainerRef = ref()

// 本地消息列表（用于流式显示）
const localMessages = ref<any[]>([])
const currentAssistantMessage = ref<any>(null)

// 合并消息显示
const displayMessages = computed(() => {
  return [...props.messages, ...localMessages.value]
})

// 转换为 BubbleList 格式
const bubbleList = computed(() => {
  return displayMessages.value.map((msg, index) => ({
    key: msg.id || `local-${index}`,
    role: msg.role,
    content: msg.content || '',
    time: msg.sentTime ? formatTime(msg.sentTime) : new Date().toLocaleTimeString(),
    loading: msg.loading || false,
    isError: msg.isError || false,
    placement: msg.role === 'user' ? 'end' : 'start',
  }))
})

// 格式化时间
function formatTime(time: string) {
  return new Date(time).toLocaleTimeString('zh-CN', {
    hour: '2-digit',
    minute: '2-digit',
  })
}

// 渲染 Markdown
function renderMarkdown(content: string) {
  if (!content)
    return ''
  try {
    return marked(content)
  }
  catch {
    return content
  }
}

// 复制消息
function copyMessage(content: string) {
  navigator.clipboard.writeText(content)
  ElMessage.success(t('agentChat.copiedToClipboard'))
}

// 发送消息
function handleSend() {
  if (props.sending) return // 防止重复发送

  const content = inputText.value.trim()
  if (!content)
    return

  emit('send', content)
  inputText.value = ''
}

// 停止响应
function handleStop() {
  emit('stop')
}

// 文件上传前处理
function handleBeforeUpload(file: File) {
  if (file.size > 10 * 1024 * 1024) {
    ElMessage.warning(t('agentChat.fileTooLarge'))
    return false
  }
  attachments.value.push(file)
  emit('upload', file)
  return false // 阻止自动上传
}

// 移除附件
function removeAttachment(index: number) {
  attachments.value.splice(index, 1)
}



// 滚动监听
// 滚动到底部按钮显隐
const showScrollBottomBtn = ref(false)
let scrollThrottleTimer: any = null

// 滚动监听 (手动节流)
function handleScroll({ scrollTop }: { scrollTop: number }) {
  if (scrollThrottleTimer) return
  
  scrollThrottleTimer = setTimeout(() => {
    scrollThrottleTimer = null
    
    // 1. 判断是否显示“回到底部”按钮 (距离底部超过300px显示)
    if (scrollbarRef.value && scrollbarRef.value.wrapRef) {
      const wrap = scrollbarRef.value.wrapRef
      // 检查是否发生异常值 (如scrollHeight为0)
      if (wrap.scrollHeight > 0) {
        const distanceToBottom = wrap.scrollHeight - wrap.scrollTop - wrap.clientHeight
        showScrollBottomBtn.value = distanceToBottom > 300
      }
    }

    // 2. 触发加载更多 (距离顶部小于50px，且不在加载中)
    if (scrollTop < 50 && !props.loading) {
      emit('load-more')
    }
  }, 200) // 200ms节流
}

// 滚动到底部
function scrollToBottom(smooth = true) {
  nextTick(() => {
    if (scrollbarRef.value) {
      const wrap = scrollbarRef.value.wrapRef
      if (wrap) {
        wrap.scrollTo({
          top: wrap.scrollHeight,
          behavior: smooth ? 'smooth' : 'auto'
        })
        showScrollBottomBtn.value = false
      }
    }
  })
}

// 暴露给父组件的方法
function addUserMessage(content: string) {
  localMessages.value.push({
    role: 'user',
    content,
    sentTime: new Date().toISOString(),
  })
  scrollToBottom()
}

function addAssistantMessage(content: string) {
  currentAssistantMessage.value = {
    role: 'assistant',
    content,
    loading: true,
    sentTime: new Date().toISOString(),
  }
  localMessages.value.push(currentAssistantMessage.value)
  scrollToBottom()
}

function appendToken(token: string) {
  if (currentAssistantMessage.value) {
    currentAssistantMessage.value.content += token
    scrollToBottom()
  }
}

function finishAssistantMessage(content?: string, isError = false) {
  if (currentAssistantMessage.value) {
    currentAssistantMessage.value.loading = false
    currentAssistantMessage.value.isError = isError
    // 如果有content参数且当前消息为空或只有空白，则使用传入的content
    if (content && (!currentAssistantMessage.value.content || !currentAssistantMessage.value.content.trim())) {
      currentAssistantMessage.value.content = content
    }
    // 如果仍然没有内容，显示默认消息
    if (!currentAssistantMessage.value.content || !currentAssistantMessage.value.content.trim()) {
      currentAssistantMessage.value.content = isError ? t('agentChat.processFailedRetry') : t('agentChat.taskCompleted')
    }
    currentAssistantMessage.value = null
  }
  scrollToBottom()
}

// 监听消息变化，清除本地消息
watch(() => props.messages, () => {
  localMessages.value = []
  currentAssistantMessage.value = null
})

// 暴露方法
defineExpose({
  addUserMessage,
  addAssistantMessage,
  appendToken,
  finishAssistantMessage,
  clearAttachments: () => { attachments.value = [] },
  getScrollParams: () => {
    if (scrollbarRef.value && scrollbarRef.value.wrapRef) {
      return {
        scrollHeight: scrollbarRef.value.wrapRef.scrollHeight,
        scrollTop: scrollbarRef.value.wrapRef.scrollTop
      }
    }
    return { scrollHeight: 0, scrollTop: 0 }
  },
  setScrollTop: (top: number) => {
    if (scrollbarRef.value && scrollbarRef.value.wrapRef) {
      scrollbarRef.value.wrapRef.scrollTop = top
    }
  },
})
</script>

<style lang="scss" scoped>
.chat-panel {
  display: flex;
  flex-direction: column;
  height: 100%;
  background: var(--el-bg-color);
}

.chat-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 16px 20px;
  border-bottom: 1px solid var(--el-border-color-lighter);
  background: var(--el-bg-color);

  .header-left {
    display: flex;
    align-items: center;
    gap: 12px;

    .chat-title {
      font-size: 16px;
      font-weight: 600;
      color: var(--el-text-color-primary);
    }
  }
}

.message-container {
  flex: 1;
  overflow: hidden;
}

.message-scrollbar {
  height: 100%;
}

.message-list {
  padding: 20px;
  min-height: 100%;
}

.welcome-message {
  text-align: center;
  padding: 60px 40px;
  color: var(--el-text-color-secondary);

  .welcome-icon {
    margin-bottom: 20px;
    color: var(--el-color-primary);
  }

  h3 {
    font-size: 20px;
    font-weight: 600;
    color: var(--el-text-color-primary);
    margin-bottom: 12px;
  }

  p {
    margin-bottom: 16px;
  }

  ul {
    list-style: none;
    padding: 0;

    li {
      padding: 8px 0;
      display: flex;
      align-items: center;
      justify-content: center;
      gap: 8px;

      &::before {
        content: '✓';
        color: var(--el-color-success);
      }
    }
  }
}

.message-content {
  line-height: 1.6;

  &.error {
    color: var(--el-color-danger);
  }

  :deep(.markdown-body) {
    font-size: 14px;

    p {
      margin: 0 0 8px;

      &:last-child {
        margin-bottom: 0;
      }
    }

    pre {
      background: var(--el-fill-color-darker);
      padding: 12px;
      border-radius: 6px;
      overflow-x: auto;
    }

    code {
      background: var(--el-fill-color);
      padding: 2px 6px;
      border-radius: 4px;
      font-size: 13px;
    }

    ul, ol {
      padding-left: 20px;
      margin: 8px 0;
    }
  }
}

.typing-indicator {
  display: flex;
  gap: 4px;
  padding: 8px;

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

.message-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-top: 8px;
  font-size: 12px;
  color: var(--el-text-color-secondary);

  .message-actions {
    opacity: 0;
    transition: opacity 0.2s;
  }
}

:deep(.bubble-list-item):hover .message-footer .message-actions {
  opacity: 1;
}

:deep(.bubble-list-item) {
  width: 100%;
  --bubble-content-max-width: 100% !important;
}

:deep(.el-bubble-content) {
  max-width: 100% !important;
}

.input-container {
  padding: 16px 20px;
  border-top: 1px solid var(--el-border-color-lighter);
  background: var(--el-bg-color);
}

.attachments-preview {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-bottom: 12px;

  .attachment-item {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 6px 12px;
    background: var(--el-fill-color-light);
    border-radius: 6px;
    font-size: 13px;

    .remove-btn {
      cursor: pointer;
      color: var(--el-text-color-secondary);

      &:hover {
        color: var(--el-color-danger);
      }
    }
  }
}

.sender-wrapper {
  display: flex;
  flex-direction: column;
  position: relative;
  width: 100%;

  // 强制全宽样式
  :deep(.custom-sender) {
    width: 100%;
    
    .epx-sender-container {
      width: 100%;
      border-radius: 8px;
      
      &:hover {
        border-color: var(--el-color-primary);
      }
    }

    .epx-sender-wrapper {
      width: 100%;
    }

    .el-textarea__inner {
      min-height: 44px !important;
      max-height: 200px;
      width: 100%;
    }
  }
}

.scroll-bottom-btn {
  position: absolute;
  bottom: 80px;
  right: 20px;
  width: 36px;
  height: 36px;
  border-radius: 50%;
  background: var(--el-bg-color-overlay);
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.15);
  display: flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
  z-index: 100;
  color: var(--el-color-primary);
  border: 1px solid var(--el-border-color-lighter);
  transition: all 0.3s;

  &:hover {
    background: var(--el-color-primary-light-9);
    transform: translateY(-2px);
    box-shadow: 0 6px 16px rgba(0, 0, 0, 0.2);
  }
}
</style>
