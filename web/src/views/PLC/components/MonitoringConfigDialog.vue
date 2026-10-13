<script setup lang="ts">
import { ElMessage } from 'element-plus'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import StopCauseDiagnosisAPI from '@/api/modules/PLC/stopCauseDiagnosis'
import messages from '../monitoring.lang'

type GenericRecord = Record<string, any>

const props = defineProps<{
  modelValue: boolean
  projectId: string | number
}>()
const emit = defineEmits<{
  'update:modelValue': [value: boolean]
}>()
const { t, mergeLocaleMessage } = useI18n()
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const visible = computed({
  get: () => props.modelValue,
  set: (value: boolean) => emit('update:modelValue', value),
})
const selectedProjectId = computed(() => String(props.projectId ?? '').trim())

// ==================== 监控配置管理 ====================
const configLoading = ref(false)
const configList = ref<GenericRecord[]>([])
const configForm = ref<GenericRecord>({})
const configEditing = ref(false)
let latestConfigRequest = 0

async function loadMonitoringConfigs() {
  if (!selectedProjectId.value) {
    return
  }
  const requestedProjectId = selectedProjectId.value
  const requestId = ++latestConfigRequest
  configLoading.value = true
  try {
    const response = await StopCauseDiagnosisAPI.getMonitoringConfigs({ projectId: requestedProjectId, limit: 200 }) as any
    const payload = response?.data ?? response?.backResult ?? response
    const rows = payload?.Data ?? payload?.data ?? payload
    if (requestId === latestConfigRequest && requestedProjectId === selectedProjectId.value && visible.value) {
      configList.value = Array.isArray(rows) ? rows : []
    }
  }
  catch (error: any) {
    ElMessage.error(error.message || t('plc_monitoring.loadConfigFailed'))
  }
  finally {
    if (requestId === latestConfigRequest) {
      configLoading.value = false
    }
  }
}

function startCreateConfig() {
  configEditing.value = true
  configForm.value = {
    id: 0,
    projectId: selectedProjectId.value,
    stationCode: '',
    enabled: true,
    coreIntervalMs: 60000,
    contextIntervalMs: 120000,
    bundleWindowMs: 3000,
    silenceThresholdSeconds: 30,
    snapshotTtlSeconds: 600,
    historyRetentionDays: 90,
    patternRebuildCron: '',
    remark: '',
  }
}

function startEditConfig(item: GenericRecord) {
  configEditing.value = true
  configForm.value = { ...item, projectId: selectedProjectId.value }
}

function cancelEditConfig() {
  configEditing.value = false
  configForm.value = {}
}

async function saveConfig() {
  const form = configForm.value
  if (!form.projectId || !form.stationCode?.trim()) {
    ElMessage.warning(t('plc_monitoring.projectIdAndStationRequired'))
    return
  }
  configLoading.value = true
  try {
    if (form.id && form.id > 0) {
      await StopCauseDiagnosisAPI.updateMonitoringConfig(form)
      ElMessage.success(t('common.updateSuccess'))
    }
    else {
      await StopCauseDiagnosisAPI.createMonitoringConfig(form)
      ElMessage.success(t('plc_monitoring.configCreateSuccess'))
    }
    configEditing.value = false
    configForm.value = {}
    await loadMonitoringConfigs()
  }
  catch (error: any) {
    ElMessage.error(error.message || t('plc_monitoring.saveConfigFailed'))
  }
  finally {
    configLoading.value = false
  }
}

async function toggleConfig(item: GenericRecord) {
  try {
    await StopCauseDiagnosisAPI.toggleMonitoringConfig(item.id, !item.enabled)
    ElMessage.success(t('plc_monitoring.stationToggled', { station: item.stationCode, action: item.enabled ? t('common.disable') : t('common.enbale') }))
    await loadMonitoringConfigs()
  }
  catch (error: any) {
    ElMessage.error(error.message || t('plc_monitoring.toggleStatusFailed'))
  }
}

async function deleteConfig(item: GenericRecord) {
  try {
    await StopCauseDiagnosisAPI.deleteMonitoringConfig(item.id)
    ElMessage.success(t('plc_monitoring.stationConfigDeleted', { station: item.stationCode }))
    await loadMonitoringConfigs()
  }
  catch (error: any) {
    ElMessage.error(error.message || t('plc_monitoring.deleteConfigFailed'))
  }
}

watch([visible, selectedProjectId], ([isOpen, id]) => {
  if (!isOpen || !id) {
    return
  }
  configEditing.value = false
  configForm.value = {}
  configList.value = []
  void loadMonitoringConfigs()
}, { immediate: true })
</script>

