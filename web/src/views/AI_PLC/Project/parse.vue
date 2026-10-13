<route lang="yaml">
meta:
  title: 解析元数据
</route>

<script setup lang="ts">
import { ElMessage, type UploadFile } from 'element-plus'
import * as signalR from '@microsoft/signalr'
import api from '@/api/modules/AI_PLC/project'
import { useI18n } from 'vue-i18n'
import messages from './parse.lang'

defineOptions({
  name: 'ProjectParse',
})

const router = useRouter()
const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const loading = ref(false)
const fileList = ref<UploadFile[]>([])
const formData = ref({
  projectName: '',
  productionLineCode: '',
})

// SignalR 连接状态
const connectionStatus = ref<'Disconnected' | 'Connecting' | 'Connected' | 'Reconnecting' | 'Error'>('Disconnected')
let connection: signalR.HubConnection | null = null

// 解析状态
const parseStatus = ref({
  isRunning: false,
  progress: 0,
  currentFile: '',
  totalFiles: 0,
  successCount: 0,
  failCount: 0,
  logs: [] as { time: string; message: string; type: 'info' | 'success' | 'error' }[],
})

// 产线选项（可以从API获取）
const productionLineOptions = ref([
  { value: 'LINE_01', label: t('aiPlcProject_parse.line01') },
  { value: 'LINE_02', label: t('aiPlcProject_parse.line02') },
  { value: 'LINE_03', label: t('aiPlcProject_parse.line03') },
])

function handleChange(file: UploadFile, uploadFiles: UploadFile[]) {
  fileList.value = uploadFiles
}

function handleRemove(file: UploadFile, uploadFiles: UploadFile[]) {
  fileList.value = uploadFiles
}

function addLog(message: string, type: 'info' | 'success' | 'error' = 'info') {
  const now = new Date()
  parseStatus.value.logs.unshift({
    time: now.toLocaleTimeString(),
    message,
    type,
  })
}

async function onSubmit() {
  if (!formData.value.productionLineCode) {
    ElMessage.warning(t('aiPlcProject_parse.selectLine'))
    return
  }

  if (fileList.value.length === 0) {
    ElMessage.warning(t('aiPlcProject_parse.selectFile'))
    return
  }

  // 收集所有有效文件
  const files: File[] = []
  for (const item of fileList.value) {
    if (item.raw) {
      files.push(item.raw)
    }
  }

  if (files.length === 0) {
    ElMessage.warning(t('aiPlcProject_parse.invalidFile'))
    return
  }

  // 重置状态
  parseStatus.value = {
    isRunning: true,
    progress: 0,
    currentFile: '',
    totalFiles: files.length,
    successCount: 0,
    failCount: 0,
    logs: [],
  }

  addLog(t('aiPlcProject_parse.startParse', { count: files.length }))
  loading.value = true

  try {
    const res: any = await api.parseMetadata(
      0,
      files,
      formData.value.projectName,
      formData.value.productionLineCode
    )

    if (res.isSuccess) {
      parseStatus.value.progress = 0
      //parseStatus.value.successCount = res.backResult?.successCount || 0
      //parseStatus.value.failCount = res.backResult?.failCount || 0
      parseStatus.value.totalFiles = res.backResult?.totalFiles || files.length

      // 加入 SignalR 分组以接收解析进度
      const blockIds = res.backResult?.blockIds || []
      if (blockIds.length > 0 && connection) {
        for (const blockId of blockIds) {
          try {
            await connection.invoke('JoinWorkflowGroup', blockId)
            console.log('[Parse] 成功加入分组:', `workflow-${blockId}`)
          } catch (err) {
            console.error('[Parse] 加入分组失败:', err)
          }
        }
        addLog(t('aiPlcProject_parse.subscribedTasks', { count: blockIds.length }), 'info')
      }

      // addLog(`解析完成！共 ${parseStatus.value.totalFiles} 个文件`, 'success')
      // addLog(`成功: ${parseStatus.value.successCount} 个`, 'success')
      if (parseStatus.value.failCount > 0) {
        addLog(t('aiPlcProject_parse.failedCount', { count: parseStatus.value.failCount }), 'error')
      }

      ElMessage.success(t('aiPlcProject_parse.parseComplete'))
    } else {
      parseStatus.value.failCount = files.length
      addLog(res.errorMessage || t('aiPlcProject_parse.parseFailed'), 'error')
      ElMessage.error(res.errorMessage || t('aiPlcProject_parse.parseFailed'))
    }
  } catch (error: any) {
    parseStatus.value.failCount = files.length
    addLog(error.message || t('aiPlcProject_parse.parseFailed'), 'error')
    ElMessage.error(error.message || t('aiPlcProject_parse.parseFailed'))
  } finally {
    // loading.value = false
    // parseStatus.value.isRunning = false
  }
}

