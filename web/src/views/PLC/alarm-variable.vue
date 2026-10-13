<route lang="yaml">
meta:
  title: 报警变量管理
  cache: true
</route>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { useRoute } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import ProjectAPI from '@/api/modules/PLC/project'
import UploadAPI from '@/api/modules/PLC/upload'
import alarmVariable from '@/api/modules/PLC/alarmVariable'
import faultEvent from '@/api/modules/PLC/faultEvent'
import messages from './alarm-variable.lang'

const route = useRoute()
const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

// ==================== 筛选 ====================
const activeTab = ref('ledger')
const loading = ref(false)
const projectOptions = ref<any[]>([])

const filters = ref({
  projectId: (route.query.projectId as string) || '',
  keyword: '',
  category: '',
  level: '',
  monitorEnabled: '' as '' | 'true' | 'false',
  manuallyConfirmed: '' as '' | 'true' | 'false',
  collectable: '' as '' | 'true' | 'false',
})

const categoryOptions = computed(() => [
  { label: t('alarm_variable.categoryEquipmentFault'), value: '设备故障' },
  { label: t('alarm_variable.categorySafety'), value: '安全' },
  { label: t('alarm_variable.categoryQuality'), value: '质量' },
  { label: t('alarm_variable.categoryOther'), value: '其它' },
])
const levelOptions = [
  { label: 'Info', value: 'Info' },
  { label: 'Warn', value: 'Warn' },
  { label: 'Critical', value: 'Critical' },
]

// ==================== 台账表格 ====================
const tableData = ref<any[]>([])
const pagination = ref({ page: 1, limit: 20, total: 0 })
const selection = ref<any[]>([])

const getList = async () => {
  loading.value = true
  try {
    const params: any = {
      page: pagination.value.page,
      limit: pagination.value.limit,
    }
    if (filters.value.projectId) params.projectId = filters.value.projectId
    if (filters.value.keyword) params.keyword = filters.value.keyword
    if (filters.value.category) params.category = filters.value.category
    if (filters.value.level) params.level = filters.value.level
    if (filters.value.monitorEnabled !== '') params.monitorEnabled = filters.value.monitorEnabled === 'true'
    if (filters.value.manuallyConfirmed !== '') params.manuallyConfirmed = filters.value.manuallyConfirmed === 'true'
    if (filters.value.collectable !== '') params.collectable = filters.value.collectable === 'true'

    const res = await alarmVariable.list(params) as any
    if (res) {
      tableData.value = res.data || []
      pagination.value.total = res.total || 0
    }
  }
  catch (error) {
    console.error(error)
  }
  finally {
    loading.value = false
  }
}

const loadProjects = async () => {
  try {
    const res = await ProjectAPI.list({ page: 1, limit: 200 }) as any
    projectOptions.value = res?.data || []
    // 无 URL 参数时默认选第一个项目
    if (!filters.value.projectId && projectOptions.value.length > 0) {
      filters.value.projectId = String(projectOptions.value[0].id)
    }
  }
  catch (error) {
    console.error(error)
  }
}

const handleSearch = () => {
  pagination.value.page = 1
  getList()
}

const onPageChange = (val: number) => {
  pagination.value.page = val
  getList()
}

const onSizeChange = (val: number) => {
  pagination.value.limit = val
  pagination.value.page = 1
  getList()
}

const handleSelectionChange = (rows: any[]) => {
  selection.value = rows
}

// ==================== 行内开关 ====================
const handleMonitorSwitch = async (row: any, value: boolean) => {
  try {
    const res = await alarmVariable.setMonitor([row.id], value) as any
    if (res?.isSuccess) {
      row.monitorEnabled = value
      ElMessage.success(value ? t('alarm_variable.monitorOn') : t('alarm_variable.monitorOff'))
    }
    else {
      row.monitorEnabled = !value
      ElMessage.warning(res?.errorMessage || t('alarm_variable.operationFailed'))
    }
  }
  catch (error) {
    row.monitorEnabled = !value
    console.error(error)
  }
}