<template>
  <!-- 工位监控配置管理弹窗 -->
  <el-dialog v-model="visible" :title="$t('plc_monitoring.configDialogTitle')" width="780px" :close-on-click-modal="false" destroy-on-close>
    <div v-if="!configEditing">
      <div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 12px;">
        <span style="font-size: 14px; color: var(--el-text-color-secondary);">{{ $t('plc_monitoring.currentProjectConfigs', { id: selectedProjectId }) }}</span>
        <el-button type="primary" size="small" @click="startCreateConfig">
          <FaIcon name="i-ep:plus" class="mr-1" />{{ $t('plc_monitoring.addStationConfigBtn') }}
        </el-button>
      </div>
      <el-table v-loading="configLoading" :data="configList" stripe size="small" max-height="420" :empty-text="$t('plc_monitoring.noConfigsEmpty')">
        <el-table-column prop="stationCode" :label="$t('plc_monitoring.stationColumn')" width="110" />
        <el-table-column prop="enabled" :label="$t('plc_monitoring.statusLabel')" width="80" align="center">
          <template #default="{ row }">
            <el-switch :model-value="row.enabled" size="small" @change="toggleConfig(row)" />
          </template>
        </el-table-column>
        <el-table-column prop="coreIntervalMs" :label="$t('plc_monitoring.coreIntervalColumn')" width="110" align="right">
          <template #default="{ row }">
            {{ row.coreIntervalMs ?? '-' }}
          </template>
        </el-table-column>
        <el-table-column prop="silenceThresholdSeconds" :label="$t('plc_monitoring.silenceThresholdColumn')" width="100" align="right">
          <template #default="{ row }">
            {{ row.silenceThresholdSeconds ?? '-' }}
          </template>
        </el-table-column>
        <el-table-column prop="patternRebuildCron" :label="$t('plc_monitoring.rebuildCronColumn')" min-width="120">
          <template #default="{ row }">
            {{ row.patternRebuildCron || '-' }}
          </template>
        </el-table-column>
        <el-table-column prop="remark" :label="$t('plc_monitoring.remarkColumn')" min-width="100">
          <template #default="{ row }">
            {{ row.remark || '-' }}
          </template>
        </el-table-column>
        <el-table-column :label="$t('common.operate')" width="130" fixed="right" align="center">
          <template #default="{ row }">
            <el-button link type="primary" size="small" @click="startEditConfig(row)">
              {{ $t('common.edit') }}
            </el-button>
            <el-popconfirm :title="$t('plc_monitoring.confirmDeleteQuestion')" :confirm-button-text="$t('common.delete')" :cancel-button-text="$t('common.cancel')" @confirm="deleteConfig(row)">
              <template #reference>
                <el-button link type="danger" size="small">
                  {{ $t('common.delete') }}
                </el-button>
              </template>
            </el-popconfirm>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <div v-else>
      <div style="margin-bottom: 12px;">
        <el-button size="small" @click="cancelEditConfig">
          <FaIcon name="i-ep:back" class="mr-1" />{{ $t('plc_monitoring.backToListBtn') }}
        </el-button>
        <span style="margin-left: 8px; font-size: 14px; font-weight: 500;">{{ configForm.id ? $t('plc_monitoring.editConfig') : $t('plc_monitoring.addConfig') }}</span>
      </div>
      <el-form :model="configForm" label-width="130px" size="small">
        <el-form-item :label="$t('plc_monitoring.stationCodeLabel')" required>
          <el-input v-model="configForm.stationCode" :disabled="!!configForm.id" :placeholder="$t('plc_monitoring.stationCodeExamplePlaceholder')" />
        </el-form-item>
        <el-form-item :label="$t('common.enbale')">
          <el-switch v-model="configForm.enabled" />
        </el-form-item>
        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item :label="$t('plc_monitoring.coreIntervalColumn')">
              <el-input-number v-model="configForm.coreIntervalMs" :min="5000" :step="5000" controls-position="right" style="width: 100%;" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('plc_monitoring.contextIntervalLabel')">
              <el-input-number v-model="configForm.contextIntervalMs" :min="5000" :step="5000" controls-position="right" style="width: 100%;" />
            </el-form-item>
          </el-col>
        </el-row>
        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item :label="$t('plc_monitoring.bundleWindowLabel')">
              <el-input-number v-model="configForm.bundleWindowMs" :min="500" :step="500" controls-position="right" style="width: 100%;" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('plc_monitoring.silenceThresholdColumn')">
              <el-input-number v-model="configForm.silenceThresholdSeconds" :min="5" :step="5" controls-position="right" style="width: 100%;" />
            </el-form-item>
          </el-col>
        </el-row>
        <el-row :gutter="16">
          <el-col :span="12">
            <el-form-item :label="$t('plc_monitoring.snapshotTtlLabel')">
              <el-input-number v-model="configForm.snapshotTtlSeconds" :min="60" :step="60" controls-position="right" style="width: 100%;" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('plc_monitoring.historyRetentionLabel')">
              <el-input-number v-model="configForm.historyRetentionDays" :min="1" :step="30" controls-position="right" style="width: 100%;" />
            </el-form-item>
          </el-col>
        </el-row>
        <el-form-item :label="$t('plc_monitoring.rebuildCronLabel')">
          <el-input v-model="configForm.patternRebuildCron" :placeholder="$t('plc_monitoring.cronPlaceholder')" />
        </el-form-item>
        <el-form-item :label="$t('plc_monitoring.remarkColumn')">
          <el-input v-model="configForm.remark" type="textarea" :rows="2" :placeholder="$t('plc_monitoring.remarkPlaceholder')" />
        </el-form-item>
        <el-form-item>
          <el-button type="primary" :loading="configLoading" @click="saveConfig">
            {{ $t('common.save') }}
          </el-button>
          <el-button @click="cancelEditConfig">
            {{ $t('common.cancel') }}
          </el-button>
        </el-form-item>
      </el-form>
    </div>
  </el-dialog>
</template>
