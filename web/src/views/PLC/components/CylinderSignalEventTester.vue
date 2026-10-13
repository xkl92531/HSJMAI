<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import CylinderSignalEventAPI from '@/api/modules/PLC/cylinderSignalEvent'
import messages from './cylinderSignalEventTester.lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

type CompareItem = {
  stationCode?: string
  cylinderId?: string
  signalName?: string
  signalRole?: string
  oldValue?: string | null
  currentValue?: string | null
  isChanged?: boolean
  isMissing?: boolean
  compareStatus?: string
}

type CollectResult = {
  stationCode?: string
  pointCount?: number
  readCount?: number
  changedCount?: number
  insertedCount?: number
  missingSignals?: string[]
  insertedEvents?: any[]
  comparedSignals?: CompareItem[]
}

type CollectAllProjectResult = {
  projectId: string | number
  projectName?: string
  plcId?: string
  stationCode?: string
  isSuccess?: boolean
  errorMessage?: string
  pointCount?: number
  readCount?: number
  changedCount?: number
  insertedCount?: number
}

type CollectAllResult = {
  triggeredAt?: string
  triggerSource?: string
  projectCount?: number
  successProjectCount?: number
  failedProjectCount?: number
  totalPointCount?: number
  totalReadCount?: number
  totalChangedCount?: number
  totalInsertedCount?: number
  projectResults?: CollectAllProjectResult[]
}

type JobConfig = {
  enabled: boolean
  intervalMilliseconds: number
  stationMode: string
  resetCacheBeforeCollect: boolean
  lastRunAt?: string
  lastRunStatus?: string
  lastMessage?: string
  lastProjectCount?: number
  lastChangedCount?: number
  lastInsertedCount?: number
}

const props = defineProps<{
  projectId?: string | number
}>()

const isGlobalMode = computed(() => !props.projectId)
const hasProject = computed(() => !!props.projectId)

const collectLoading = ref(false)
const listLoading = ref(false)
const jobLoading = ref(false)
const saveJobLoading = ref(false)
const runAllLoading = ref(false)

const collectResult = ref<CollectResult | null>(null)
const collectAllResult = ref<CollectAllResult | null>(null)
const parseError = ref('')

const collectForm = ref({
  stationCode: '',
  stationMode: 'AUTO',
  resetCacheBeforeCollect: false,
  useSimulation: false,
  simulatedValuesText: '{\n  "OP080_YV16_W": true,\n  "OP080_CYL16_W": false\n}',
})

const jobForm = ref<JobConfig>({
  enabled: false,
  intervalMilliseconds: 30000,
  stationMode: 'AUTO',
  resetCacheBeforeCollect: false,
  lastRunAt: '',
  lastRunStatus: '',
  lastMessage: '',
  lastProjectCount: 0,
  lastChangedCount: 0,
  lastInsertedCount: 0,
})

const compareFilter = ref({
  onlyChanged: false,
  onlyMissing: false,
})

const filterForm = ref({
  plcId: '',
  stationCode: '',
  cylinderId: '',
  signalName: '',
  signalRole: '',
})

const pagination = ref({
  page: 1,
  limit: 20,
  total: 0,
})

const rows = ref<any[]>([])

const signalRoleOptions = [
  { label: t('cylinderSignalEventTester.all'), value: '' },
  { label: 'extend_valve', value: 'extend_valve' },
  { label: 'retract_valve', value: 'retract_valve' },
  { label: 'common_valve', value: 'common_valve' },
  { label: 'work_sensor', value: 'work_sensor' },
  { label: 'home_sensor', value: 'home_sensor' },
  { label: 'unknown', value: 'unknown' },
]

const comparedSignalRows = computed(() => {
  let list = collectResult.value?.comparedSignals || []
  if (compareFilter.value.onlyChanged)
    list = list.filter(item => item.isChanged)
  if (compareFilter.value.onlyMissing)
    list = list.filter(item => item.isMissing)
  return list
})

function parseSimulationValues() {
  parseError.value = ''
  if (!collectForm.value.useSimulation)
    return undefined

  const text = collectForm.value.simulatedValuesText.trim()
  if (!text)
    return {}

  try {
    return JSON.parse(text)
  }
  catch (error: any) {
    parseError.value = error?.message || t('cylinderSignalEventTester.simJsonParseFailed')
    throw error
  }
}

