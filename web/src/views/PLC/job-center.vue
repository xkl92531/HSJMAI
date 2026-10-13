<route lang="yaml">
meta:
  title: PLC任务调度中心
</route>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import JobCenterAPI from '@/api/modules/PLC/jobCenter'
import messages from './job-center.lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

type ScheduleType = 'interval' | 'daily'
type ExecutorType = 'internal' | 'http'

type JobTemplate = {
  taskType: string
  taskTypeName: string
  executorType: ExecutorType
  category?: string
  description?: string
}

type JobStationGroup = {
  stationCode: string
  stationName: string
  projectCount: number
  pointCount: number
  tableNames: string[]
}

type JobTask = {
  taskKey: string
  taskName: string
  taskType: string
  taskTypeName?: string
  executorType?: ExecutorType
  category?: string
  description?: string
  enabled?: boolean
  scheduleType?: ScheduleType
  intervalMilliseconds?: number | null
  dailyTime?: string | null
  timeoutMilliseconds?: number
  retryCount?: number
  projectIdsText?: string
  stationGroupCodesJson?: string
  stationMode?: string
  resetCacheBeforeCollect?: boolean
  requestUrl?: string
  httpMethod?: string
  headersJson?: string
  queryJson?: string
  bodyJson?: string
  contentType?: string
  lastRunAt?: string
  nextRunAt?: string
  lastRunStatus?: string
  lastMessage?: string
  lastDurationMilliseconds?: number
  lastReadCount?: number
  lastCacheHitCount?: number
  lastNoCacheCount?: number
  lastReadPlcDurationMilliseconds?: number
  lastProjectCount?: number
  lastChangedCount?: number
  lastInsertedCount?: number
}

const refreshIntervalMs = 1000
let refreshTimer: ReturnType<typeof window.setInterval> | null = null
let durationTimer: ReturnType<typeof window.setInterval> | null = null
const nowTick = ref(Date.now())

const loading = ref(false)
const saveLoading = ref(false)
const runLoading = ref(false)
const clearCacheLoading = ref(false)
const createLoading = ref(false)
const detailLoading = ref(false)
const createVisible = ref(false)

const tasks = ref<JobTask[]>([])
const templates = ref<JobTemplate[]>([])
const currentTask = ref<JobTask | null>(null)
const currentTaskKey = ref('')
const configStationGroups = ref<JobStationGroup[]>([])
const createStationGroups = ref<JobStationGroup[]>([])
const configSelectedStationCodes = ref<string[]>([])
const createSelectedStationCodes = ref<string[]>([])

const configForm = reactive({
  taskKey: '',
  taskName: '',
  enabled: false,
  scheduleType: 'interval' as ScheduleType,
  intervalMilliseconds: 30000,
  dailyTime: '08:00:00',
  timeoutMilliseconds: 30000,
  retryCount: 0,
  projectIdsText: '4541738602710831104',
  stationGroupCodesJson: '[]',
  stationMode: 'AUTO',
  resetCacheBeforeCollect: false,
  requestUrl: '',
  httpMethod: 'POST',
  headersJson: '{}',
  queryJson: '{}',
  bodyJson: '{}',
  contentType: 'application/json',
})

const createForm = reactive({
  executorType: 'http' as ExecutorType,
  taskName: '',
  enabled: false,
  scheduleType: 'interval' as ScheduleType,
  intervalMilliseconds: 30000,
  dailyTime: '08:00:00',
  timeoutMilliseconds: 30000,
  retryCount: 0,
  projectIdsText: '4541738602710831104',
  stationGroupCodesJson: '[]',
  stationMode: 'AUTO',
  resetCacheBeforeCollect: false,
  requestUrl: '',
  httpMethod: 'POST',
  headersJson: '{}',
  queryJson: '{}',
  bodyJson: '{}',
  contentType: 'application/json',
})

const createTemplate = computed(() => templates.value.find(item => item.executorType === createForm.executorType))

function normalizeTemplate(raw: any): JobTemplate {
  return {
    taskType: raw?.taskType ?? raw?.TaskType ?? '',
    taskTypeName: raw?.taskTypeName ?? raw?.TaskTypeName ?? '',
    executorType: (raw?.executorType ?? raw?.ExecutorType ?? 'internal') as ExecutorType,
    category: raw?.category ?? raw?.Category ?? '',
    description: raw?.description ?? raw?.Description ?? '',
  }
}

function normalizeTask(raw: any): JobTask {
  return {
    taskKey: raw?.taskKey ?? raw?.TaskKey ?? '',
    taskName: raw?.taskName ?? raw?.TaskName ?? '',
    taskType: raw?.taskType ?? raw?.TaskType ?? '',
    taskTypeName: raw?.taskTypeName ?? raw?.TaskTypeName ?? '',
    executorType: (raw?.executorType ?? raw?.ExecutorType ?? 'internal') as ExecutorType,
    category: raw?.category ?? raw?.Category ?? '',
    description: raw?.description ?? raw?.Description ?? '',
    enabled: raw?.enabled ?? raw?.Enabled ?? false,
    scheduleType: (raw?.scheduleType ?? raw?.ScheduleType ?? 'interval') as ScheduleType,
    intervalMilliseconds: raw?.intervalMilliseconds ?? raw?.IntervalMilliseconds ?? null,
    dailyTime: raw?.dailyTime ?? raw?.DailyTime ?? null,
    timeoutMilliseconds: raw?.timeoutMilliseconds ?? raw?.TimeoutMilliseconds ?? 30000,
    retryCount: raw?.retryCount ?? raw?.RetryCount ?? 0,
    projectIdsText: raw?.projectIdsText ?? raw?.ProjectIdsText ?? '4541738602710831104',
    stationGroupCodesJson: raw?.stationGroupCodesJson ?? raw?.StationGroupCodesJson ?? '[]',
    stationMode: raw?.stationMode ?? raw?.StationMode ?? 'AUTO',
    resetCacheBeforeCollect: raw?.resetCacheBeforeCollect ?? raw?.ResetCacheBeforeCollect ?? false,
    requestUrl: raw?.requestUrl ?? raw?.RequestUrl ?? '',
    httpMethod: raw?.httpMethod ?? raw?.HttpMethod ?? 'POST',
    headersJson: raw?.headersJson ?? raw?.HeadersJson ?? '{}',
    queryJson: raw?.queryJson ?? raw?.QueryJson ?? '{}',
    bodyJson: raw?.bodyJson ?? raw?.BodyJson ?? '{}',
    contentType: raw?.contentType ?? raw?.ContentType ?? 'application/json',
    lastRunAt: raw?.lastRunAt ?? raw?.LastRunAt ?? '',
    nextRunAt: raw?.nextRunAt ?? raw?.NextRunAt ?? '',
    lastRunStatus: raw?.lastRunStatus ?? raw?.LastRunStatus ?? '',
    lastMessage: raw?.lastMessage ?? raw?.LastMessage ?? '',
    lastDurationMilliseconds: raw?.lastDurationMilliseconds ?? raw?.LastDurationMilliseconds ?? 0,
    lastReadCount: raw?.lastReadCount ?? raw?.LastReadCount ?? 0,
    lastCacheHitCount: raw?.lastCacheHitCount ?? raw?.LastCacheHitCount ?? 0,
    lastNoCacheCount: raw?.lastNoCacheCount ?? raw?.LastNoCacheCount ?? 0,
    lastReadPlcDurationMilliseconds: raw?.lastReadPlcDurationMilliseconds ?? raw?.LastReadPlcDurationMilliseconds ?? 0,
    lastProjectCount: raw?.lastProjectCount ?? raw?.LastProjectCount ?? 0,
    lastChangedCount: raw?.lastChangedCount ?? raw?.LastChangedCount ?? 0,
    lastInsertedCount: raw?.lastInsertedCount ?? raw?.LastInsertedCount ?? 0,
  }
}