const handlePushSwitch = async (row: any, value: boolean) => {
  try {
    const res = await alarmVariable.setPush([row.id], value) as any
    if (res?.isSuccess) {
      row.pushEnabled = value
    }
    else {
      row.pushEnabled = !value
      ElMessage.warning(res?.errorMessage || t('alarm_variable.operationFailed'))
    }
  }
  catch (error) {
    row.pushEnabled = !value
    console.error(error)
  }
}

const handleConfirm = async (row: any) => {
  const target = !row.manuallyConfirmed
  const res = await alarmVariable.confirm(row.id, target) as any
  if (res?.isSuccess) {
    row.manuallyConfirmed = target
    ElMessage.success(target ? t('alarm_variable.confirmedLocked') : t('alarm_variable.confirmUnlocked'))
  }
}

// ==================== 批量操作 ====================
const batchIds = computed(() => selection.value.map(r => r.id))

const handleBatchMonitor = async (enabled: boolean) => {
  if (batchIds.value.length === 0) {
    ElMessage.warning(t('alarm_variable.pleaseSelectRows'))
    return
  }
  const res = await alarmVariable.setMonitor(batchIds.value, enabled) as any
  if (res?.isSuccess) {
    ElMessage.success(enabled ? t('alarm_variable.batchMonitorOnSuccess') : t('alarm_variable.batchMonitorOffSuccess'))
    getList()
  }
  else {
    ElMessage.warning(res?.errorMessage || t('alarm_variable.batchOperationFailed'))
  }
}

const handleBatchDelete = async () => {
  if (batchIds.value.length === 0) {
    ElMessage.warning(t('alarm_variable.pleaseSelectRows'))
    return
  }
  await ElMessageBox.confirm(t('alarm_variable.batchDeleteConfirm', { count: batchIds.value.length }), t('alarm_variable.warningTitle'), { type: 'warning' })
  const res = await alarmVariable.removeBatch(batchIds.value) as any
  if (res?.isSuccess !== false) {
    ElMessage.success(t('common.deleteSuccess'))
    getList()
  }
}

// ==================== 识别进度 ====================
const recognitionVisible = ref(false)
const recognitionRunning = ref(false)
const recognitionProgress = ref<any>(null)
const recognitionTimer = ref<number | null>(null)

const statusText = computed(() => {
  const s = recognitionProgress.value?.status
  const map: Record<number, string> = { 0: t('alarm_variable.statusWaiting'), 1: t('alarm_variable.statusAnalyzing'), 2: t('alarm_variable.statusSaving'), 3: t('alarm_variable.statusCompleted'), 4: t('alarm_variable.statusFailed') }
  return map[s] || ''
})

const stopRecognitionPolling = () => {
  if (recognitionTimer.value) {
    clearInterval(recognitionTimer.value)
    recognitionTimer.value = null
  }
}

const pollRecognition = async () => {
  const taskId = recognitionProgress.value?.taskId
  if (!taskId) return
  try {
    const res = await UploadAPI.getProgress(taskId) as any
    if (res?.isSuccess && res.backResult) {
      recognitionProgress.value = res.backResult
      if ([3, 4].includes(res.backResult.status)) {
        stopRecognitionPolling()
        recognitionRunning.value = false
        if (res.backResult.status === 3) {
          ElMessage.success(t('alarm_variable.recognitionDone'))
          getList()
        }
        else {
          ElMessage.error(res.backResult.errorDetails || t('alarm_variable.recognitionFailed'))
        }
      }
    }
  }
  catch (error) {
    console.error(error)
  }
}

const handleRerunRecognition = async () => {
  if (!filters.value.projectId) {
    ElMessage.warning(t('alarm_variable.pleaseSelectProject'))
    return
  }
  try {
    const res = await alarmVariable.rerunRecognition(filters.value.projectId) as any
    if (res?.isSuccess && res.backResult) {
      recognitionVisible.value = true
      recognitionRunning.value = true
      recognitionProgress.value = { taskId: res.backResult.taskId, status: 0 }
      stopRecognitionPolling()
      recognitionTimer.value = window.setInterval(pollRecognition, 1500)
      pollRecognition()
    }
    else {
      ElMessage.warning(res?.errorMessage || t('alarm_variable.startRecognitionFailed'))
    }
  }
  catch (error) {
    console.error(error)
  }
}

