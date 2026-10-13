<template>
  <div class="thinking-process">
    <!-- 标题 -->
    <div class="panel-header">
      <span class="title">{{ $t('agentChat.executionProcess') }}</span>
      <el-tag v-if="currentStage" :type="getStageType(currentStage)" size="small">
        {{ currentStage }}
      </el-tag>
    </div>

    <el-scrollbar ref="scrollbarRef" class="process-scroll">
      <div class="process-content">
        <!-- 执行日志时间线 -->
        <div v-if="executionLogs.length > 0" class="section">
          <div class="section-title">
            <el-icon><Timer /></el-icon>
            <span>{{ $t('agentChat.executionLogs') }}</span>
          </div>
          <el-timeline>
            <el-timeline-item
              v-for="(log, index) in executionLogs.slice(-20)"
              :key="index"
              :type="log.type || 'primary'"
              :timestamp="log.timestamp"
              placement="top"
            >
              <div class="log-item">
                <div class="log-stage">
                  <el-tag :type="log.type || 'info'" size="small">{{ log.stage }}</el-tag>
                </div>
                <div class="log-message">{{ log.message }}</div>
                <div v-if="log.data" class="log-data">
                  <el-collapse>
                    <el-collapse-item :title="$t('agentChat.detailData')">
                      <pre>{{ formatJson(log.data) }}</pre>
                    </el-collapse-item>
                  </el-collapse>
                </div>
              </div>
            </el-timeline-item>
          </el-timeline>
        </div>

        <!-- 智能体思考 & 反思 -->
        <div v-if="agentThoughts.length > 0" class="section">
          <div class="section-title">
            <el-icon><ChatDotSquare /></el-icon>
            <span>{{ $t('agentChat.agentThinkingSection') }}</span>
          </div>
          <div class="thought-list">
            <div 
              v-for="(thought, index) in agentThoughts.slice(-10)" 
              :key="index" 
              class="thought-item"
              :class="thought.type || 'thought'"
            >
              <div class="thought-header">
                <el-avatar :size="24" class="thought-avatar">
                  <el-icon v-if="thought.type === 'reflection'"><DataAnalysis /></el-icon>
                  <el-icon v-else><Service /></el-icon>
                </el-avatar>
                <span class="thought-title">{{ thought.type === 'reflection' ? $t('agentChat.reflectionLabel') : $t('agentChat.thoughtLabel') }}</span>
                <span class="thought-time">{{ thought.timestamp }}</span>
              </div>
              <div class="thought-content">{{ thought.thought }}</div>
            </div>
          </div>
        </div>

        <!-- 工具调用 -->
        <div v-if="toolCalls.length > 0" class="section">
          <div class="section-title">
            <el-icon><Setting /></el-icon>
            <span>{{ $t('agentChat.toolCallsSection') }}</span>
          </div>
          <div class="tool-list">
            <div
              v-for="(call, index) in toolCalls.slice(-10)"
              :key="index"
              class="tool-item"
              :class="call.status"
            >
              <div class="tool-header">
                <el-icon class="tool-icon">
                  <component :is="getToolIcon(call.status)" />
                </el-icon>
                <span class="tool-name">{{ call.toolName }}</span>
                <el-tag :type="getToolStatusType(call.status)" size="small">
                  {{ getToolStatusText(call.status) }}
                </el-tag>
              </div>
              <div v-if="call.parameters" class="tool-params">
                <span class="label">{{ $t('agentChat.paramsLabel') }}</span>
                <code>{{ formatParams(call.parameters) }}</code>
              </div>
              <div v-if="call.result" class="tool-result">
                <span class="label">{{ $t('agentChat.resultLabel') }}</span>
                <el-collapse>
                  <el-collapse-item :title="$t('agentChat.viewResult')">
                    <pre>{{ formatResult(call.result) }}</pre>
                  </el-collapse-item>
                </el-collapse>
              </div>
              <div v-if="call.durationMs" class="tool-duration">
                {{ $t('agentChat.durationMs', { n: call.durationMs }) }}
              </div>
            </div>
          </div>
        </div>

        <!-- 空状态 -->
        <div v-if="executionLogs.length === 0 && agentThoughts.length === 0 && toolCalls.length === 0" class="empty-state">
          <el-empty :description="$t('agentChat.noExecutionRecords')" :image-size="60" />
        </div>
      </div>
    </el-scrollbar>
  </div>
</template>

<script setup lang="ts">
import { ref, watch, nextTick } from 'vue'
import { Timer, ChatDotSquare, Setting, Service, Loading, CircleCheck, CircleClose, DataAnalysis } from '@element-plus/icons-vue'
import { useI18n } from 'vue-i18n'
import messages from './lang'

const { t, mergeLocaleMessage } = useI18n()
// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const props = defineProps<{
  executionLogs: any[]
  toolCalls: any[]
  agentThoughts: any[]
  currentStage: string
}>()

const scrollbarRef = ref()

// 自动滚动到底部
function scrollToBottom() {
  nextTick(() => {
    if (scrollbarRef.value) {
      const wrap = scrollbarRef.value.wrapRef
      if (wrap) {
        wrap.scrollTo({
          top: wrap.scrollHeight,
          behavior: 'smooth'
        })
      }
    }
  })
}

// 监听数据变化，触发滚动
watch(
  [() => props.executionLogs.length, () => props.agentThoughts.length, () => props.toolCalls.length, () => props.toolCalls.map(t => t.status).join(',')],
  () => {
    scrollToBottom()
  },
  { deep: true }
)