function normalizeStationGroups(raw: any): JobStationGroup[] {
  return Array.isArray(raw)
    ? raw.map((item: any) => ({
        stationCode: item?.stationCode ?? item?.StationCode ?? '',
        stationName: item?.stationName ?? item?.StationName ?? item?.stationCode ?? item?.StationCode ?? '',
        projectCount: item?.projectCount ?? item?.ProjectCount ?? 0,
        pointCount: item?.pointCount ?? item?.PointCount ?? 0,
        tableNames: Array.isArray(item?.tableNames ?? item?.TableNames) ? (item?.tableNames ?? item?.TableNames) : [],
      })).filter((item: JobStationGroup) => !!item.stationCode)
    : []
}

function parseStationGroupCodesJson(json?: string) {
  if (!json)
    return [] as string[]
  try {
    const values = JSON.parse(json)
    if (!Array.isArray(values))
      return []
    return values.map(item => `${item ?? ''}`.trim()).filter(Boolean)
  }
  catch {
    return []
  }
}

function mergeTaskSummary(primary: JobTask, fallback?: JobTask | null): JobTask {
  if (!fallback)
    return primary

  const merged = { ...primary }
  if ((!merged.lastRunAt || merged.lastRunAt === '') && fallback.lastRunAt)
    merged.lastRunAt = fallback.lastRunAt
  if ((!merged.lastMessage || merged.lastMessage === '') && fallback.lastMessage)
    merged.lastMessage = fallback.lastMessage
  return merged
}

function applyDetailToForm(task: JobTask) {
  configForm.taskKey = task.taskKey
  configForm.taskName = task.taskName || ''
  configForm.enabled = !!task.enabled
  configForm.scheduleType = (task.scheduleType || 'interval') as ScheduleType
  configForm.intervalMilliseconds = task.intervalMilliseconds || 30000
  configForm.dailyTime = task.dailyTime || '08:00:00'
  configForm.timeoutMilliseconds = task.timeoutMilliseconds || 30000
  configForm.retryCount = task.retryCount || 0
  configForm.projectIdsText = task.projectIdsText || '4541738602710831104'
  configForm.stationGroupCodesJson = task.stationGroupCodesJson || '[]'
  configForm.stationMode = task.stationMode || 'AUTO'
  configForm.resetCacheBeforeCollect = !!task.resetCacheBeforeCollect
  configForm.requestUrl = task.requestUrl || ''
  configForm.httpMethod = task.httpMethod || 'POST'
  configForm.headersJson = task.headersJson || '{}'
  configForm.queryJson = task.queryJson || '{}'
  configForm.bodyJson = task.bodyJson || '{}'
  configForm.contentType = task.contentType || 'application/json'
  configSelectedStationCodes.value = parseStationGroupCodesJson(configForm.stationGroupCodesJson)
}

async function loadStationGroups(projectIdsText: string, target: 'config' | 'create', silent = false) {
  try {
    const res: any = await JobCenterAPI.getStationGroups(projectIdsText)
    const groups = normalizeStationGroups(res)
    if (target === 'config')
      configStationGroups.value = groups
    else
      createStationGroups.value = groups
  }
  catch (error: any) {
    if (!silent)
      ElMessage.error(error?.message || t('job_center.loadStationGroupsFailed'))
  }
}

function isAllStationsSelected(target: 'config' | 'create') {
  const selected = target === 'config' ? configSelectedStationCodes.value : createSelectedStationCodes.value
  return selected.length === 0
}

function isStationSelected(target: 'config' | 'create', stationCode: string) {
  const selected = target === 'config' ? configSelectedStationCodes.value : createSelectedStationCodes.value
  return selected.includes(stationCode)
}

function toggleAllStations(target: 'config' | 'create') {
  if (target === 'config') {
    configSelectedStationCodes.value = []
    configForm.stationGroupCodesJson = '[]'
  }
  else {
    createSelectedStationCodes.value = []
    createForm.stationGroupCodesJson = '[]'
  }
}

function toggleStationSelection(target: 'config' | 'create', stationCode: string) {
  const selected = target === 'config'
    ? [...configSelectedStationCodes.value]
    : [...createSelectedStationCodes.value]
  const index = selected.indexOf(stationCode)
  if (index >= 0)
    selected.splice(index, 1)
  else
    selected.push(stationCode)

  const json = JSON.stringify(selected)
  if (target === 'config') {
    configSelectedStationCodes.value = selected
    configForm.stationGroupCodesJson = json
  }
  else {
    createSelectedStationCodes.value = selected
    createForm.stationGroupCodesJson = json
  }
}