function onReset() {
  fileList.value = []
  formData.value = {
    projectName: '',
    productionLineCode: '',
  }
  parseStatus.value = {
    isRunning: false,
    progress: 0,
    currentFile: '',
    totalFiles: 0,
    successCount: 0,
    failCount: 0,
    logs: [],
  }
}

// ========== SignalR 连接 ==========
async function connectSignalR() {
  try {
    connectionStatus.value = 'Connecting'

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
    connection.onreconnecting((error) => {
      console.warn('[Parse] SignalR 连接断开，正在重连...', error)
      connectionStatus.value = 'Reconnecting'
    })

    connection.onreconnected((connectionId) => {
      console.log('[Parse] SignalR 重连成功，ConnectionId:', connectionId)
      connectionStatus.value = 'Connected'
    })

    connection.onclose((error) => {
      console.error('[Parse] SignalR 连接关闭', error)
      connectionStatus.value = 'Disconnected'
    })

    // 注册事件处理器
    registerSignalRHandlers()

    // 启动连接
    await connection.start()
    connectionStatus.value = 'Connected'
    console.log('[Parse] SignalR 连接成功, ConnectionId:', connection.connectionId)
  } catch (error) {
    console.error('[Parse] SignalR 连接失败:', error)
    connectionStatus.value = 'Error'
  }
}

function disconnectSignalR() {
  if (connection) {
    connection.stop()
    connection = null
    connectionStatus.value = 'Disconnected'
  }
}

function registerSignalRHandlers() {
  if (!connection) return

  // 监听解析进度
  connection.on('ParseProgress', (data: any) => {
    debugger;
    if(data.status == 1){
      parseStatus.value.successCount++;
       addLog(t('aiPlcProject_parse.fileParseSuccess', { name: data.blockName }), 'success')
    }
    else if(data.status == 3){
      parseStatus.value.failCount++;
      addLog(t('aiPlcProject_parse.fileParseFailed', { name: data.blockName }), 'error')
    }
    parseStatus.value.progress = parseFloat(((parseStatus.value.successCount + parseStatus.value.failCount) / parseStatus.value.totalFiles * 100).toFixed(2))
    if(parseStatus.value.progress == 100){
      parseStatus.value.isRunning = false
      loading.value = false
    }
    
    console.log('[Parse] ParseProgress', data)
    // // 根据后端返回的 BlockEntity 数据更新进度
    // if (data.blockName) {
    //   addLog(`正在处理: ${data.blockName}`, 'info')
    // }
    // if (data.status !== undefined) {
    //   const statusText = getTaskStatusText(data.status)
    //   addLog(`Block 状态: ${statusText}`, 'info')
    // }
  })
}

function getTaskStatusText(status: number): string {
  const statusMap: Record<number, string> = {
    0: t('aiPlcProject_parse.taskPending'),
    1: t('aiPlcProject_parse.taskReady'),
    2: t('aiPlcProject_parse.taskRunning'),
    3: t('aiPlcProject_parse.taskDone'),
    4: t('aiPlcProject_parse.taskFailed'),
    5: t('aiPlcProject_parse.taskBlocked'),
    6: t('aiPlcProject_parse.taskConfirm'),
  }
  return statusMap[status] || t('aiPlcProject_parse.unknown')
}