function formatTime(value?: string) {
  if (!value)
    return '-'
  const date = new Date(value)
  if (Number.isNaN(date.getTime()))
    return value
  return date.toLocaleString('zh-CN', { hour12: false })
}

function getCompareStatusText(row: CompareItem) {
  if (row.isMissing)
    return t('cylinderSignalEventTester.statusMissing')
  if (row.compareStatus === 'first_persist')
    return t('cylinderSignalEventTester.statusFirstPersist')
  if (row.compareStatus === 'first_seen')
    return t('cylinderSignalEventTester.statusFirstSeen')
  if (row.isChanged)
    return t('cylinderSignalEventTester.statusChanged')
  return t('cylinderSignalEventTester.statusUnchanged')
}

function getCompareTagType(row: CompareItem) {
  if (row.isMissing)
    return 'danger'
  if (row.compareStatus === 'first_persist')
    return 'primary'
  if (row.compareStatus === 'first_seen')
    return 'warning'
  if (row.isChanged)
    return 'success'
  return 'info'
}

function getJobStatusText(status?: string) {
  if (status === 'success')
    return t('cylinderSignalEventTester.statusSuccess')
  if (status === 'partial_success')
    return t('cylinderSignalEventTester.statusPartial')
  if (status === 'failed')
    return t('cylinderSignalEventTester.statusFailed')
  return t('cylinderSignalEventTester.statusNotRun')
}

function getJobStatusType(status?: string) {
  if (status === 'success')
    return 'success'
  if (status === 'partial_success')
    return 'warning'
  if (status === 'failed')
    return 'danger'
  return 'info'
}

async function fetchJobConfig() {
  if (!isGlobalMode.value)
    return

  jobLoading.value = true
  try {
    const res: any = await CylinderSignalEventAPI.getJobConfig()
    jobForm.value = {
      enabled: !!res.enabled,
      intervalMilliseconds: res.intervalMilliseconds || ((res.intervalSeconds || 30) * 1000),
      stationMode: res.stationMode || 'AUTO',
      resetCacheBeforeCollect: !!res.resetCacheBeforeCollect,
      lastRunAt: res.lastRunAt || '',
      lastRunStatus: res.lastRunStatus || '',
      lastMessage: res.lastMessage || '',
      lastProjectCount: res.lastProjectCount || 0,
      lastChangedCount: res.lastChangedCount || 0,
      lastInsertedCount: res.lastInsertedCount || 0,
    }
  }
  catch (error: any) {
    ElMessage.error(error?.message || t('cylinderSignalEventTester.loadJobConfigFailed'))
  }
  finally {
    jobLoading.value = false
  }
}

async function handleSaveJobConfig() {
  saveJobLoading.value = true
  try {
    const payload = {
      ...jobForm.value,
      intervalMilliseconds: Math.max(1, Number(jobForm.value.intervalMilliseconds) || 30000),
    }
    const res: any = await CylinderSignalEventAPI.saveJobConfig(payload)
    jobForm.value = {
      ...jobForm.value,
      ...(res.data || payload),
    }
    ElMessage.success(t('cylinderSignalEventTester.jobConfigSaved'))
  }
  catch (error: any) {
    ElMessage.error(error?.message || t('cylinderSignalEventTester.saveJobConfigFailed'))
  }
  finally {
    saveJobLoading.value = false
  }
}

async function handleRunAll(autoGenerateSimulationValues = false) {
  runAllLoading.value = true
  try {
    const res: any = await CylinderSignalEventAPI.collectAllOnce({
      stationMode: jobForm.value.stationMode || 'AUTO',
      resetCacheBeforeCollect: jobForm.value.resetCacheBeforeCollect,
      useSimulation: autoGenerateSimulationValues,
      autoGenerateSimulationValues,
      triggerSource: autoGenerateSimulationValues ? 'ManualSimulation' : 'Manual',
    })

    collectAllResult.value = res.data || null
    await fetchJobConfig()
    await fetchList()
    ElMessage.success(t('cylinderSignalEventTester.collectAllDone', { count: collectAllResult.value?.totalInsertedCount || 0 }))
  }
  catch (error: any) {
    ElMessage.error(error?.message || t('cylinderSignalEventTester.collectAllFailed'))
  }
  finally {
    runAllLoading.value = false
  }
}

