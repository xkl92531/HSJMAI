<template>
  <div class="session-list">
    <!-- 顶部操作栏 -->
    <div class="session-header">
      <span class="title">{{ $t('agentChat.sessionList') }}</span>
      <div class="actions">
        <el-button type="primary" size="small" :icon="Plus" circle @click="$emit('create')" />
        <el-button size="small" :icon="Refresh" circle :loading="loading" @click="$emit('refresh')" />
      </div>
    </div>

    <!-- 会话列表 -->
    <el-scrollbar ref="scrollbarRef" class="session-scroll" @scroll="handleScroll">
      <div v-if="sessions.length === 0 && !loading" class="empty-state">
        <el-empty :description="$t('agentChat.noSessions')" :image-size="80">
          <el-button type="primary" size="small" @click="$emit('create')">
            {{ $t('agentChat.newChat') }}
          </el-button>
        </el-empty>
      </div>

      <div
        v-for="session in sessions"
        :key="session.id"
        class="session-item"
        :class="{ active: currentSession?.id === session.id }"
        @click="selectSession(session)"
      >
        <div class="session-icon">
          <el-icon size="20">
            <ChatDotRound />
          </el-icon>
        </div>
        <div class="session-content">
          <div class="session-title">{{ session.title || $t('agentChat.sessionTitleFallback') }}</div>
          <div class="session-meta">
            <span class="message-count">{{ $t('agentChat.messageCount', { n: session.messageCount || 0 }) }}</span>
            <span class="session-time">{{ formatTime(session.lastMessageTime || session.createTime) }}</span>
          </div>
        </div>
        <div class="session-actions" @click.stop>
          <el-dropdown trigger="click">
            <el-button size="small" :icon="MoreFilled" text circle />
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item @click="handleRename(session)">
                  <el-icon><Edit /></el-icon>
                  {{ $t('agentChat.rename') }}
                </el-dropdown-item>
                <el-dropdown-item divided @click="handleDelete(session)">
                  <el-icon><Delete /></el-icon>
                  {{ $t('common.delete') }}
                </el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
        </div>
      </div>
    </el-scrollbar>
  </div>
</template>

<script setup lang="ts">
import { Plus, Refresh, MoreFilled, Edit, Delete, ChatDotRound } from '@element-plus/icons-vue'
import { ElMessageBox, ElScrollbar } from 'element-plus'
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import messages from './lang'
import api from '@/api'

const { t, mergeLocaleMessage } = useI18n()
// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

// API 基础路径
const apiUrl = import.meta.env.DEV
  ? import.meta.env.VITE_APP_API_BASEURL
  : (window as any).apiConfig?.apiUrl || ''

const props = defineProps<{
  sessions: any[]
  currentSession: any
  loading: boolean
}>()

const emit = defineEmits<{
  (e: 'update:currentSession', value: any): void
  (e: 'create'): void
  (e: 'delete', sessionId: string): void
  (e: 'refresh'): void
  (e: 'load-more'): void
}>()

// 滚动到底部加载更多
const scrollbarRef = ref<InstanceType<typeof ElScrollbar>>()
let scrollThrottle: any = null

function handleScroll() {
  if (props.loading || scrollThrottle) return

  scrollThrottle = setTimeout(() => {
    scrollThrottle = null
    if (scrollbarRef.value && scrollbarRef.value.wrapRef) {
      const wrap = scrollbarRef.value.wrapRef
      const threshold = 50 // 距离底部50px触发
      if (wrap.scrollHeight - wrap.scrollTop - wrap.clientHeight < threshold) {
        emit('load-more')
      }
    }
  }, 200)
}

function selectSession(session: any) {
  emit('update:currentSession', session)
}

function formatTime(time: string | null) {
  if (!time)
    return ''
  const date = new Date(time)
  const now = new Date()
  const diff = now.getTime() - date.getTime()
  const days = Math.floor(diff / (1000 * 60 * 60 * 24))

  if (days === 0) {
    return date.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })
  }
  else if (days === 1) {
    return t('agentChat.yesterday')
  }
  else if (days < 7) {
    return t('agentChat.daysAgo', { n: days })
  }
  else {
    return date.toLocaleDateString('zh-CN', { month: 'short', day: 'numeric' })
  }
}

async function handleRename(session: any) {
  try {
    const { value } = await ElMessageBox.prompt(t('agentChat.renamePrompt'), t('agentChat.rename'), {
      inputValue: session.title,
      inputPattern: /\S+/,
      inputErrorMessage: t('agentChat.nameRequired'),
    })

    // 调用后端API更新会话标题
    const res: any = await api.post(`${apiUrl}/api/chat/session/${session.id}/rename`, {
      title: value
    })

    if (res.success) {
      // 更新本地会话数据
      session.title = value
      // 刷新会话列表以确保数据同步
      emit('refresh')
    }
  }
  catch (err) {
    // 用户取消或操作失败
    if (err !== 'cancel' && err !== 'closed') {
      console.error('重命名会话失败:', err)
    }
  }
}

async function handleDelete(session: any) {
  try {
    await ElMessageBox.confirm(t('agentChat.deleteSessionConfirm'), t('agentChat.deleteConfirmTitle'), {
      type: 'warning',
    })
    emit('delete', session.id)
  }
  catch {
    // 用户取消
  }
}
</script>

<style lang="scss" scoped>
.session-list {
  display: flex;
  flex-direction: column;
  height: 100%;
}

.session-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 16px;
  border-bottom: 1px solid var(--el-border-color-lighter);

  .title {
    font-size: 16px;
    font-weight: 600;
    color: var(--el-text-color-primary);
  }

  .actions {
    display: flex;
    gap: 8px;
  }
}

.session-scroll {
  flex: 1;
  overflow: hidden;
}

.empty-state {
  display: flex;
  justify-content: center;
  padding-top: 60px;
}

.session-item {
  display: flex;
  align-items: center;
  padding: 12px 16px;
  cursor: pointer;
  transition: all 0.2s;
  border-bottom: 1px solid var(--el-border-color-extra-light);

  &:hover {
    background: var(--el-fill-color-light);

    .session-actions {
      opacity: 1;
    }
  }

  &.active {
    background: var(--el-color-primary-light-9);
    border-left: 3px solid var(--el-color-primary);

    .session-title {
      color: var(--el-color-primary);
    }
  }

  .session-icon {
    width: 40px;
    height: 40px;
    border-radius: 8px;
    background: var(--el-color-primary-light-8);
    display: flex;
    align-items: center;
    justify-content: center;
    margin-right: 12px;
    color: var(--el-color-primary);
  }

  .session-content {
    flex: 1;
    min-width: 0;

    .session-title {
      font-size: 14px;
      font-weight: 500;
      color: var(--el-text-color-primary);
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      margin-bottom: 4px;
    }

    .session-meta {
      display: flex;
      align-items: center;
      gap: 8px;
      font-size: 12px;
      color: var(--el-text-color-secondary);

      .message-count {
        padding: 2px 6px;
        background: var(--el-fill-color);
        border-radius: 4px;
      }
    }
  }

  .session-actions {
    opacity: 0;
    transition: opacity 0.2s;
  }
}
</style>
