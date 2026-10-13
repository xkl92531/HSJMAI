<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import StopCauseDiagnosisAPI from '@/api/modules/PLC/stopCauseDiagnosis'
import { useI18n } from 'vue-i18n'
import messages from './deviceFocusSignals.lang'

const props = defineProps<{
  projectId: string | number
}>()

const loading = ref(false)
const syncing = ref(false)
const keyword = ref('')
const stationCode = ref('')
const anchorType = ref('')
const activeStations = ref<string[]>([])
const stationOptions = ref<string[]>([])
const result = ref<any>({
  totalCount: 0,
  stationCount: 0,
  categoryCounts: {},
  stations: [],
  signals: [],
})

const projectIdValue = computed(() => String(props.projectId ?? '').trim())

const { t, mergeLocaleMessage } = useI18n()

// 遍历语言包并合并到当前实例
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const getPayload = (response: any) => response?.data ?? response?.backResult ?? response?.BackResult ?? response ?? {}

const ensureArray = (value: any): any[] => Array.isArray(value) ? value : []

const normalizeStationCode = (value: any) => String(value ?? '').trim()

const sortStationCodes = (values: any[]) => Array.from(new Set(
  values.map(normalizeStationCode).filter(Boolean),
)).sort((a, b) => a.localeCompare(b, 'zh-Hans-CN', { numeric: true }))

const mergeStationOptions = (values: any[]) => {
  stationOptions.value = sortStationCodes([
    ...stationOptions.value,
    ...values,
  ])
}

const collectStationCodesFromStationMap = (payload: any) => {
  const codes = [
    ...ensureArray(payload?.stations ?? payload?.Stations),
    ...ensureArray(payload?.stationBuckets ?? payload?.StationBuckets)
      .map(item => item?.stationCode ?? item?.StationCode),
    ...ensureArray(payload?.anchorAssignments ?? payload?.AnchorAssignments)
      .map(item => item?.stationCode ?? item?.StationCode),
  ]
  return sortStationCodes(codes)
}

const summaryItems = computed(() => [
  { label: t('deviceFocusSignals.focusSignals'), value: result.value?.totalCount || 0 },
  { label: t('deviceFocusSignals.coveredStations'), value: result.value?.stationCount || 0 },
  { label: t('deviceFocusSignals.rootFeedback'), value: (result.value?.categoryCounts?.DeviceMotionFeedback || 0) + (result.value?.categoryCounts?.DeviceHoldState || 0) },
  { label: t('deviceFocusSignals.alarmContext'), value: (result.value?.categoryCounts?.DeviceAlarmState || 0) + (result.value?.categoryCounts?.DeviceAnalogContext || 0) },
])

const anchorTypeOptions = computed(() => [
  { label: t('deviceFocusSignals.anchorAll'), value: '' },
  { label: t('deviceFocusSignals.anchorAlarm'), value: 'DeviceAlarmState' },
  { label: t('deviceFocusSignals.anchorHold'), value: 'DeviceHoldState' },
  { label: t('deviceFocusSignals.anchorMotion'), value: 'DeviceMotionFeedback' },
  { label: t('deviceFocusSignals.anchorPermit'), value: 'DevicePermitState' },
  { label: t('deviceFocusSignals.anchorContext'), value: 'DeviceAnalogContext' },
])

const anchorTypeLabel = (value?: string) => {
  const matched = anchorTypeOptions.value.find(item => item.value === value)
  return matched?.label || value || '-'
}

const anchorTagType = (value?: string) => {
  if (value === 'DeviceAlarmState') return 'danger'
  if (value === 'DeviceMotionFeedback') return 'success'
  if (value === 'DeviceHoldState') return 'warning'
  if (value === 'DevicePermitState') return 'primary'
  return 'info'
}

const roleLabel = (row: any) => {
  if (row.isContext) return t('deviceFocusSignals.roleContext')
  if (row.isCore) return t('deviceFocusSignals.roleCore')
  if (row.isWatchOnly) return t('deviceFocusSignals.roleWatch')
  return t('deviceFocusSignals.roleFocus')
}

const roleTagType = (row: any) => {
  if (row.isContext) return 'info'
  if (row.isCore) return 'danger'
  if (row.isWatchOnly) return 'warning'
  return 'primary'
}