function getStationSelectionText(target: 'config' | 'create') {
  const selected = target === 'config' ? configSelectedStationCodes.value : createSelectedStationCodes.value
  return selected.length === 0 ? t('job_center.allStations') : t('job_center.selectedStations', { count: selected.length })
}

function formatTime(value?: string) {
  if (!value)
    return '-'
  const date = new Date(value)
  if (Number.isNaN(date.getTime()))
    return value
  return date.toLocaleString('zh-CN', { hour12: false })
}

function getStatusText(status?: string) {
  if (status === 'running')
    return t('job_center.statusRunning')
  if (status === 'success')
    return t('job_center.statusSuccess')
  if (status === 'partial_success')
    return t('job_center.statusPartialSuccess')
  if (status === 'failed')
    return t('job_center.statusFailed')
  return t('job_center.statusNotRun')
}

function getStatusType(status?: string) {
  if (status === 'running')
    return 'warning'
  if (status === 'success')
    return 'success'
  if (status === 'partial_success')
    return 'warning'
  if (status === 'failed')
    return 'danger'
  return 'info'
}

function getScheduleText(task: JobTask) {
  if (task.scheduleType === 'daily')
    return t('job_center.scheduleDailyText', { time: task.dailyTime || '08:00:00' })
  return t('job_center.scheduleIntervalText', { ms: task.intervalMilliseconds || 0 })
}

function executorText(executorType?: ExecutorType) {
  return executorType === 'http' ? t('job_center.httpTask') : t('job_center.internalTask')
}

function getDisplayDuration(task?: JobTask | null) {
  if (!task)
    return 0
  if (task.lastRunStatus === 'running' && task.lastRunAt) {
    const startedAt = new Date(task.lastRunAt).getTime()
    if (!Number.isNaN(startedAt))
      return Math.max(nowTick.value - startedAt, 0)
  }
  return task.lastDurationMilliseconds || 0
}

async function loadTemplates() {
  const res: any = await JobCenterAPI.getTaskTemplates()
  templates.value = Array.isArray(res) ? res.map((item: any) => normalizeTemplate(item)) : []
}

async function loadTasks(selectTaskKey?: string, silent = false) {
  if (!silent)
    loading.value = true
  try {
    const res: any = await JobCenterAPI.getTasks()
    tasks.value = Array.isArray(res) ? res.map((item: any) => normalizeTask(item)) : []
    const targetKey = selectTaskKey || currentTaskKey.value || tasks.value[0]?.taskKey
    if (targetKey)
      await loadTaskDetail(targetKey, true, configForm.taskKey !== targetKey)
    else
      currentTask.value = null
  }
  catch (error: any) {
    if (!silent)
      ElMessage.error(error?.message || t('job_center.loadTasksFailed'))
  }
  finally {
    if (!silent)
      loading.value = false
  }
}

async function loadTaskDetail(taskKey: string, silent = false, applyForm = false) {
  if (!taskKey)
    return
  if (!silent)
    detailLoading.value = true
  try {
    const res: any = await JobCenterAPI.getTaskDetail(taskKey)
    const listTask = tasks.value.find(item => item.taskKey === taskKey) || null
    currentTask.value = res ? mergeTaskSummary(normalizeTask(res), listTask) : listTask
    currentTaskKey.value = currentTask.value?.taskKey || ''
    if (currentTask.value && applyForm) {
      applyDetailToForm(currentTask.value)
      if (currentTask.value.executorType === 'internal')
        await loadStationGroups(configForm.projectIdsText, 'config', true)
    }
  }
  catch (error: any) {
    if (!silent)
      ElMessage.error(error?.message || t('job_center.loadTaskDetailFailed'))
  }
  finally {
    if (!silent)
      detailLoading.value = false
  }
}

function mergeCurrentTaskFromList() {
  if (!currentTaskKey.value)
    return
  const listTask = tasks.value.find(item => item.taskKey === currentTaskKey.value)
  if (!listTask)
    return
  currentTask.value = {
    ...currentTask.value,
    ...listTask,
  }
}

async function refreshDashboardSilently() {
  try {
    const listRes: any = await JobCenterAPI.getTasks()
    tasks.value = Array.isArray(listRes) ? listRes.map((item: any) => normalizeTask(item)) : []

    if (!currentTaskKey.value) {
      const firstTask = tasks.value[0]
      if (firstTask) {
        currentTaskKey.value = firstTask.taskKey
        currentTask.value = firstTask
      }
      return
    }

    const detailRes: any = await JobCenterAPI.getTaskDetail(currentTaskKey.value)
    if (detailRes) {
      const listTask = tasks.value.find(item => item.taskKey === currentTaskKey.value) || null
      currentTask.value = mergeTaskSummary(normalizeTask(detailRes), listTask)
    }
  }
  catch {
  }
}

function startAutoRefresh() {
  stopAutoRefresh()
  refreshTimer = window.setInterval(async () => {
    await refreshDashboardSilently()
  }, refreshIntervalMs)
  durationTimer = window.setInterval(() => {
    nowTick.value = Date.now()
  }, 100)
}

function stopAutoRefresh() {
  if (refreshTimer) {
    window.clearInterval(refreshTimer)
    refreshTimer = null
  }
  if (durationTimer) {
    window.clearInterval(durationTimer)
    durationTimer = null
  }
}

function openCreateDialog() {
  createForm.executorType = 'http'
  createForm.taskName = ''
  createForm.enabled = false
  createForm.scheduleType = 'interval'
  createForm.intervalMilliseconds = 30000
  createForm.dailyTime = '08:00:00'
  createForm.timeoutMilliseconds = 30000
  createForm.retryCount = 0
  createForm.projectIdsText = '4541738602710831104'
  createForm.stationGroupCodesJson = '[]'
  createForm.stationMode = 'AUTO'
  createForm.resetCacheBeforeCollect = false
  createForm.requestUrl = ''
  createForm.httpMethod = 'POST'
  createForm.headersJson = '{}'
  createForm.queryJson = '{}'
  createForm.bodyJson = '{}'
  createForm.contentType = 'application/json'
  createSelectedStationCodes.value = []
  createVisible.value = true
  if (createForm.executorType === 'internal')
    loadStationGroups(createForm.projectIdsText, 'create', true)
}