async function handleGenerateSimulationFromCache() {
  if (!hasProject.value) {
    ElMessage.warning(t('cylinderSignalEventTester.useSimCollectHint'))
    return false
  }

  try {
    const res: any = await CylinderSignalEventAPI.generateSimulationValues({
      projectId: props.projectId!,
      stationCode: collectForm.value.stationCode || undefined,
    })

    const payload = res.data?.simulatedValues || {}
    if (!Object.keys(payload).length) {
      ElMessage.warning(t('cylinderSignalEventTester.noSimValues'))
      return false
    }

    collectForm.value.useSimulation = true
    collectForm.value.resetCacheBeforeCollect = false
    collectForm.value.simulatedValuesText = JSON.stringify(payload, null, 2)
    parseError.value = ''
    ElMessage.success(t('cylinderSignalEventTester.simValuesGenerated', { count: Object.keys(payload).length }))
    return true
  }
  catch (error: any) {
    ElMessage.error(error?.message || t('cylinderSignalEventTester.generateSimFailed'))
    return false
  }
}

async function handleGenerateAndCollect() {
  const ok = await handleGenerateSimulationFromCache()
  if (!ok)
    return
  await handleCollect()
}

async function handleCollect() {
  if (!hasProject.value) {
    ElMessage.warning(t('cylinderSignalEventTester.noProjectSpecified'))
    return
  }

  let simulatedValues: Record<string, unknown> | undefined
  try {
    simulatedValues = parseSimulationValues()
  }
  catch {
    ElMessage.error(t('cylinderSignalEventTester.simJsonInvalid'))
    return
  }

  collectLoading.value = true
  try {
    const res: any = await CylinderSignalEventAPI.collectOnce({
      projectId: props.projectId!,
      stationCode: collectForm.value.stationCode || undefined,
      stationMode: collectForm.value.stationMode || undefined,
      resetCacheBeforeCollect: collectForm.value.resetCacheBeforeCollect,
      useSimulation: collectForm.value.useSimulation,
      simulatedValues,
    })

    collectResult.value = res.data || null
    ElMessage.success(t('cylinderSignalEventTester.collectDone', { changed: collectResult.value?.changedCount || 0, inserted: collectResult.value?.insertedCount || 0 }))
    pagination.value.page = 1
    await fetchList()
  }
  catch (error: any) {
    ElMessage.error(error?.message || t('cylinderSignalEventTester.collectFailed'))
  }
  finally {
    collectLoading.value = false
  }
}

async function fetchList() {
  listLoading.value = true
  try {
    const res: any = await CylinderSignalEventAPI.list({
      page: pagination.value.page,
      limit: pagination.value.limit,
      projectId: hasProject.value ? props.projectId! : undefined,
      plcId: isGlobalMode.value ? (filterForm.value.plcId || undefined) : undefined,
      stationCode: filterForm.value.stationCode || undefined,
      cylinderId: filterForm.value.cylinderId || undefined,
      signalName: filterForm.value.signalName || undefined,
      signalRole: filterForm.value.signalRole || undefined,
    })
    rows.value = res.data || []
    pagination.value.total = res.total || 0
  }
  catch (error: any) {
    ElMessage.error(error?.message || t('cylinderSignalEventTester.loadListFailed'))
  }
  finally {
    listLoading.value = false
  }
}

function handleSearch() {
  pagination.value.page = 1
  fetchList()
}

function handleResetFilter() {
  filterForm.value = {
    plcId: '',
    stationCode: '',
    cylinderId: '',
    signalName: '',
    signalRole: '',
  }
  pagination.value.page = 1
  fetchList()
}

function handlePageChange(page: number) {
  pagination.value.page = page
  fetchList()
}

watch(
  () => props.projectId,
  async () => {
    collectResult.value = null
    collectAllResult.value = null
    pagination.value.page = 1
    if (isGlobalMode.value)
      await fetchJobConfig()
    await fetchList()
  },
  { immediate: true },
)
</script>