// 生命周期
onMounted(() => {
  connectSignalR()
})

onUnmounted(() => {
  disconnectSignalR()
})

function goBack() {
  router.push('/ai_plc/project')
}
</script>

<template>
  <div class="parse-page">
    <FaPageMain>
      <div class="page-header">
        <ElButton @click="goBack">
          <FaIcon name="i-ep:arrow-left" /> {{ $t('aiPlcProject_parse.backToList') }}
        </ElButton>
        <h2 class="page-title">{{ $t('aiPlcProject_parse.pageTitle') }}</h2>
        <ElTag
          :type="connectionStatus === 'Connected' ? 'success' : connectionStatus === 'Error' ? 'danger' : 'warning'"
          size="small"
        >
          <FaIcon
            :name="connectionStatus === 'Connected' ? 'i-ep:circle-check' : connectionStatus === 'Error' ? 'i-ep:circle-close' : 'i-ep:loading'"
            :class="{ 'animate-spin': connectionStatus === 'Connecting' || connectionStatus === 'Reconnecting' }"
          />
          {{ connectionStatus === 'Connected' ? $t('aiPlcProject_parse.connected') : connectionStatus === 'Connecting' ? $t('aiPlcProject_parse.connecting') : connectionStatus === 'Reconnecting' ? $t('aiPlcProject_parse.reconnecting') : connectionStatus === 'Error' ? $t('aiPlcProject_parse.connectFailed') : $t('aiPlcProject_parse.disconnected') }}
        </ElTag>
      </div>

      <div class="parse-container">
        <!-- 左侧：上传表单 -->
        <div class="left-panel">
          <ElCard shadow="never">
            <template #header>
              <span class="card-title">{{ $t('aiPlcProject_parse.uploadConfig') }}</span>
            </template>
            <ElForm :model="formData" label-width="100px">
              <ElFormItem :label="$t('aiPlcProject_parse.projectName')">
                <ElInput v-model="formData.projectName" :placeholder="$t('aiPlcProject_parse.projectNameOptional')" />
              </ElFormItem>
              <ElFormItem :label="$t('aiPlcProject_parse.productionLine')" required>
                <ElSelect v-model="formData.productionLineCode" :placeholder="$t('aiPlcProject_parse.selectLine')" style="width: 100%">
                  <ElOption
                    v-for="item in productionLineOptions"
                    :key="item.value"
                    :label="item.label"
                    :value="item.value"
                  />
                </ElSelect>
              </ElFormItem>
              <ElFormItem :label="$t('aiPlcProject_parse.uploadFile')">
                <ElUpload
                  drag
                  multiple
                  :auto-upload="false"
                  :file-list="fileList"
                  accept=".xml,.ml,.zip"
                  :on-change="handleChange"
                  :on-remove="handleRemove"
                  :disabled="parseStatus.isRunning"
                  style="width: 100%"
                >
                  <div class="upload-content">
                    <FaIcon name="i-ep:upload-filled" class="text-4xl mb-2 text-gray-400" />
                    <div class="text-gray-600">{{ $t('aiPlcProject_parse.uploadDragTip') }}</div>
                    <div class="upload-tip">{{ $t('aiPlcProject_parse.uploadFormatTip') }}</div>
                  </div>
                </ElUpload>
              </ElFormItem>
              <ElFormItem>
                <ElButton type="primary" :loading="loading" :disabled="parseStatus.isRunning" @click="onSubmit">
                  <FaIcon name="i-ep:video-play" /> {{ $t('aiPlcProject_parse.startParseBtn') }}
                </ElButton>
                <ElButton :disabled="parseStatus.isRunning" @click="onReset">
                  <FaIcon name="i-ep:refresh" /> {{ $t('common.reset') }}
                </ElButton>
              </ElFormItem>
            </ElForm>
          </ElCard>
        </div>

        <!-- 右侧：进度和状态 -->
        <div class="right-panel">
          <ElCard shadow="never" class="status-card">
            <template #header>
              <span class="card-title">{{ $t('aiPlcProject_parse.parseProgress') }}</span>
            </template>
            <div class="progress-section">
              <ElProgress
                :percentage="parseStatus.progress"
                :status=" parseStatus.progress === 100 ? 'success' : undefined"
                :stroke-width="20"
                :text-inside="true"
              />
              <div class="status-info">
                <div class="stat-card total">
                  <div class="stat-icon">
                    <FaIcon name="i-ep:document" />
                  </div>
                  <div class="stat-content">
                    <span class="stat-value">{{ parseStatus.totalFiles }}</span>
                    <span class="stat-label">{{ $t('aiPlcProject_parse.totalFiles') }}</span>
                  </div>
                </div>
                <div class="stat-card success">
                  <div class="stat-icon">
                    <FaIcon name="i-ep:circle-check" />
                  </div>
                  <div class="stat-content">
                    <span class="stat-value">{{ parseStatus.successCount }}</span>
                    <span class="stat-label">{{ $t('aiPlcProject_parse.success') }}</span>
                  </div>
                </div>
                <div class="stat-card error">
                  <div class="stat-icon">
                    <FaIcon name="i-ep:circle-close" />
                  </div>
                  <div class="stat-content">
                    <span class="stat-value">{{ parseStatus.failCount }}</span>
                    <span class="stat-label">{{ $t('aiPlcProject_parse.failed') }}</span>
                  </div>
                </div>
                <div class="stat-card status">
                  <div class="stat-icon">
                    <FaIcon :name="parseStatus.isRunning ? 'i-ep:loading' : parseStatus.progress === 100 ? 'i-ep:success-filled' : 'i-ep:clock'" :class="{ 'animate-spin': parseStatus.isRunning }" />
                  </div>
                  <div class="stat-content">
                    <div class="status-tag-wrapper">
                      <ElTag v-if="parseStatus.isRunning" type="warning" effect="dark" size="small">{{ $t('aiPlcProject_parse.parsing') }}</ElTag>
                      <ElTag v-else-if="parseStatus.progress >= 100 && parseStatus.failCount === 0" type="success" effect="dark" size="small">{{ $t('aiPlcProject_parse.done') }}</ElTag>
                      <ElTag v-else-if="parseStatus.progress >= 100 && parseStatus.failCount > 0" type="danger" effect="dark" size="small">{{ $t('aiPlcProject_parse.partialFailed') }}</ElTag>
                      <ElTag v-else type="info" size="small">{{ $t('aiPlcProject_parse.pendingStart') }}</ElTag>
                    </div>
                    <span class="stat-label">{{ $t('aiPlcProject_parse.status') }}</span>
                  </div>
                </div>
              </div>
            </div>
          </ElCard>

          <ElCard shadow="never" class="log-card">
            <template #header>
              <span class="card-title">{{ $t('aiPlcProject_parse.execLog') }}</span>
            </template>
            <div class="log-list">
              <div v-if="parseStatus.logs.length === 0" class="empty-log">
                {{ $t('aiPlcProject_parse.noLogs') }}
              </div>
              <div
                v-for="(log, index) in parseStatus.logs"
                :key="index"
                class="log-item"
                :class="log.type"
              >
                <span class="log-time">[{{ log.time }}]</span>
                <span class="log-message">{{ log.message }}</span>
              </div>
            </div>
          </ElCard>
        </div>
      </div>
    </FaPageMain>
  </div>