async function handleCreate() {
  if (!createTemplate.value?.taskType) {
    ElMessage.warning(t('job_center.pleaseSelectExecutorType'))
    return
  }

  createLoading.value = true
  try {
    const res: any = await JobCenterAPI.createTask({
      taskName: createForm.taskName,
      taskType: createTemplate.value.taskType,
      enabled: createForm.enabled,
      scheduleType: createForm.scheduleType,
      intervalMilliseconds: createForm.scheduleType === 'interval' ? Math.max(1, Number(createForm.intervalMilliseconds) || 30000) : undefined,
      dailyTime: createForm.scheduleType === 'daily' ? createForm.dailyTime : undefined,
      timeoutMilliseconds: Math.max(1000, Number(createForm.timeoutMilliseconds) || 30000),
      retryCount: Math.max(0, Number(createForm.retryCount) || 0),
      projectIdsText: createForm.projectIdsText,
      stationGroupCodesJson: createForm.stationGroupCodesJson,
      stationMode: createForm.stationMode,
      resetCacheBeforeCollect: createForm.resetCacheBeforeCollect,
      requestUrl: createForm.requestUrl,
      httpMethod: createForm.httpMethod,
      headersJson: createForm.headersJson,
      queryJson: createForm.queryJson,
      bodyJson: createForm.bodyJson,
      contentType: createForm.contentType,
    })
    createVisible.value = false
    ElMessage.success(t('job_center.taskCreated'))
    await loadTasks(res.data?.taskKey || res.backResult?.taskKey)
  }
  catch (error: any) {
    ElMessage.error(error?.message || t('job_center.createTaskFailed'))
  }
  finally {
    createLoading.value = false
  }
}

async function handleSave() {
  if (!configForm.taskKey) {
    ElMessage.warning(t('job_center.pleaseSelectTask'))
    return
  }

  saveLoading.value = true
  try {
    await saveCurrentTaskConfig(true)
    await loadTasks(configForm.taskKey)
  }
  catch (error: any) {
    ElMessage.error(error?.message || t('job_center.saveTaskConfigFailed'))
  }
  finally {
    saveLoading.value = false
  }
}

async function saveCurrentTaskConfig(showSuccessMessage = true) {
  const res: any = await JobCenterAPI.saveTaskConfig({
    taskKey: configForm.taskKey,
    taskName: configForm.taskName,
    enabled: configForm.enabled,
    scheduleType: configForm.scheduleType,
    intervalMilliseconds: configForm.scheduleType === 'interval' ? Math.max(1, Number(configForm.intervalMilliseconds) || 30000) : undefined,
    dailyTime: configForm.scheduleType === 'daily' ? configForm.dailyTime : undefined,
    timeoutMilliseconds: Math.max(1000, Number(configForm.timeoutMilliseconds) || 30000),
    retryCount: Math.max(0, Number(configForm.retryCount) || 0),
    projectIdsText: configForm.projectIdsText,
    stationGroupCodesJson: configForm.stationGroupCodesJson,
    stationMode: configForm.stationMode,
    resetCacheBeforeCollect: configForm.resetCacheBeforeCollect,
    requestUrl: configForm.requestUrl,
    httpMethod: configForm.httpMethod,
    headersJson: configForm.headersJson,
    queryJson: configForm.queryJson,
    bodyJson: configForm.bodyJson,
    contentType: configForm.contentType,
  })
  currentTask.value = normalizeTask(res.data || res.backResult || currentTask.value)
  if (currentTask.value)
    applyDetailToForm(currentTask.value)
  if (showSuccessMessage)
    ElMessage.success(t('job_center.taskConfigSaved'))
  return res
}

async function handleRunNow(useSimulation = false) {
  if (!configForm.taskKey) {
    ElMessage.warning(t('job_center.pleaseSelectTask'))
    return
  }

  runLoading.value = true
  try {
    await saveCurrentTaskConfig(false)
    const res: any = await JobCenterAPI.runTaskNow({
      taskKey: configForm.taskKey,
      useSimulation,
      autoGenerateSimulationValues: useSimulation,
    })
    ElMessage.success(res.data?.message || res.backResult?.message || t('job_center.taskExecuted'))
    await loadTasks(configForm.taskKey)
  }
  catch (error: any) {
    ElMessage.error(error?.message || t('job_center.executeTaskFailed'))
  }
  finally {
    runLoading.value = false
  }
}

async function handleClearCache(clearAllProjectCaches = false) {
  if (!configForm.taskKey) {
    ElMessage.warning(t('job_center.pleaseSelectTask'))
    return
  }

  clearCacheLoading.value = true
  try {
    await saveCurrentTaskConfig(false)
    const confirmText = clearAllProjectCaches
      ? t('job_center.clearAllCacheConfirm')
      : t('job_center.clearRangeCacheConfirm')
    await ElMessageBox.confirm(confirmText, t('common.tip'), {
      type: 'warning',
      confirmButtonText: t('job_center.confirmClear'),
      cancelButtonText: t('common.cancel'),
    })

    const res: any = await JobCenterAPI.clearTaskCache({
      taskKey: configForm.taskKey,
      clearAllProjectCaches,
    })
    const info = res?.data || res?.backResult
    ElMessage.success(t('job_center.cacheCleared', { count: info?.clearedKeyCount ?? 0 }))
    await loadTasks(configForm.taskKey, true)
  }
  catch (error: any) {
    if (error !== 'cancel')
      ElMessage.error(error?.message || t('job_center.clearCacheFailed'))
  }
  finally {
    clearCacheLoading.value = false
  }
}

async function handleToggleTask(task: JobTask) {
  try {
    await JobCenterAPI.changeTaskState({
      taskKey: task.taskKey,
      enabled: !task.enabled,
    })
    ElMessage.success(task.enabled ? t('job_center.taskPaused') : t('job_center.taskEnabled'))
    await loadTasks(task.taskKey)
  }
  catch (error: any) {
    ElMessage.error(error?.message || t('job_center.toggleTaskFailed'))
  }
}