// ==================== 新增/编辑 ====================
const editVisible = ref(false)
const editForm = ref<any>({})
const editType = ref<'create' | 'edit'>('create')

const handleCreate = () => {
  editType.value = 'create'
  editForm.value = {
    projectId: filters.value.projectId,
    variableName: '',
    stationCode: '',
    alarmCategory: '设备故障',
    alarmLevel: 'Warn',
    description: '',
    remark: '',
  }
  editVisible.value = true
}

const handleEdit = (row: any) => {
  editType.value = 'edit'
  editForm.value = {
    id: row.id,
    stationCode: row.stationCode,
    alarmCategory: row.alarmCategory,
    alarmLevel: row.alarmLevel,
    description: row.description,
    remark: row.remark,
  }
  editVisible.value = true
}

const submitEdit = async () => {
  if (editType.value === 'create') {
    if (!editForm.value.variableName) {
      ElMessage.warning(t('alarm_variable.variableNameRequired'))
      return
    }
    const res = await alarmVariable.insert(editForm.value) as any
    if (res?.isSuccess) {
      ElMessage.success(t('alarm_variable.createSuccessLocked'))
      if (res.errorMessage) ElMessage.info(res.errorMessage)
      editVisible.value = false
      getList()
    }
    else {
      ElMessage.warning(res?.errorMessage || t('alarm_variable.createFailed'))
    }
  }
  else {
    const res = await alarmVariable.update(editForm.value) as any
    if (res?.isSuccess) {
      ElMessage.success(t('alarm_variable.saveSuccessLocked'))
      editVisible.value = false
      getList()
    }
    else {
      ElMessage.warning(res?.errorMessage || t('alarm_variable.saveFailed'))
    }
  }
}

const handleDelete = async (row: any) => {
  await ElMessageBox.confirm(t('alarm_variable.deleteConfirm', { name: row.variableName }), t('alarm_variable.warningTitle'), { type: 'warning' })
  const res = await alarmVariable.remove(row.id) as any
  if (res?.isSuccess !== false) {
    ElMessage.success(t('common.deleteSuccess'))
    getList()
  }
}

// ==================== 证据抽屉 ====================
const evidenceVisible = ref(false)
const evidenceRow = ref<any>(null)
const evidenceLines = computed((): string[] => {
  if (!evidenceRow.value?.evidenceJson) return []
  try {
    const parsed = JSON.parse(evidenceRow.value.evidenceJson)
    return (parsed.evidences || []).map((e: any) => String(e))
  }
  catch {
    return [String(evidenceRow.value.evidenceJson)]
  }
})

const showEvidence = (row: any) => {
  evidenceRow.value = row
  evidenceVisible.value = true
}

// ==================== 故障记录页签 ====================
const faultLoading = ref(false)
const faultData = ref<any[]>([])
const faultPagination = ref({ page: 1, limit: 20, total: 0 })
const faultFilters = ref({
  variableName: '',
  onlyOpen: false,
  timeRange: null as [string, string] | null,
})

const getFaultList = async () => {
  if (!filters.value.projectId) return
  faultLoading.value = true
  try {
    const params: any = {
      page: faultPagination.value.page,
      limit: faultPagination.value.limit,
      projectId: filters.value.projectId,
    }
    if (faultFilters.value.variableName) params.variableName = faultFilters.value.variableName
    if (faultFilters.value.onlyOpen) params.onlyOpen = true
    if (faultFilters.value.timeRange) {
      params.startTime = faultFilters.value.timeRange[0]
      params.endTime = faultFilters.value.timeRange[1]
    }
    const res = await faultEvent.list(params) as any
    if (res) {
      faultData.value = res.data || []
      faultPagination.value.total = res.total || 0
    }
  }
  catch (error) {
    console.error(error)
  }
  finally {
    faultLoading.value = false
  }
}

const handleTabChange = (name: string | number) => {
  if (name === 'fault') getFaultList()
}

const formatDuration = (seconds: number | null) => {
  if (seconds == null) return '-'
  if (seconds < 60) return t('alarm_variable.durationSeconds', { s: seconds })
  if (seconds < 3600) return t('alarm_variable.durationMinutes', { m: Math.floor(seconds / 60), s: seconds % 60 })
  return t('alarm_variable.durationHours', { h: Math.floor(seconds / 3600), m: Math.floor((seconds % 3600) / 60) })
}