</template>

<style scoped>
.parse-page {
  height: 100%;
}

.page-header {
  display: flex;
  align-items: center;
  gap: 16px;
  margin-bottom: 20px;
}

.page-title {
  margin: 0;
  font-size: 18px;
  font-weight: 600;
}

.parse-container {
  display: flex;
  gap: 20px;
  min-height: calc(100vh - 280px);
  align-items: stretch;
  padding-bottom: 40px;
}

.left-panel {
  flex: 1;
  min-width: 400px;
  display: flex;
  flex-direction: column;
}

.left-panel :deep(.el-card) {
  flex: 1;
  display: flex;
  flex-direction: column;
}

.left-panel :deep(.el-card__body) {
  flex: 1;
  display: flex;
  flex-direction: column;
}

.right-panel {
  flex: 1;
  min-width: 400px;
  display: flex;
  flex-direction: column;
  gap: 20px;
}

.card-title {
  font-weight: 600;
  font-size: 16px;
}

.upload-content {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 40px 20px;
}

.upload-tip {
  margin-top: 8px;
  color: #909399;
  font-size: 12px;
}

.status-card {
  flex-shrink: 0;
}

.progress-section {
  padding: 10px 0;
}

.status-info {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 12px;
  margin-top: 20px;
}

.stat-card {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 16px;
  border-radius: 12px;
  background: linear-gradient(135deg, #f5f7fa 0%, #e4e7ed 100%);
  transition: all 0.3s ease;
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.05);
}