<template>
  <div class="cylinder-event-workbench">
    <div class="toolbar-band">
      <div class="toolbar-title">
        <div class="title-text">{{ isGlobalMode ? $t('cylinderSignalEventTester.globalTitle') : $t('cylinderSignalEventTester.title') }}</div>
        <div class="title-subtext">
          {{ isGlobalMode ? $t('cylinderSignalEventTester.globalModeSubtext') : $t('cylinderSignalEventTester.projectSubtext', { id: projectId || '-' }) }}
        </div>
      </div>
      <div class="toolbar-actions">
        <el-button
          v-if="isGlobalMode"
          type="primary"
          :loading="runAllLoading"
          @click="handleRunAll(false)"
        >
          {{ $t('cylinderSignalEventTester.runAllNow') }}
        </el-button>
        <el-button
          v-if="isGlobalMode"
          type="success"
          plain
          :loading="runAllLoading"
          @click="handleRunAll(true)"
        >
          {{ $t('cylinderSignalEventTester.simCollectAll') }}
        </el-button>
        <el-button
          v-else
          type="primary"
          :loading="collectLoading"
          :disabled="!hasProject"
          @click="handleCollect"
        >
          {{ $t('cylinderSignalEventTester.runCollect') }}
        </el-button>
      </div>
    </div>

    <div v-if="isGlobalMode" class="section-band" v-loading="jobLoading">
      <div class="section-header">
        <div class="panel-title">{{ $t('cylinderSignalEventTester.jobConfigTitle') }}</div>
        <div class="panel-subtext">{{ $t('cylinderSignalEventTester.jobConfigSubtext') }}</div>
      </div>

      <el-form :model="jobForm" label-width="140px" class="collect-form">
        <el-row :gutter="16">
          <el-col :xs="24" :md="6">
            <el-form-item :label="$t('cylinderSignalEventTester.jobEnabledLabel')">
              <el-switch v-model="jobForm.enabled" />
            </el-form-item>
          </el-col>
          <el-col :xs="24" :md="6">
              <el-form-item :label="$t('cylinderSignalEventTester.intervalLabel')">
              <el-input-number v-model="jobForm.intervalMilliseconds" :min="1" :max="86400000" :step="1" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :xs="24" :md="6">
            <el-form-item :label="$t('cylinderSignalEventTester.stationModeLabel')">
              <el-input v-model="jobForm.stationMode" placeholder="AUTO" clearable />
            </el-form-item>
          </el-col>
          <el-col :xs="24" :md="6">
            <el-form-item :label="$t('cylinderSignalEventTester.resetCacheLabel')">
              <el-switch v-model="jobForm.resetCacheBeforeCollect" />
            </el-form-item>
          </el-col>
        </el-row>

        <div class="simulation-action-row">
          <el-button type="primary" :loading="saveJobLoading" @click="handleSaveJobConfig">
            {{ $t('cylinderSignalEventTester.saveJobConfig') }}
          </el-button>
          <span class="action-tip">{{ $t('cylinderSignalEventTester.jobActionTip') }}</span>
        </div>
      </el-form>
    </div>

    <div v-if="isGlobalMode" class="section-band">
      <div class="summary-grid">
        <div class="metric-tile">
          <div class="metric-label">{{ $t('cylinderSignalEventTester.jobStatusLabel') }}</div>
          <div class="metric-value">
            <el-tag :type="jobForm.enabled ? 'success' : 'info'">{{ jobForm.enabled ? $t('common.enabled') : $t('cylinderSignalEventTester.jobNotEnabled') }}</el-tag>
          </div>
        </div>
        <div class="metric-tile">
          <div class="metric-label">{{ $t('cylinderSignalEventTester.lastRunLabel') }}</div>
          <div class="metric-value">{{ formatTime(jobForm.lastRunAt) }}</div>
        </div>
        <div class="metric-tile">
          <div class="metric-label">{{ $t('cylinderSignalEventTester.lastResultLabel') }}</div>
          <div class="metric-value">
            <el-tag :type="getJobStatusType(jobForm.lastRunStatus)">{{ getJobStatusText(jobForm.lastRunStatus) }}</el-tag>
          </div>
        </div>
        <div class="metric-tile">
          <div class="metric-label">{{ $t('cylinderSignalEventTester.lastProjectCountLabel') }}</div>
          <div class="metric-value">{{ jobForm.lastProjectCount || 0 }}</div>
        </div>
        <div class="metric-tile">
          <div class="metric-label">{{ $t('cylinderSignalEventTester.lastChangedCountLabel') }}</div>
          <div class="metric-value accent">{{ jobForm.lastChangedCount || 0 }}</div>
        </div>
        <div class="metric-tile">
          <div class="metric-label">{{ $t('cylinderSignalEventTester.lastInsertedCountLabel') }}</div>
          <div class="metric-value success">{{ jobForm.lastInsertedCount || 0 }}</div>
        </div>
      </div>
      <div class="form-help form-help-wrap">
        <span>{{ jobForm.lastMessage || $t('cylinderSignalEventTester.noRunRecords') }}</span>
      </div>
    </div>

    <div v-if="isGlobalMode && collectAllResult" class="section-band">
      <div class="section-header">
        <div class="panel-title">{{ $t('cylinderSignalEventTester.lastAllResultTitle') }}</div>
        <div class="panel-subtext">{{ $t('cylinderSignalEventTester.lastAllResultSubtext') }}</div>
      </div>

      <div class="summary-grid">
        <div class="metric-tile">
          <div class="metric-label">{{ $t('cylinderSignalEventTester.triggeredAtLabel') }}</div>
          <div class="metric-value">{{ formatTime(collectAllResult.triggeredAt) }}</div>
        </div>
        <div class="metric-tile">
          <div class="metric-label">{{ $t('cylinderSignalEventTester.triggerSourceLabel') }}</div>
          <div class="metric-value">{{ collectAllResult.triggerSource || '-' }}</div>
        </div>
        <div class="metric-tile">
          <div class="metric-label">{{ $t('cylinderSignalEventTester.projectCountLabel') }}</div>
          <div class="metric-value">{{ collectAllResult.projectCount || 0 }}</div>
        </div>
        <div class="metric-tile">
          <div class="metric-label">{{ $t('cylinderSignalEventTester.successProjectsLabel') }}</div>
          <div class="metric-value success">{{ collectAllResult.successProjectCount || 0 }}</div>
        </div>
        <div class="metric-tile">
          <div class="metric-label">{{ $t('cylinderSignalEventTester.failedProjectsLabel') }}</div>
          <div class="metric-value warning">{{ collectAllResult.failedProjectCount || 0 }}</div>
        </div>
        <div class="metric-tile">
          <div class="metric-label">{{ $t('cylinderSignalEventTester.totalInsertedLabel') }}</div>
          <div class="metric-value accent">{{ collectAllResult.totalInsertedCount || 0 }}</div>
        </div>
      </div>

      <el-table :data="collectAllResult.projectResults || []" max-height="420" stripe>
        <el-table-column prop="projectId" :label="$t('cylinderSignalEventTester.projectIdLabel')" width="180" />
        <el-table-column prop="projectName" :label="$t('cylinderSignalEventTester.projectNameLabel')" min-width="160" show-overflow-tooltip />
        <el-table-column prop="stationCode" :label="$t('cylinderSignalEventTester.stationLabel')" width="100" />
        <el-table-column prop="pointCount" :label="$t('cylinderSignalEventTester.pointCountLabel')" width="110" />
        <el-table-column prop="readCount" :label="$t('cylinderSignalEventTester.readCountLabel')" width="90" />
        <el-table-column prop="changedCount" :label="$t('cylinderSignalEventTester.changedCountLabel')" width="90" />
        <el-table-column prop="insertedCount" :label="$t('cylinderSignalEventTester.insertedCountLabel')" width="90" />
        <el-table-column :label="$t('cylinderSignalEventTester.resultLabel')" width="110">
          <template #default="{ row }">
            <el-tag :type="row.isSuccess ? 'success' : 'danger'">{{ row.isSuccess ? $t('cylinderSignalEventTester.statusSuccess') : $t('cylinderSignalEventTester.statusFailed') }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="errorMessage" :label="$t('cylinderSignalEventTester.errorMessageLabel')" min-width="180" show-overflow-tooltip />
      </el-table>
    </div>

    <div v-if="!isGlobalMode" class="section-band">
      <el-form :model="collectForm" label-width="140px" class="collect-form">
        <el-row :gutter="16">
          <el-col :xs="24" :md="8">
            <el-form-item :label="$t('cylinderSignalEventTester.stationCodeLabel')">
              <el-input v-model="collectForm.stationCode" :placeholder="$t('cylinderSignalEventTester.stationCodePlaceholder')" clearable />
            </el-form-item>
          </el-col>
          <el-col :xs="24" :md="6">
            <el-form-item :label="$t('cylinderSignalEventTester.stationModeLabel')">
              <el-input v-model="collectForm.stationMode" placeholder="AUTO" clearable />
            </el-form-item>
          </el-col>
          <el-col :xs="24" :md="5">
            <el-form-item :label="$t('cylinderSignalEventTester.resetRedisLabel')">
              <el-switch v-model="collectForm.resetCacheBeforeCollect" />
            </el-form-item>
          </el-col>
          <el-col :xs="24" :md="5">
            <el-form-item :label="$t('cylinderSignalEventTester.useSimulationLabel')">
              <el-switch v-model="collectForm.useSimulation" />
            </el-form-item>
          </el-col>
        </el-row>

        <div class="simulation-action-row">
          <el-button type="warning" plain @click="handleGenerateSimulationFromCache">
            {{ $t('cylinderSignalEventTester.generateSimFromCache') }}
          </el-button>
          <el-button type="success" plain @click="handleGenerateAndCollect">
            {{ $t('cylinderSignalEventTester.generateAndCollect') }}
          </el-button>
          <span class="action-tip">{{ $t('cylinderSignalEventTester.singleProjectTip') }}</span>
        </div>

        <div class="form-help">
          <span>{{ $t('cylinderSignalEventTester.oldValueTip') }}</span>
        </div>

        <el-form-item v-if="collectForm.useSimulation" :label="$t('cylinderSignalEventTester.simJsonLabel')">
          <el-input
            v-model="collectForm.simulatedValuesText"
            type="textarea"
            :rows="10"
            placeholder='{"OP080_YV16_W": true}'
          />
          <div class="form-help form-help-wrap">
            <span>{{ $t('cylinderSignalEventTester.simJsonHelp') }}</span>
            <span v-if="parseError" class="parse-error">{{ parseError }}</span>
          </div>
        </el-form-item>
      </el-form>
    </div>

    <div v-if="!isGlobalMode && collectResult" class="section-band">
      <div class="summary-grid">
        <div class="metric-tile">
          <div class="metric-label">{{ $t('cylinderSignalEventTester.returnedStationLabel') }}</div>
          <div class="metric-value">{{ collectResult.stationCode || '-' }}</div>
        </div>
        <div class="metric-tile">
          <div class="metric-label">{{ $t('cylinderSignalEventTester.pointCountLabel') }}</div>
          <div class="metric-value">{{ collectResult.pointCount ?? 0 }}</div>
        </div>
        <div class="metric-tile">
          <div class="metric-label">{{ $t('cylinderSignalEventTester.readTotalLabel') }}</div>
          <div class="metric-value">{{ collectResult.readCount ?? 0 }}</div>
        </div>
        <div class="metric-tile">
          <div class="metric-label">{{ $t('cylinderSignalEventTester.changedTotalLabel') }}</div>
          <div class="metric-value accent">{{ collectResult.changedCount ?? 0 }}</div>
        </div>
        <div class="metric-tile">
          <div class="metric-label">{{ $t('cylinderSignalEventTester.insertedTotalLabel') }}</div>
          <div class="metric-value success">{{ collectResult.insertedCount ?? 0 }}</div>
        </div>
        <div class="metric-tile">
          <div class="metric-label">{{ $t('cylinderSignalEventTester.missingSignalsLabel') }}</div>
          <div class="metric-value warning">{{ collectResult.missingSignals?.length ?? 0 }}</div>
        </div>
      </div>
    </div>

    <div v-if="!isGlobalMode && collectResult" class="section-band">
      <div class="section-header">
        <div class="panel-title">{{ $t('cylinderSignalEventTester.compareDetailTitle') }}</div>
        <div class="panel-subtext">{{ $t('cylinderSignalEventTester.compareDetailSubtext') }}</div>
      </div>

      <div class="filter-bar compare-filter-bar">
        <el-checkbox v-model="compareFilter.onlyChanged">{{ $t('cylinderSignalEventTester.onlyChanged') }}</el-checkbox>
        <el-checkbox v-model="compareFilter.onlyMissing">{{ $t('cylinderSignalEventTester.onlyMissing') }}</el-checkbox>
      </div>

      <el-table :data="comparedSignalRows" max-height="420" stripe>
        <el-table-column prop="stationCode" :label="$t('cylinderSignalEventTester.stationLabel')" width="90" />
        <el-table-column prop="cylinderId" label="IOTag ID" width="130" />
        <el-table-column prop="signalName" :label="$t('cylinderSignalEventTester.signalNameLabel')" min-width="220" show-overflow-tooltip />
        <el-table-column prop="signalRole" :label="$t('cylinderSignalEventTester.roleLabel')" width="130" />
        <el-table-column prop="oldValue" :label="$t('cylinderSignalEventTester.oldValueLabel')" width="100" />
        <el-table-column prop="currentValue" :label="$t('cylinderSignalEventTester.currentValueLabel')" width="100" />
        <el-table-column :label="$t('cylinderSignalEventTester.resultLabel')" width="110">
          <template #default="{ row }">
            <el-tag :type="getCompareTagType(row)">{{ getCompareStatusText(row) }}</el-tag>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <div v-if="!isGlobalMode && collectResult?.insertedEvents?.length" class="section-band">
      <div class="panel-title">{{ $t('cylinderSignalEventTester.insertedEventsTitle') }}</div>
      <el-table :data="collectResult.insertedEvents || []" max-height="320" stripe>
        <el-table-column prop="stationCode" :label="$t('cylinderSignalEventTester.stationLabel')" width="90" />
        <el-table-column prop="cylinderId" label="IOTag ID" width="120" />
        <el-table-column prop="signalName" :label="$t('cylinderSignalEventTester.signalNameLabel')" min-width="180" show-overflow-tooltip />
        <el-table-column prop="signalRole" :label="$t('cylinderSignalEventTester.roleLabel')" width="120" />
        <el-table-column prop="oldValue" :label="$t('cylinderSignalEventTester.prevValueLabel')" width="90" />
        <el-table-column prop="newValue" :label="$t('cylinderSignalEventTester.newValueLabel')" width="90" />
      </el-table>
    </div>

    <div class="section-band">
      <div class="section-header">
        <div class="panel-title">{{ $t('cylinderSignalEventTester.storedEventsTitle') }}</div>
        <div class="panel-subtext">{{ $t('cylinderSignalEventTester.storedEventsSubtext') }}</div>
      </div>

      <div class="filter-bar">
        <el-form :inline="true" :model="filterForm" @submit.prevent="handleSearch">
          <el-form-item v-if="isGlobalMode" label="PLC ID">
            <el-input v-model="filterForm.plcId" :placeholder="$t('cylinderSignalEventTester.plcIdFilterPlaceholder')" clearable style="width: 180px" />
          </el-form-item>
          <el-form-item :label="$t('cylinderSignalEventTester.stationLabel')">
            <el-input v-model="filterForm.stationCode" :placeholder="$t('cylinderSignalEventTester.stationPlaceholderExample')" clearable style="width: 140px" />
          </el-form-item>
          <el-form-item label="IOTag ID">
            <el-input v-model="filterForm.cylinderId" :placeholder="$t('cylinderSignalEventTester.iotagIdPlaceholder')" clearable style="width: 180px" />
          </el-form-item>
          <el-form-item :label="$t('cylinderSignalEventTester.signalNameLabel')">
            <el-input v-model="filterForm.signalName" :placeholder="$t('cylinderSignalEventTester.signalNamePlaceholder')" clearable style="width: 200px" />
          </el-form-item>
          <el-form-item :label="$t('cylinderSignalEventTester.roleLabel')">
            <el-select v-model="filterForm.signalRole" clearable style="width: 160px">
              <el-option v-for="item in signalRoleOptions" :key="item.value" :label="item.label" :value="item.value" />
            </el-select>
          </el-form-item>
          <el-form-item>
            <el-button type="primary" @click="handleSearch">{{ $t('cylinderSignalEventTester.query') }}</el-button>
            <el-button @click="handleResetFilter">{{ $t('common.reset') }}</el-button>
          </el-form-item>
        </el-form>
      </div>

      <el-table v-loading="listLoading" :data="rows" stripe>
        <el-table-column prop="id" label="ID" width="180" show-overflow-tooltip />
        <el-table-column prop="plcId" label="PLC ID" width="180" show-overflow-tooltip />
        <el-table-column prop="stationCode" :label="$t('cylinderSignalEventTester.stationLabel')" width="100" />
        <el-table-column prop="cylinderId" label="IOTag ID" width="140" show-overflow-tooltip />
        <el-table-column prop="signalName" :label="$t('cylinderSignalEventTester.signalNameLabel')" min-width="220" show-overflow-tooltip />
        <el-table-column prop="signalRole" :label="$t('cylinderSignalEventTester.roleLabel')" width="130" />
        <el-table-column prop="oldValue" :label="$t('cylinderSignalEventTester.prevValueLabel')" width="90" />
        <el-table-column prop="newValue" :label="$t('cylinderSignalEventTester.newValueLabel')" width="90" />
        <el-table-column prop="stationMode" :label="$t('cylinderSignalEventTester.modeLabel')" width="100" />
        <el-table-column :label="$t('cylinderSignalEventTester.changedAtLabel')" width="180">
          <template #default="{ row }">
            {{ formatTime(row.changedAt) }}
          </template>
        </el-table-column>
      </el-table>

      <div class="pagination-wrap">
        <el-pagination
          v-model:current-page="pagination.page"
          v-model:page-size="pagination.limit"
          :total="pagination.total"
          :page-sizes="[20, 50, 100, 200]"
          layout="total, sizes, prev, pager, next, jumper"
          @current-change="handlePageChange"
        />
      </div>
    </div>
  </div>
</template>

<style scoped>
.cylinder-event-workbench {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.toolbar-band,
.section-band {
  background: var(--el-bg-color);
  border: 1px solid var(--el-border-color-light);
  border-radius: 8px;
  padding: 16px;
}

.toolbar-band {
  display: flex;
  justify-content: space-between;
  gap: 16px;
  align-items: center;
}

.toolbar-actions {
  display: flex;
  gap: 12px;
  flex-wrap: wrap;
}

.toolbar-title {
  min-width: 0;
}

.title-text,
.panel-title {
  font-size: 16px;
  font-weight: 600;
  color: var(--el-text-color-primary);
}

.title-subtext,
.panel-subtext,
.form-help,
.action-tip {
  font-size: 13px;
  color: var(--el-text-color-secondary);
}

.section-header {
  margin-bottom: 12px;
}

.simulation-action-row {
  display: flex;
  gap: 12px;
  align-items: center;
  flex-wrap: wrap;
  margin-bottom: 12px;
}

.form-help-wrap {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-top: 8px;
}

.parse-error {
  color: var(--el-color-danger);
}

.summary-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
  gap: 12px;
}

.metric-tile {
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 8px;
  padding: 14px;
  min-height: 82px;
}

.metric-label {
  font-size: 12px;
  color: var(--el-text-color-secondary);
  margin-bottom: 8px;
}

.metric-value {
  font-size: 18px;
  color: var(--el-text-color-primary);
  word-break: break-all;
}

.metric-value.accent {
  color: var(--el-color-primary);
}

.metric-value.success {
  color: var(--el-color-success);
}

.metric-value.warning {
  color: var(--el-color-warning);
}

.filter-bar {
  margin-bottom: 12px;
}

.compare-filter-bar {
  display: flex;
  gap: 16px;
  align-items: center;
}

.pagination-wrap {
  display: flex;
  justify-content: flex-end;
  margin-top: 16px;
}
</style>