async function handleDeleteTask() {
  if (!currentTask.value?.taskKey) {
    ElMessage.warning(t('job_center.pleaseSelectTask'))
    return
  }

  try {
    await ElMessageBox.confirm(t('job_center.deleteTaskConfirm'), t('common.tip'), {
      type: 'warning',
      confirmButtonText: t('job_center.confirmDeleteText'),
      cancelButtonText: t('common.cancel'),
    })

    await JobCenterAPI.deleteTask({
      taskKey: currentTask.value.taskKey,
    })
    ElMessage.success(t('job_center.taskDeleted'))
    currentTask.value = null
    currentTaskKey.value = ''
    await loadTasks()
  }
  catch (error: any) {
    if (error !== 'cancel')
      ElMessage.error(error?.message || t('job_center.deleteTaskFailed'))
  }
}

onMounted(async () => {
  await loadTemplates()
  await loadTasks()
  startAutoRefresh()
})

watch(() => createForm.executorType, async (value) => {
  if (createVisible.value && value === 'internal')
    await loadStationGroups(createForm.projectIdsText, 'create', true)
})

onBeforeUnmount(() => {
  stopAutoRefresh()
})
</script>

<template>
  <div class="job-center-page" v-loading="loading">
    <FaPageHeader :title="$t('job_center.pageTitle')">
      <template #description>
        <div class="header-description">
          {{ $t('job_center.headerDescription') }}
        </div>
      </template>
      <div class="header-actions">
        <el-button @click="loadTasks(currentTaskKey)">
          {{ $t('job_center.refresh') }}
        </el-button>
        <el-button type="primary" @click="openCreateDialog">
          {{ $t('job_center.addTask') }}
        </el-button>
      </div>
    </FaPageHeader>

    <FaPageMain>
      <div class="job-center-layout">
        <section class="panel">
          <div class="panel-title-row">
            <div class="panel-title">{{ $t('job_center.taskList') }}</div>
            <div class="panel-subtitle">{{ $t('job_center.autoRefreshTip') }}</div>
          </div>

          <div class="job-list">
            <button
              v-for="task in tasks"
              :key="task.taskKey"
              type="button"
              class="job-list-item"
              :class="{ active: currentTaskKey === task.taskKey }"
              @click="loadTaskDetail(task.taskKey, false, true)"
            >
              <div class="job-list-top">
                <div class="job-name">{{ task.taskName }}</div>
                <el-tag size="small" :type="task.enabled ? 'success' : 'info'">
                  {{ task.enabled ? $t('common.enabled') : $t('job_center.paused') }}
                </el-tag>
              </div>
              <div class="job-meta">
                <span>{{ executorText(task.executorType) }}</span>
                <span>{{ task.taskTypeName || '-' }}</span>
              </div>
              <div class="job-desc">{{ task.description || '-' }}</div>
              <div class="job-time">{{ $t('job_center.scheduleMode') }}：{{ getScheduleText(task) }}</div>
              <div class="job-time">{{ $t('job_center.lastRunLabel') }}：{{ formatTime(task.lastRunAt) }}</div>
              <div class="job-time">{{ $t('job_center.nextRunLabel') }}：{{ formatTime(task.nextRunAt) }}</div>
            </button>
          </div>
        </section>

        <section class="panel detail-panel" v-loading="detailLoading">
          <template v-if="currentTask">
            <div class="detail-header">
              <div>
                <div class="panel-title">{{ currentTask.taskName }}</div>
                <div class="panel-subtitle">{{ currentTask.description || $t('job_center.noDescription') }}</div>
              </div>
              <div class="detail-header-actions">
                <el-tag :type="getStatusType(currentTask.lastRunStatus)">
                  {{ getStatusText(currentTask.lastRunStatus) }}
                </el-tag>
                <el-button size="small" @click="handleToggleTask(currentTask)">
                  {{ currentTask.enabled ? $t('job_center.pauseTask') : $t('job_center.enableTask') }}
                </el-button>
                <el-button size="small" type="danger" plain @click="handleDeleteTask">
                  {{ $t('job_center.deleteTask') }}
                </el-button>
              </div>
            </div>

            <div class="summary-grid">
              <div class="metric-tile">
                <div class="metric-label">{{ $t('job_center.executorTypeLabel') }}</div>
                <div class="metric-value">{{ executorText(currentTask.executorType) }}</div>
              </div>
              <div class="metric-tile">
                <div class="metric-label">{{ $t('job_center.scheduleMode') }}</div>
                <div class="metric-value">{{ currentTask.scheduleType === 'daily' ? $t('job_center.dailySchedule') : $t('job_center.intervalSchedule') }}</div>
              </div>
              <div class="metric-tile">
                <div class="metric-label">{{ $t('job_center.lastRunLabel') }}</div>
                <div class="metric-value">{{ formatTime(currentTask.lastRunAt) }}</div>
              </div>
              <div class="metric-tile">
                <div class="metric-label">{{ $t('job_center.nextRunLabel') }}</div>
                <div class="metric-value">{{ formatTime(currentTask.nextRunAt) }}</div>
              </div>
              <div class="metric-tile">
                <div class="metric-label">{{ $t('job_center.lastDurationLabel') }}</div>
                <div class="metric-value">{{ getDisplayDuration(currentTask) }} ms</div>
              </div>
              <div class="metric-tile">
                <div class="metric-label">{{ $t('job_center.readPlcDurationLabel') }}</div>
                <div class="metric-value">{{ currentTask.lastReadPlcDurationMilliseconds || 0 }} ms</div>
              </div>
              <div class="metric-tile">
                <div class="metric-label">{{ $t('job_center.readCountLabel') }}</div>
                <div class="metric-value">{{ currentTask.lastReadCount || 0 }}</div>
              </div>
              <div class="metric-tile">
                <div class="metric-label">{{ $t('job_center.cacheHitLabel') }}</div>
                <div class="metric-value">{{ currentTask.lastCacheHitCount || 0 }}</div>
              </div>
              <div class="metric-tile">
                <div class="metric-label">{{ $t('job_center.noCacheLabel') }}</div>
                <div class="metric-value">{{ currentTask.lastNoCacheCount || 0 }}</div>
              </div>
              <div class="metric-tile">
                <div class="metric-label">{{ $t('job_center.lastInsertedLabel') }}</div>
                <div class="metric-value">{{ currentTask.lastInsertedCount || 0 }}</div>
              </div>
            </div>

            <div class="message-band">
              {{ currentTask.lastMessage || $t('job_center.noSummary') }}
            </div>

            <div class="config-band">
              <div class="panel-title">{{ $t('job_center.taskConfig') }}</div>
              <el-form label-width="120px">
                <el-row :gutter="16">
                  <el-col :xs="24" :md="8">
                    <el-form-item :label="$t('job_center.taskName')">
                      <el-input v-model="configForm.taskName" />
                    </el-form-item>
                  </el-col>
                  <el-col :xs="24" :md="6">
                    <el-form-item :label="$t('job_center.executorTypeLabel')">
                      <el-input :model-value="executorText(currentTask.executorType)" disabled />
                    </el-form-item>
                  </el-col>
                  <el-col :xs="24" :md="5">
                    <el-form-item :label="$t('job_center.taskTypeLabel')">
                      <el-input :model-value="currentTask.taskTypeName || currentTask.taskType" disabled />
                    </el-form-item>
                  </el-col>
                  <el-col :xs="24" :md="5">
                    <el-form-item :label="$t('job_center.enabledLabel')">
                      <el-switch v-model="configForm.enabled" />
                    </el-form-item>
                  </el-col>
                </el-row>

                <el-row :gutter="16">
                  <el-col :xs="24" :md="8">
                    <el-form-item :label="$t('job_center.scheduleMode')">
                      <el-radio-group v-model="configForm.scheduleType">
                        <el-radio-button label="interval">{{ $t('job_center.intervalSchedule') }}</el-radio-button>
                        <el-radio-button label="daily">{{ $t('job_center.dailySchedule') }}</el-radio-button>
                      </el-radio-group>
                    </el-form-item>
                  </el-col>
                  <el-col v-if="configForm.scheduleType === 'interval'" :xs="24" :md="8">
                    <el-form-item :label="$t('job_center.intervalMsLabel')">
                      <el-input-number v-model="configForm.intervalMilliseconds" :min="1" :step="1" :max="86400000" style="width: 100%" />
                    </el-form-item>
                  </el-col>
                  <el-col v-else :xs="24" :md="8">
                    <el-form-item :label="$t('job_center.dailyTimeLabel')">
                      <el-time-picker
                        v-model="configForm.dailyTime"
                        value-format="HH:mm:ss"
                        format="HH:mm:ss"
                        :placeholder="$t('job_center.pleaseSelectTime')"
                        style="width: 100%"
                      />
                    </el-form-item>
                  </el-col>
                  <el-col :xs="24" :md="4">
                    <el-form-item :label="$t('job_center.timeoutMsLabel')">
                      <el-input-number v-model="configForm.timeoutMilliseconds" :min="1000" :step="1000" :max="600000" style="width: 100%" />
                    </el-form-item>
                  </el-col>
                  <el-col :xs="24" :md="4">
                    <el-form-item :label="$t('job_center.retryLabel')">
                      <el-input-number v-model="configForm.retryCount" :min="0" :step="1" :max="5" style="width: 100%" />
                    </el-form-item>
                  </el-col>
                </el-row>

                <template v-if="currentTask.executorType === 'internal'">
                  <el-row :gutter="16">
                    <el-col :xs="24" :md="8">
                      <el-form-item :label="$t('job_center.projectIdsLabel')">
                        <el-input
                          v-model="configForm.projectIdsText"
                          :placeholder="$t('job_center.projectIdsPlaceholder')"
                          @change="loadStationGroups(configForm.projectIdsText, 'config')"
                        />
                      </el-form-item>
                    </el-col>
                    <el-col :xs="24" :md="8">
                      <el-form-item :label="$t('job_center.stationModeLabel')">
                        <el-input v-model="configForm.stationMode" placeholder="AUTO" />
                      </el-form-item>
                    </el-col>
                    <el-col :xs="24" :md="8">
                      <el-form-item :label="$t('job_center.resetCacheLabel')">
                        <el-switch v-model="configForm.resetCacheBeforeCollect" />
                      </el-form-item>
                    </el-col>
                  </el-row>

                  <div class="station-group-band">
                    <div class="station-group-header">
                      <div>
                        <div class="station-group-title">{{ $t('job_center.stationGroupTitle') }}</div>
                        <div class="station-group-subtitle">
                          {{ getStationSelectionText('config') }}{{ $t('job_center.stationGroupSubtitleSuffix') }}
                        </div>
                      </div>
                    </div>
                    <div class="station-group-grid">
                      <button
                        type="button"
                        class="station-card"
                        :class="{ active: isAllStationsSelected('config') }"
                        @click="toggleAllStations('config')"
                      >
                        <div class="station-card-title">{{ $t('job_center.allStations') }}</div>
                        <div class="station-card-meta">{{ $t('job_center.allStationsMeta') }}</div>
                        <div class="station-card-hint">{{ $t('job_center.allStationsHint') }}</div>
                      </button>
                      <button
                        v-for="group in configStationGroups"
                        :key="`config-${group.stationCode}`"
                        type="button"
                        class="station-card"
                        :class="{ active: isStationSelected('config', group.stationCode) }"
                        @click="toggleStationSelection('config', group.stationCode)"
                      >
                        <div class="station-card-title">{{ group.stationName || group.stationCode }}</div>
                        <div class="station-card-meta">
                          {{ $t('job_center.stationCardMeta', { points: group.pointCount, projects: group.projectCount }) }}
                        </div>
                        <div class="station-card-hint">
                          {{ group.tableNames?.length ? group.tableNames.join(' / ') : group.stationCode }}
                        </div>
                      </button>
                    </div>
                  </div>
                </template>

                <template v-else>
                  <el-row :gutter="16">
                    <el-col :xs="24" :md="10">
                      <el-form-item :label="$t('job_center.requestUrlLabel')">
                        <el-input v-model="configForm.requestUrl" :placeholder="$t('job_center.requestUrlPlaceholder')" />
                      </el-form-item>
                    </el-col>
                    <el-col :xs="24" :md="4">
                      <el-form-item :label="$t('job_center.httpMethodLabel')">
                        <el-select v-model="configForm.httpMethod" style="width: 100%">
                          <el-option label="GET" value="GET" />
                          <el-option label="POST" value="POST" />
                          <el-option label="PUT" value="PUT" />
                          <el-option label="DELETE" value="DELETE" />
                        </el-select>
                      </el-form-item>
                    </el-col>
                    <el-col :xs="24" :md="10">
                      <el-form-item label="Content-Type">
                        <el-input v-model="configForm.contentType" placeholder="application/json" />
                      </el-form-item>
                    </el-col>
                  </el-row>

                  <el-row :gutter="16">
                    <el-col :xs="24" :md="8">
                      <el-form-item :label="$t('job_center.headersJsonLabel')">
                        <el-input v-model="configForm.headersJson" type="textarea" :rows="6" placeholder='{"Authorization":"Bearer xxx"}' />
                      </el-form-item>
                    </el-col>
                    <el-col :xs="24" :md="8">
                      <el-form-item :label="$t('job_center.queryJsonLabel')">
                        <el-input v-model="configForm.queryJson" type="textarea" :rows="6" placeholder='{"projectId":"123"}' />
                      </el-form-item>
                    </el-col>
                    <el-col :xs="24" :md="8">
                      <el-form-item :label="$t('job_center.bodyJsonLabel')">
                        <el-input v-model="configForm.bodyJson" type="textarea" :rows="6" placeholder='{"name":"demo"}' />
                      </el-form-item>
                    </el-col>
                  </el-row>
                </template>
              </el-form>

              <div class="action-row">
                <el-button type="primary" :loading="saveLoading" @click="handleSave">
                  {{ $t('job_center.saveConfig') }}
                </el-button>
                <el-button type="success" :loading="runLoading" @click="handleRunNow(false)">
                  {{ $t('job_center.runNow') }}
                </el-button>
                <el-button
                  v-if="currentTask.taskType === 'cylinder_signal_collector'"
                  type="danger"
                  plain
                  :loading="clearCacheLoading"
                  @click="handleClearCache(false)"
                >
                  {{ $t('job_center.clearRangeCache') }}
                </el-button>
                <el-button
                  v-if="currentTask.taskType === 'cylinder_signal_collector'"
                  type="danger"
                  :loading="clearCacheLoading"
                  @click="handleClearCache(true)"
                >
                  {{ $t('job_center.clearAllCache') }}
                </el-button>
                <el-button
                  v-if="currentTask.taskType === 'cylinder_signal_collector'"
                  type="warning"
                  plain
                  :loading="runLoading"
                  @click="handleRunNow(true)"
                >
                  {{ $t('job_center.simulateRun') }}
                </el-button>
              </div>
            </div>
          </template>

          <div v-else class="empty-state">
            {{ $t('job_center.emptyState') }}
          </div>
        </section>
      </div>
    </FaPageMain>

    <el-dialog v-model="createVisible" :title="$t('job_center.createDialogTitle')" width="720px">
      <el-form label-position="top" class="create-form">
        <el-form-item :label="$t('job_center.executorTypeLabel')">
          <el-radio-group v-model="createForm.executorType">
            <el-radio-button label="http">{{ $t('job_center.httpTask') }}</el-radio-button>
            <el-radio-button label="internal">{{ $t('job_center.internalTask') }}</el-radio-button>
          </el-radio-group>
        </el-form-item>

        <el-form-item :label="$t('job_center.taskName')">
          <el-input v-model="createForm.taskName" :placeholder="$t('job_center.taskNamePlaceholder')" />
        </el-form-item>

        <el-form-item :label="$t('job_center.enabledLabel')">
          <el-switch v-model="createForm.enabled" />
        </el-form-item>

        <el-form-item :label="$t('job_center.scheduleMode')">
          <el-radio-group v-model="createForm.scheduleType">
            <el-radio-button label="interval">{{ $t('job_center.intervalSchedule') }}</el-radio-button>
            <el-radio-button label="daily">{{ $t('job_center.dailySchedule') }}</el-radio-button>
          </el-radio-group>
        </el-form-item>

        <el-form-item v-if="createForm.scheduleType === 'interval'" :label="$t('job_center.intervalMsLabel')">
          <el-input-number v-model="createForm.intervalMilliseconds" :min="1" :step="1" :max="86400000" style="width: 100%" />
        </el-form-item>
        <el-form-item v-else :label="$t('job_center.dailyTimeLabel')">
          <el-time-picker
            v-model="createForm.dailyTime"
            value-format="HH:mm:ss"
            format="HH:mm:ss"
            :placeholder="$t('job_center.pleaseSelectTime')"
            style="width: 100%"
          />
        </el-form-item>

        <el-form-item :label="$t('job_center.timeoutMsLabel')">
          <el-input-number v-model="createForm.timeoutMilliseconds" :min="1000" :step="1000" :max="600000" style="width: 100%" />
        </el-form-item>

        <el-form-item :label="$t('job_center.retryCountLabel')">
          <el-input-number v-model="createForm.retryCount" :min="0" :step="1" :max="5" style="width: 100%" />
        </el-form-item>

        <template v-if="createForm.executorType === 'internal'">
          <el-form-item :label="$t('job_center.projectIdsLabel')">
            <el-input
              v-model="createForm.projectIdsText"
              :placeholder="$t('job_center.projectIdsPlaceholder')"
              @change="loadStationGroups(createForm.projectIdsText, 'create')"
            />
          </el-form-item>
          <el-alert
            :title="$t('job_center.internalTaskTip')"
            type="info"
            :closable="false"
            class="create-form-tip"
          />
          <div class="station-group-band">
            <div class="station-group-header">
              <div>
                <div class="station-group-title">{{ $t('job_center.stationGroupTitle') }}</div>
                <div class="station-group-subtitle">
                  {{ getStationSelectionText('create') }}{{ $t('job_center.stationGroupSubtitleSuffix') }}
                </div>
              </div>
            </div>
            <div class="station-group-grid">
              <button
                type="button"
                class="station-card"
                :class="{ active: isAllStationsSelected('create') }"
                @click="toggleAllStations('create')"
              >
                <div class="station-card-title">{{ $t('job_center.allStations') }}</div>
                <div class="station-card-meta">{{ $t('job_center.allStationsMeta') }}</div>
                <div class="station-card-hint">{{ $t('job_center.allStationsHint') }}</div>
              </button>
              <button
                v-for="group in createStationGroups"
                :key="`create-${group.stationCode}`"
                type="button"
                class="station-card"
                :class="{ active: isStationSelected('create', group.stationCode) }"
                @click="toggleStationSelection('create', group.stationCode)"
              >
                <div class="station-card-title">{{ group.stationName || group.stationCode }}</div>
                <div class="station-card-meta">
                  {{ $t('job_center.stationCardMeta', { points: group.pointCount, projects: group.projectCount }) }}
                </div>
                <div class="station-card-hint">
                  {{ group.tableNames?.length ? group.tableNames.join(' / ') : group.stationCode }}
                </div>
              </button>
            </div>
          </div>
          <el-form-item :label="$t('job_center.stationModeLabel')">
            <el-input v-model="createForm.stationMode" placeholder="AUTO" />
          </el-form-item>
          <el-form-item :label="$t('job_center.resetCacheLabel')">
            <el-switch v-model="createForm.resetCacheBeforeCollect" />
          </el-form-item>
        </template>

        <template v-else>
          <el-form-item :label="$t('job_center.requestUrlLabel')">
            <el-input v-model="createForm.requestUrl" :placeholder="$t('job_center.requestUrlPlaceholder')" />
          </el-form-item>
          <el-form-item :label="$t('job_center.httpMethodLabel')">
            <el-select v-model="createForm.httpMethod" style="width: 100%">
              <el-option label="GET" value="GET" />
              <el-option label="POST" value="POST" />
              <el-option label="PUT" value="PUT" />
              <el-option label="DELETE" value="DELETE" />
            </el-select>
          </el-form-item>
          <el-form-item label="Content-Type">
            <el-input v-model="createForm.contentType" placeholder="application/json" />
          </el-form-item>
          <el-form-item :label="$t('job_center.headersJsonLabel')">
            <el-input v-model="createForm.headersJson" type="textarea" :rows="5" placeholder='{"Authorization":"Bearer xxx"}' />
          </el-form-item>
          <el-form-item :label="$t('job_center.queryJsonLabel')">
            <el-input v-model="createForm.queryJson" type="textarea" :rows="5" placeholder='{"projectId":"123"}' />
          </el-form-item>
          <el-form-item :label="$t('job_center.bodyJsonLabel')">
            <el-input v-model="createForm.bodyJson" type="textarea" :rows="5" placeholder='{"name":"demo"}' />
          </el-form-item>
        </template>
      </el-form>

      <template #footer>
        <el-button @click="createVisible = false">
          {{ $t('common.cancel') }}
        </el-button>
        <el-button type="primary" :loading="createLoading" @click="handleCreate">
          {{ $t('job_center.createTask') }}
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.header-description,
.panel-subtitle,
.job-meta,
.job-desc,
.job-time,
.message-band,
.empty-state {
  font-size: 13px;
  color: var(--el-text-color-secondary);
}