const loadStationOptions = async () => {
  const currentProjectId = projectIdValue.value
  if (!currentProjectId) {
    stationOptions.value = []
    return
  }

  try {
    const res: any = await StopCauseDiagnosisAPI.getPersistedStationMap({
      projectId: currentProjectId,
    })
    if (currentProjectId !== projectIdValue.value) return
    mergeStationOptions(collectStationCodesFromStationMap(getPayload(res)))
  } catch {
    if (!stationOptions.value.length) {
      stationOptions.value = []
    }
  }
}

const loadSignals = async () => {
  const currentProjectId = projectIdValue.value
  if (!currentProjectId) return
  loading.value = true
  try {
    const res: any = await StopCauseDiagnosisAPI.getDeviceFocusSignals({
      projectId: currentProjectId,
      stationCode: stationCode.value || undefined,
      keyword: keyword.value || undefined,
      anchorType: anchorType.value || undefined,
      limit: 5000,
    })
    if (currentProjectId !== projectIdValue.value) return
    result.value = getPayload(res)
    mergeStationOptions((result.value?.stations || []).map((item: any) => item.stationCode))
    activeStations.value = (result.value?.stations || [])
      .slice(0, 4)
      .map((item: any) => item.stationCode)
  } finally {
    loading.value = false
  }
}

const handleReset = () => {
  keyword.value = ''
  stationCode.value = ''
  anchorType.value = ''
  loadSignals()
}

const handleSync = async () => {
  if (!projectIdValue.value) return
  await ElMessageBox.confirm(
    t('deviceFocusSignals.syncConfirm'),
    t('deviceFocusSignals.syncTitle'),
    {
      confirmButtonText: t('deviceFocusSignals.startSync'),
      cancelButtonText: t('deviceFocusSignals.cancel'),
      type: 'warning',
    },
  )
  syncing.value = true
  try {
    const res: any = await StopCauseDiagnosisAPI.refreshDeviceFocusSignals({
      projectId: projectIdValue.value,
      stationCode: stationCode.value || undefined,
      replaceExistingDeviceSignals: true,
      seedRecommendedOnly: false,
      maxCandidates: 0,
      minScore: 0,
      includeLocalVariables: true,
      includeFeedbackSignals: true,
      includeAnalogContextSignals: true,
      useLlmSemanticClassification: true,
      useSemanticCache: true,
      refreshSemanticCache: false,
      persistSemanticCache: true,
      maxLlmCandidates: 0,
    })
    const payload = getPayload(res)
    ElMessage.success(t('deviceFocusSignals.syncDone', { inserted: payload.insertedCount || 0, updated: payload.updatedCount || 0 }))
    await Promise.all([loadStationOptions(), loadSignals()])
  } finally {
    syncing.value = false
  }
}

const loadInitialData = async () => {
  await Promise.all([loadStationOptions(), loadSignals()])
}

watch(() => props.projectId, () => {
  stationCode.value = ''
  stationOptions.value = []
  void loadInitialData()
})

onMounted(loadInitialData)
</script>