onMounted(async () => {
  await loadProjects()
  await getList()
})

onUnmounted(() => {
  stopRecognitionPolling()
})
</script>

<template>
  <div class="h-full">
    <FaPageMain class="h-full flex flex-col">
      <FaSearchBar :show-toggle="false">
        <template #default>
          <el-form :inline="true" class="w-full">
            <el-form-item :label="$t('alarm_variable.projectLabel')">
              <el-select v-model="filters.projectId" :placeholder="$t('alarm_variable.selectProjectPlaceholder')" filterable style="width: 220px" @change="handleSearch">
                <el-option v-for="p in projectOptions" :key="p.id" :label="p.projectName" :value="String(p.id)" />
              </el-select>
            </el-form-item>
            <el-form-item :label="$t('alarm_variable.keywordLabel')">
              <el-input v-model="filters.keyword" :placeholder="$t('alarm_variable.keywordPlaceholder')" clearable style="width: 180px" @keyup.enter="handleSearch" />
            </el-form-item>
            <el-form-item :label="$t('alarm_variable.categoryLabel')">
              <el-select v-model="filters.category" :placeholder="$t('alarm_variable.allOption')" clearable style="width: 120px" @change="handleSearch">
                <el-option v-for="c in categoryOptions" :key="c.value" :label="c.label" :value="c.value" />
              </el-select>
            </el-form-item>
            <el-form-item :label="$t('alarm_variable.levelLabel')">
              <el-select v-model="filters.level" :placeholder="$t('alarm_variable.allOption')" clearable style="width: 110px" @change="handleSearch">
                <el-option v-for="l in levelOptions" :key="l.value" :label="l.label" :value="l.value" />
              </el-select>
            </el-form-item>
            <el-form-item :label="$t('alarm_variable.monitorLabel')">
              <el-select v-model="filters.monitorEnabled" :placeholder="$t('alarm_variable.allOption')" clearable style="width: 100px" @change="handleSearch">
                <el-option :label="$t('alarm_variable.opened')" value="true" />
                <el-option :label="$t('alarm_variable.notOpened')" value="false" />
              </el-select>
            </el-form-item>
            <el-form-item :label="$t('common.confirm')">
              <el-select v-model="filters.manuallyConfirmed" :placeholder="$t('alarm_variable.allOption')" clearable style="width: 100px" @change="handleSearch">
                <el-option :label="$t('alarm_variable.confirmedOption')" value="true" />
                <el-option :label="$t('alarm_variable.notConfirmedOption')" value="false" />
              </el-select>
            </el-form-item>
            <el-form-item>
              <el-button type="primary" @click="handleSearch">
                <FaIcon name="i-ep:search" class="mr-1" />
                {{ $t('common.search') }}
              </el-button>
              <el-button type="success" @click="handleRerunRecognition" :disabled="!filters.projectId">
                <FaIcon name="i-ep:magic-stick" class="mr-1" />
                {{ $t('alarm_variable.rerunRecognition') }}
              </el-button>
              <el-button type="primary" @click="handleCreate">
                <FaIcon name="i-ep:plus" class="mr-1" />
                {{ $t('alarm_variable.manualCreate') }}
              </el-button>
              <el-button @click="handleBatchMonitor(true)">
                {{ $t('alarm_variable.batchMonitorOn') }}
              </el-button>
              <el-button @click="handleBatchMonitor(false)">
                {{ $t('alarm_variable.batchMonitorOff') }}
              </el-button>
              <el-button type="danger" plain @click="handleBatchDelete">
                {{ $t('common.deleteBatch') }}
              </el-button>
            </el-form-item>
          </el-form>
        </template>
      </FaSearchBar>

      <el-divider border-style="dashed" class="my-4" />

      <el-tabs v-model="activeTab" class="flex-1 flex flex-col" @tab-change="handleTabChange">
        <!-- ============ 页签一：变量台账 ============ -->
        <el-tab-pane :label="$t('alarm_variable.ledgerTab')" name="ledger">
          <el-alert type="info" :closable="false" class="mb-2"
            :title="$t('alarm_variable.ledgerAlertTitle')" />
          <el-table v-loading="loading" :data="tableData" style="width: 100%" stripe border
            :row-class-name="({ row }: any) => (row.collectable ? '' : 'uncollectable-row')"
            @selection-change="handleSelectionChange">
            <el-table-column type="selection" width="45" />
            <el-table-column prop="variableName" :label="$t('alarm_variable.variableNameLabel')" min-width="220" show-overflow-tooltip />
            <el-table-column prop="address" :label="$t('alarm_variable.addressLabel')" width="110" show-overflow-tooltip />
            <el-table-column prop="stationCode" :label="$t('alarm_variable.stationLabel')" width="90" show-overflow-tooltip />
            <el-table-column prop="alarmCategory" :label="$t('alarm_variable.categoryLabel')" width="90" />
            <el-table-column prop="alarmLevel" :label="$t('alarm_variable.levelLabel')" width="90">
              <template #default="{ row }">
                <el-tag :type="row.alarmLevel === 'Critical' ? 'danger' : row.alarmLevel === 'Info' ? 'info' : 'warning'" size="small">
                  {{ row.alarmLevel || 'Warn' }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column prop="description" :label="$t('alarm_variable.descriptionLabel')" min-width="180" show-overflow-tooltip />
            <el-table-column :label="$t('alarm_variable.sourceLabel')" width="100">
              <template #default="{ row }">
                <el-tag size="small" :type="row.source === 'Manual' ? 'success' : row.source === 'Structural' ? 'info' : 'warning'">
                  {{ row.source === 'Structural' ? $t('alarm_variable.structuralSource') : row.source === 'Manual' ? $t('alarm_variable.manualSource') : 'LLM' }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column :label="$t('alarm_variable.confidenceLabel')" width="85">
              <template #default="{ row }">
                <span :class="{ 'text-red-500': row.llmConfidence != null && row.llmConfidence < 0.7 }">
                  {{ row.llmConfidence != null ? Number(row.llmConfidence).toFixed(2) : '-' }}
                </span>
                <span v-if="row.llmConfidence != null && row.llmConfidence < 0.7" class="ml-1 text-xs text-orange-500">{{ $t('alarm_variable.needsReview') }}</span>
              </template>
            </el-table-column>
            <el-table-column :label="$t('alarm_variable.monitorLabel')" width="80" align="center">
              <template #default="{ row }">
                <el-switch :model-value="row.monitorEnabled" :disabled="!row.collectable"
                  @change="(v: any) => handleMonitorSwitch(row, v)" />
              </template>
            </el-table-column>
            <el-table-column :label="$t('alarm_variable.pushLabel')" width="90" align="center">
              <template #default="{ row }">
                <el-switch :model-value="row.pushEnabled" @change="(v: any) => handlePushSwitch(row, v)" />
              </template>
            </el-table-column>
            <el-table-column :label="$t('alarm_variable.confirmLockLabel')" width="80" align="center">
              <template #default="{ row }">
                <el-button link :type="row.manuallyConfirmed ? 'success' : 'info'" @click="handleConfirm(row)">
                  {{ row.manuallyConfirmed ? $t('alarm_variable.lockedConfirmed') : $t('common.confirm') }}
                </el-button>
              </template>
            </el-table-column>
            <el-table-column prop="alarmCount" :label="$t('alarm_variable.alarmCountLabel')" width="85" align="center" />
            <el-table-column prop="lastAlarmAt" :label="$t('alarm_variable.lastAlarmAtLabel')" width="160" show-overflow-tooltip />
            <el-table-column :label="$t('common.operate')" width="150" fixed="right" align="center">
              <template #default="{ row }">
                <el-button link type="primary" @click="handleEdit(row)">
                  {{ $t('common.edit') }}
                </el-button>
                <el-button link type="info" @click="showEvidence(row)">
                  {{ $t('alarm_variable.evidenceLabel') }}
                </el-button>
                <el-button link type="danger" @click="handleDelete(row)">
                  {{ $t('common.delete') }}
                </el-button>
              </template>
            </el-table-column>
          </el-table>

          <div class="mt-4 flex justify-end">
            <el-pagination v-model:current-page="pagination.page" v-model:page-size="pagination.limit"
              :total="pagination.total" :page-sizes="[10, 20, 50, 100]"
              layout="total, sizes, prev, pager, next, jumper" @current-change="onPageChange" @size-change="onSizeChange" />
          </div>
        </el-tab-pane>

        <!-- ============ 页签二：故障记录 ============ -->
        <el-tab-pane :label="$t('alarm_variable.faultTab')" name="fault">
          <el-form :inline="true" class="mb-2">
            <el-form-item :label="$t('alarm_variable.variableLabel')">
              <el-input v-model="faultFilters.variableName" :placeholder="$t('alarm_variable.variableNamePlaceholder')" clearable style="width: 200px"
                @keyup.enter="getFaultList" />
            </el-form-item>
            <el-form-item :label="$t('alarm_variable.timeLabel')">
              <el-date-picker v-model="faultFilters.timeRange" type="datetimerange" :range-separator="$t('alarm_variable.rangeTo')"
                :start-placeholder="$t('alarm_variable.startWord')" :end-placeholder="$t('alarm_variable.endWord')" />
            </el-form-item>
            <el-form-item>
              <el-checkbox v-model="faultFilters.onlyOpen" :label="$t('alarm_variable.onlyOpenLabel')" @change="getFaultList" />
            </el-form-item>
            <el-form-item>
              <el-button type="primary" @click="getFaultList">
                {{ $t('alarm_variable.queryButton') }}
              </el-button>
            </el-form-item>
          </el-form>

          <el-table v-loading="faultLoading" :data="faultData" style="width: 100%" stripe border>
            <el-table-column prop="variableName" :label="$t('alarm_variable.variableNameLabel')" min-width="220" show-overflow-tooltip />
            <el-table-column prop="stationCode" :label="$t('alarm_variable.stationLabel')" width="90" />
            <el-table-column prop="alarmLevel" :label="$t('alarm_variable.levelLabel')" width="90">
              <template #default="{ row }">
                <el-tag :type="row.alarmLevel === 'Critical' ? 'danger' : 'warning'" size="small">
                  {{ row.alarmLevel || 'Warn' }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column prop="description" :label="$t('alarm_variable.descriptionLabel')" min-width="180" show-overflow-tooltip />
            <el-table-column prop="startedAt" :label="$t('common.beginTime')" width="170" />
            <el-table-column prop="endedAt" :label="$t('common.endTime')" width="170">
              <template #default="{ row }">
                <span v-if="row.endedAt">{{ row.endedAt }}</span>
                <el-tag v-else type="danger" size="small">{{ $t('alarm_variable.ongoing') }}</el-tag>
              </template>
            </el-table-column>
            <el-table-column :label="$t('alarm_variable.durationLabel')" width="130">
              <template #default="{ row }">
                {{ formatDuration(row.durationSeconds) }}
              </template>
            </el-table-column>
            <el-table-column prop="pushedToSubscriber" :label="$t('alarm_variable.pushedLabel')" width="80" align="center">
              <template #default="{ row }">
                <el-tag :type="row.pushedToSubscriber ? 'success' : 'info'" size="small">
                  {{ row.pushedToSubscriber ? $t('common.yes') : $t('common.no') }}
                </el-tag>
              </template>
            </el-table-column>
          </el-table>

          <div class="mt-4 flex justify-end">
            <el-pagination v-model:current-page="faultPagination.page" v-model:page-size="faultPagination.limit"
              :total="faultPagination.total" :page-sizes="[10, 20, 50, 100]"
              layout="total, sizes, prev, pager, next, jumper"
              @current-change="(v: number) => { faultPagination.page = v; getFaultList() }"
              @size-change="(v: number) => { faultPagination.limit = v; faultPagination.page = 1; getFaultList() }" />
          </div>
        </el-tab-pane>
      </el-tabs>
    </FaPageMain>

    <!-- 识别进度对话框 -->
    <el-dialog v-model="recognitionVisible" :title="$t('alarm_variable.recognitionDialogTitle')" width="520px"
      :close-on-click-modal="false" @closed="stopRecognitionPolling">
      <div v-if="recognitionProgress" class="progress-container">
        <div class="mb-2 flex justify-between">
          <span>{{ statusText }}</span>
          <span v-if="recognitionProgress.totalFiles > 0">{{ recognitionProgress.processedFiles }} / {{ recognitionProgress.totalFiles }}</span>
        </div>
        <el-progress :percentage="recognitionProgress.percentage || 0"
          :status="recognitionProgress.status === 3 ? 'success' : recognitionProgress.status === 4 ? 'exception' : undefined" />
        <div class="mt-2 text-xs text-gray-500">
          {{ recognitionProgress.message }}
        </div>
      </div>
      <template #footer>
        <el-button @click="recognitionVisible = false">{{ $t('common.close') }}</el-button>
      </template>
    </el-dialog>

    <!-- 新增/编辑对话框 -->
    <el-dialog v-model="editVisible" :title="editType === 'create' ? $t('alarm_variable.createDialogTitle') : $t('alarm_variable.editDialogTitle')" width="520px">
      <el-form :model="editForm" label-width="90px">
        <el-form-item v-if="editType === 'create'" :label="$t('alarm_variable.variableNameLabel')" required>
          <el-input v-model="editForm.variableName" :placeholder="$t('alarm_variable.variableNameTip')" />
        </el-form-item>
        <el-form-item :label="$t('alarm_variable.stationLabel')">
          <el-input v-model="editForm.stationCode" :placeholder="$t('alarm_variable.stationPlaceholder')" />
        </el-form-item>
        <el-form-item :label="$t('alarm_variable.categoryLabel')">
          <el-select v-model="editForm.alarmCategory" style="width: 100%">
            <el-option v-for="c in categoryOptions" :key="c.value" :label="c.label" :value="c.value" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('alarm_variable.levelLabel')">
          <el-select v-model="editForm.alarmLevel" style="width: 100%">
            <el-option v-for="l in levelOptions" :key="l.value" :label="l.label" :value="l.value" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('alarm_variable.descriptionLabel')">
          <el-input v-model="editForm.description" type="textarea" :rows="2" maxlength="480" show-word-limit />
        </el-form-item>
        <el-form-item :label="$t('alarm_variable.remarkLabel')">
          <el-input v-model="editForm.remark" type="textarea" :rows="2" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="editVisible = false">{{ $t('common.cancel') }}</el-button>
        <el-button type="primary" @click="submitEdit">{{ $t('alarm_variable.okButton') }}</el-button>
      </template>
    </el-dialog>

    <!-- 证据抽屉 -->
    <el-drawer v-model="evidenceVisible" :title="$t('alarm_variable.evidenceTitle', { name: evidenceRow?.variableName || '' })" size="400px">
      <div v-if="evidenceRow" class="text-sm">
        <div class="mb-2">
          <el-tag size="small" :type="evidenceRow.source === 'Manual' ? 'success' : 'warning'">
            {{ $t('alarm_variable.sourceLabel') }}：{{ evidenceRow.source === 'Structural' ? $t('alarm_variable.structuralSource') : evidenceRow.source === 'Manual' ? $t('alarm_variable.manualSource') : 'LLM' }}
          </el-tag>
          <el-tag v-if="evidenceRow.llmConfidence != null" size="small" class="ml-2">
            {{ $t('alarm_variable.confidenceLabel') }}：{{ Number(evidenceRow.llmConfidence).toFixed(2) }}
          </el-tag>
        </div>
        <el-timeline>
          <el-timeline-item v-for="(line, i) in evidenceLines" :key="i" :timestamp="$t('alarm_variable.evidenceItem', { index: i + 1 })">
            {{ line }}
          </el-timeline-item>
        </el-timeline>
        <div v-if="evidenceLines.length === 0" class="text-gray-400">
          {{ $t('alarm_variable.noEvidence') }}
        </div>
      </div>
    </el-drawer>
  </div>
</template>

<style scoped>
.progress-container {
  background: var(--el-fill-color-light);
  border-radius: 8px;
  padding: 16px;
}

:deep(.el-table .uncollectable-row) {
  color: var(--el-text-color-placeholder);
}
</style>