.header-actions,
.detail-header-actions,
.action-row {
  display: flex;
  gap: 12px;
  align-items: center;
}

.job-center-layout {
  display: grid;
  grid-template-columns: 340px minmax(0, 1fr);
  gap: 16px;
}

.panel {
  background: var(--el-bg-color);
  border: 1px solid var(--el-border-color-light);
  border-radius: 8px;
  padding: 16px;
}

.panel-title-row,
.detail-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 12px;
}

.panel-title {
  font-size: 16px;
  font-weight: 600;
  color: var(--el-text-color-primary);
}

.job-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
  margin-top: 12px;
}

.job-list-item {
  width: 100%;
  text-align: left;
  border: 1px solid var(--el-border-color-light);
  background: var(--el-fill-color-blank);
  border-radius: 8px;
  padding: 12px;
  cursor: pointer;
}

.job-list-item.active {
  border-color: var(--el-color-primary);
  box-shadow: 0 0 0 1px var(--el-color-primary-light-7) inset;
}

.job-list-top {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 12px;
}

.job-name {
  font-size: 15px;
  font-weight: 600;
  color: var(--el-text-color-primary);
}

.job-meta {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  margin-top: 8px;
}

.job-desc,
.job-time {
  margin-top: 8px;
}

.detail-panel {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.summary-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
  gap: 12px;
}