<template>
  <div class="device-focus">
    <div class="device-focus__toolbar">
      <el-form :inline="true" @submit.prevent="loadSignals">
        <el-form-item :label="$t('deviceFocusSignals.station')">
          <el-select
            v-model="stationCode"
            :placeholder="$t('deviceFocusSignals.stationAll')"
            clearable
            filterable
            allow-create
            default-first-option
            style="width: 150px"
          >
            <el-option
              v-for="item in stationOptions"
              :key="item"
              :label="item"
              :value="item"
            />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('deviceFocusSignals.type')">
          <el-select v-model="anchorType" style="width: 150px">
            <el-option
              v-for="item in anchorTypeOptions"
              :key="item.value"
              :label="item.label"
              :value="item.value"
            />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('common.keyword')">
          <el-input
            v-model="keyword"
            :placeholder="$t('deviceFocusSignals.keywordPlaceholder')"
            clearable
            style="width: 220px"
            @keyup.enter="loadSignals"
          />
        </el-form-item>
        <el-form-item>
          <el-button type="primary" :loading="loading" @click="loadSignals">
            <i-ep-search class="mr-1" /> {{ $t('deviceFocusSignals.query') }}
          </el-button>
          <el-button @click="handleReset">
            <i-ep-refresh class="mr-1" /> {{ $t('common.reset') }}
          </el-button>
          <el-button type="warning" :loading="syncing" @click="handleSync">
            <i-ep-connection class="mr-1" /> {{ $t('deviceFocusSignals.sync') }}
          </el-button>
        </el-form-item>
      </el-form>
    </div>

    <div class="device-focus__summary">
      <div v-for="item in summaryItems" :key="item.label" class="summary-item">
        <span class="summary-label">{{ item.label }}</span>
        <span class="summary-value">{{ item.value }}</span>
      </div>
    </div>

    <el-empty v-if="!loading && !(result.stations || []).length" :description="$t('deviceFocusSignals.empty')" />

    <el-collapse v-else v-model="activeStations" class="station-list">
      <el-collapse-item
        v-for="station in result.stations || []"
        :key="station.stationCode"
        :name="station.stationCode"
      >
        <template #title>
          <div class="station-title">
            <span class="station-code">{{ station.stationCode }}</span>
            <el-tag size="small" type="info">{{ station.totalCount }} {{ $t('deviceFocusSignals.unit') }}</el-tag>
            <el-tag
              v-for="(count, type) in station.categoryCounts"
              :key="type"
              size="small"
              :type="anchorTagType(String(type))"
            >
              {{ anchorTypeLabel(String(type)) }} {{ count }}
            </el-tag>
          </div>
        </template>
        <el-table v-loading="loading" :data="station.signals || []" stripe>
          <el-table-column prop="variableName" :label="$t('deviceFocusSignals.variable')" min-width="260" show-overflow-tooltip />
          <el-table-column prop="semanticLabel" :label="$t('deviceFocusSignals.semantic')" min-width="180" show-overflow-tooltip />
          <el-table-column prop="anchorType" :label="$t('deviceFocusSignals.type')" width="120">
            <template #default="{ row }">
              <el-tag size="small" :type="anchorTagType(row.anchorType)">
                {{ anchorTypeLabel(row.anchorType) }}
              </el-tag>
            </template>
          </el-table-column>
          <el-table-column :label="$t('deviceFocusSignals.role')" width="90" align="center">
            <template #default="{ row }">
              <el-tag size="small" :type="roleTagType(row)">
                {{ roleLabel(row) }}
              </el-tag>
            </template>
          </el-table-column>
          <el-table-column prop="activationRule" :label="$t('deviceFocusSignals.trigger')" width="110" />
          <el-table-column prop="priority" :label="$t('deviceFocusSignals.priority')" width="90" align="center" />
          <el-table-column :label="$t('deviceFocusSignals.enable')" width="80" align="center">
            <template #default="{ row }">
              <el-tag size="small" :type="row.enabled ? 'success' : 'info'">
                {{ row.enabled ? $t('common.yes') : $t('common.no') }}
              </el-tag>
            </template>
          </el-table-column>
          <el-table-column prop="logicUnitId" label="LogicUnit" width="160" show-overflow-tooltip />
          <el-table-column prop="remark" :label="$t('deviceFocusSignals.basis')" min-width="260" show-overflow-tooltip />
        </el-table>
      </el-collapse-item>
    </el-collapse>
  </div>
</template>

<style scoped>
.device-focus {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.device-focus__toolbar {
  padding: 16px;
  background: var(--el-fill-color-light);
  border-radius: 8px;
}

.device-focus__summary {
  display: grid;
  grid-template-columns: repeat(4, minmax(120px, 1fr));
  gap: 12px;
}

.summary-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  min-height: 46px;
  padding: 0 14px;
  border: 1px solid var(--el-border-color-light);
  border-radius: 6px;
  background: var(--el-bg-color);
}

.summary-label {
  color: var(--el-text-color-secondary);
  font-size: 13px;
}

.summary-value {
  color: var(--el-text-color-primary);
  font-size: 18px;
  font-weight: 600;
}

.station-list {
  border-top: 1px solid var(--el-border-color-lighter);
}

.station-title {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}

.station-code {
  font-weight: 600;
}

:deep(.el-form-item) {
  margin-bottom: 0;
}

@media (max-width: 900px) {
  .device-focus__summary {
    grid-template-columns: repeat(2, minmax(120px, 1fr));
  }
}
</style>