.stat-card:hover {
  transform: translateY(-2px);
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.1);
}

.stat-card.total {
  background: linear-gradient(135deg, #e3f2fd 0%, #bbdefb 100%);
}

.stat-card.total .stat-icon {
  background: linear-gradient(135deg, #2196f3 0%, #1976d2 100%);
}

.stat-card.success {
  background: linear-gradient(135deg, #e8f5e9 0%, #c8e6c9 100%);
}

.stat-card.success .stat-icon {
  background: linear-gradient(135deg, #4caf50 0%, #388e3c 100%);
}

.stat-card.error {
  background: linear-gradient(135deg, #ffebee 0%, #ffcdd2 100%);
}

.stat-card.error .stat-icon {
  background: linear-gradient(135deg, #f44336 0%, #d32f2f 100%);
}

.stat-card.status {
  background: linear-gradient(135deg, #fff3e0 0%, #ffe0b2 100%);
}

.stat-card.status .stat-content {
  min-width: 60px;
}

.stat-card.status .stat-icon {
  background: linear-gradient(135deg, #ff9800 0%, #f57c00 100%);
}

.status-tag-wrapper {
  height: 24px;
  display: flex;
  align-items: center;
  overflow: hidden;
}

.stat-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 40px;
  height: 40px;
  border-radius: 10px;
  color: white;
  font-size: 20px;
  flex-shrink: 0;
}

.stat-content {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}

.stat-value {
  font-size: 24px;
  font-weight: 700;
  line-height: 1;
  color: #303133;
}

.stat-label {
  font-size: 12px;
  color: #909399;
  white-space: nowrap;
}

.animate-spin {
  animation: spin 1s linear infinite;
}

@keyframes spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}

.log-card {
  flex: 1;
  display: flex;
  flex-direction: column;
}

.log-card :deep(.el-card__body) {
  flex: 1;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}

.log-list {
  flex: 1;
  overflow-y: auto;
  max-height: 300px;
  padding: 10px;
  background: #f5f7fa;
  border-radius: 4px;
  font-family: 'Consolas', 'Monaco', monospace;
  font-size: 13px;
}

.empty-log {
  color: #909399;
  text-align: center;
  padding: 40px;
}

.log-item {
  padding: 4px 0;
  border-bottom: 1px solid #e4e7ed;
}

.log-item:last-child {
  border-bottom: none;
}

.log-time {
  color: #909399;
  margin-right: 8px;
}

.log-item.success .log-message {
  color: #67c23a;
}

.log-item.error .log-message {
  color: #f56c6c;
}

.log-item.info .log-message {
  color: #606266;
}
</style>