.metric-tile {
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 8px;
  padding: 12px;
}

.metric-label {
  font-size: 12px;
  color: var(--el-text-color-secondary);
  margin-bottom: 8px;
}

.metric-value {
  font-size: 17px;
  color: var(--el-text-color-primary);
  line-height: 1.4;
}

.message-band,
.config-band {
  border: 1px solid var(--el-border-color-light);
  border-radius: 8px;
  padding: 16px;
}

.station-group-band {
  border-top: 1px solid var(--el-border-color-lighter);
  margin-top: 4px;
  padding-top: 16px;
}

.station-group-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 12px;
  margin-bottom: 12px;
}

.station-group-title {
  font-size: 14px;
  font-weight: 600;
  color: var(--el-text-color-primary);
}

.station-group-subtitle,
.station-card-meta,
.station-card-hint {
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.station-group-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 12px;
}

.station-card {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 6px;
  min-height: 104px;
  padding: 12px;
  border: 1px solid var(--el-border-color-light);
  border-radius: 8px;
  background: var(--el-fill-color-blank);
  text-align: left;
  cursor: pointer;
  transition: border-color 0.2s ease, box-shadow 0.2s ease, background-color 0.2s ease;
}

.station-card:hover {
  border-color: var(--el-color-primary-light-5);
}

.station-card.active {
  border-color: var(--el-color-primary);
  background: var(--el-color-primary-light-9);
  box-shadow: 0 0 0 1px var(--el-color-primary-light-7) inset;
}

.station-card-title {
  font-size: 14px;
  font-weight: 600;
  color: var(--el-text-color-primary);
  line-height: 1.4;
  word-break: break-word;
}

.station-card-hint {
  line-height: 1.5;
  word-break: break-word;
}

.create-form :deep(.el-input-number) {
  width: 100%;
}

.create-form-tip {
  margin-bottom: 16px;
}

.empty-state {
  min-height: 420px;
  display: flex;
  align-items: center;
  justify-content: center;
}

@media (max-width: 960px) {
  .job-center-layout {
    grid-template-columns: 1fr;
  }

  .panel-title-row,
  .detail-header {
    flex-direction: column;
  }
}
</style>