function getStageType(stage: string) {
  if (stage.includes(t('agentChat.stageCompleted')))
    return 'success'
  if (stage.includes(t('agentChat.stageFailed')))
    return 'danger'
  if (stage.includes(t('agentChat.toolCallKeyword')))
    return 'warning'
  return 'info'
}

function getToolIcon(status: string) {
  switch (status) {
    case 'calling':
      return Loading
    case 'success':
      return CircleCheck
    case 'failed':
      return CircleClose
    default:
      return Setting
  }
}

function getToolStatusType(status: string) {
  switch (status) {
    case 'calling':
      return 'info'
    case 'success':
      return 'success'
    case 'failed':
      return 'danger'
    default:
      return 'info'
  }
}

function getToolStatusText(status: string) {
  switch (status) {
    case 'calling':
      return t('agentChat.toolStatusCalling')
    case 'success':
      return t('agentChat.toolStatusSuccess')
    case 'failed':
      return t('agentChat.toolStatusFailed')
    default:
      return status
  }
}

function formatJson(data: any) {
  try {
    return JSON.stringify(data, null, 2)
  }
  catch {
    return String(data)
  }
}

function formatParams(params: any) {
  if (typeof params === 'string')
    return params
  try {
    const str = JSON.stringify(params)
    return str.length > 100 ? `${str.substring(0, 100)}...` : str
  }
  catch {
    return String(params)
  }
}

function formatResult(result: any) {
  if (typeof result === 'string') {
    return result.length > 500 ? `${result.substring(0, 500)}...` : result
  }
  try {
    const str = JSON.stringify(result, null, 2)
    return str.length > 500 ? `${str.substring(0, 500)}...` : str
  }
  catch {
    return String(result)
  }
}
</script>

<style lang="scss" scoped>
.thinking-process {
  display: flex;
  flex-direction: column;
  height: 100%;
}

.panel-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 16px;
  border-bottom: 1px solid var(--el-border-color-lighter);

  .title {
    font-size: 14px;
    font-weight: 600;
    color: var(--el-text-color-primary);
  }
}

.process-scroll {
  flex: 1;
  overflow: hidden;
}

.process-content {
  padding: 16px;
}

.section {
  margin-bottom: 24px;

  &:last-child {
    margin-bottom: 0;
  }
}

.section-title {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 13px;
  font-weight: 600;
  color: var(--el-text-color-regular);
  margin-bottom: 12px;
  padding-bottom: 8px;
  border-bottom: 1px dashed var(--el-border-color-lighter);
}

.log-item {
  .log-stage {
    margin-bottom: 4px;
  }

  .log-message {
    font-size: 13px;
    color: var(--el-text-color-primary);
    line-height: 1.5;
  }

  .log-data {
    margin-top: 8px;

    pre {
      font-size: 12px;
      background: var(--el-fill-color-light);
      padding: 8px;
      border-radius: 4px;
      overflow-x: auto;
      max-height: 200px;
    }
  }
}

.thought-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.thought-item {
  padding: 12px;
  background: var(--el-fill-color-light);
  border-radius: 8px;
  border-left: 3px solid var(--el-color-primary);

  &.reflection {
    border-left-color: var(--el-color-warning);
    background: var(--el-color-warning-light-9);
  }

  .thought-header {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 8px;

    .thought-avatar {
      background: var(--el-color-primary-light-8);
      color: var(--el-color-primary);
    }

    .thought-title {
      font-size: 12px;
      font-weight: 600;
      color: var(--el-text-color-primary);
    }

    .thought-time {
      font-size: 12px;
      color: var(--el-text-color-secondary);
      margin-left: auto;
    }
  }

  &.reflection .thought-header .thought-avatar {
    background: var(--el-color-warning-light-8);
    color: var(--el-color-warning);
  }

  .thought-content {
    font-size: 13px;
    color: var(--el-text-color-primary);
    line-height: 1.6;
    white-space: pre-wrap;
  }
}

.tool-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.tool-item {
  padding: 12px;
  background: var(--el-fill-color-light);
  border-radius: 8px;
  border-left: 3px solid var(--el-color-info);

  &.calling {
    border-left-color: var(--el-color-primary);
    animation: pulse 1.5s infinite;
  }

  &.success {
    border-left-color: var(--el-color-success);
  }

  &.failed {
    border-left-color: var(--el-color-danger);
  }

  .tool-header {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 8px;

    .tool-icon {
      font-size: 16px;
    }

    .tool-name {
      font-weight: 500;
      font-size: 13px;
      color: var(--el-text-color-primary);
    }
  }

  .tool-params,
  .tool-result {
    margin-top: 8px;
    font-size: 12px;

    .label {
      color: var(--el-text-color-secondary);
      margin-right: 4px;
    }

    code {
      background: var(--el-fill-color);
      padding: 2px 6px;
      border-radius: 4px;
      font-size: 11px;
    }

    pre {
      background: var(--el-fill-color);
      padding: 8px;
      border-radius: 4px;
      font-size: 11px;
      overflow-x: auto;
      max-height: 150px;
    }
  }

  .tool-duration {
    margin-top: 8px;
    font-size: 11px;
    color: var(--el-text-color-secondary);
  }
}

@keyframes pulse {
  0%, 100% {
    opacity: 1;
  }
  50% {
    opacity: 0.7;
  }
}

.empty-state {
  display: flex;
  justify-content: center;
  padding-top: 40px;
}
</style>
