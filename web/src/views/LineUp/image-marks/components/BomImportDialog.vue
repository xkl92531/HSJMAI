<script setup lang="ts">
import type { BomImportPreviewResult, BomImportPreviewRow, LineUpPlcDevice } from '@/api/modules/LineUp/imageMarks'
import type { EntityId } from '@/api/modules/LineUp/types'
import { ElMessage } from 'element-plus'
import { computed, ref, watch } from 'vue'
import imageMarkApi from '@/api/modules/LineUp/imageMarks'
import { useI18n } from 'vue-i18n'

const props = defineProps<{
  visible: boolean
  lines: Array<{ id: EntityId, name: string | null }>
  defaultLineId: EntityId | ''
}>()

const emit = defineEmits<{
  (e: 'update:visible', value: boolean): void
  (e: 'imported'): void
}>()

const { t } = useI18n()
const step = ref<'upload' | 'preview'>('upload')
const lineId = ref<EntityId | ''>(props.defaultLineId)
const plcId = ref<EntityId | ''>('')
const plcDevices = ref<LineUpPlcDevice[]>([])
const plcLoading = ref(false)
const file = ref<File | null>(null)
const parsing = ref(false)
const committing = ref(false)
const preview = ref<BomImportPreviewResult | null>(null)

const canParse = computed(() => !!lineId.value && !!plcId.value && !!file.value)
const canCommit = computed(() =>
  !!preview.value && (preview.value.createCount + preview.value.updateCount) > 0)

watch(() => props.visible, (open) => {
  if (open)
    reset()
})

watch(lineId, async () => {
  plcId.value = ''
  plcDevices.value = []
  await loadPlcDevices()
})

function reset() {
  step.value = 'upload'
  lineId.value = props.defaultLineId
  plcId.value = ''
  plcDevices.value = []
  file.value = null
  preview.value = null
  if (lineId.value)
    void loadPlcDevices()
}

async function loadPlcDevices() {
  if (!lineId.value)
    return
  plcLoading.value = true
  try {
    plcDevices.value = await imageMarkApi.listPlcs(lineId.value)
  }
  catch {
    plcDevices.value = []
  }
  finally {
    plcLoading.value = false
  }
}

function onFileChange(event: Event) {
  const input = event.target as HTMLInputElement
  const picked = input.files?.[0] ?? null
  if (picked && !/\.(xlsx|xls)$/i.test(picked.name)) {
    ElMessage.warning(t('lineupImageMarks_import.fileTypeInvalid'))
    input.value = ''
    file.value = null
    return
  }
  file.value = picked
}

async function parsePreview() {
  if (!lineId.value || !plcId.value || !file.value)
    return
  parsing.value = true
  try {
    preview.value = await imageMarkApi.importBomPreview(lineId.value, file.value)
    step.value = 'preview'
  }
  catch (error) {
    ElMessage.error(error instanceof Error ? error.message : t('lineupImageMarks_import.parseFailed'))
  }
  finally {
    parsing.value = false
  }
}

async function commitImport() {
  if (!preview.value || !lineId.value || !plcId.value)
    return
  committing.value = true
  try {
    const rows = preview.value.rows
      .filter(row => row.status !== 'error')
      .map(row => ({
        rowIndex: row.rowIndex,
        refNo: row.refNo,
        name: row.name,
        tagCode: row.tagCode,
        dbAddress: row.dbAddress,
        modelNo: row.modelNo,
        brand: row.brand,
        partNo: row.partNo,
      }))
    const result = await imageMarkApi.importBomCommit({
      productionLineId: lineId.value,
      plcId: plcId.value,
      rows,
    })
    ElMessage.success(t('lineupImageMarks_import.commitSucceeded', {
      created: result.createdCount,
      updated: result.updatedCount,
    }))
    if (result.failedRows.length)
      ElMessage.warning(t('lineupImageMarks_import.partialFailed', { count: result.failedRows.length }))
    close()
    emit('imported')
  }
  catch (error) {
    ElMessage.error(error instanceof Error ? error.message : t('lineupImageMarks_import.commitFailed'))
  }
  finally {
    committing.value = false
  }
}

async function downloadTemplate() {
  await imageMarkApi.downloadBomTemplate()
}

function backToUpload() {
  step.value = 'upload'
}

function close() {
  emit('update:visible', false)
}

function statusTagType(row: BomImportPreviewRow) {
  if (row.status === 'error')
    return 'danger'
  return row.status === 'update' ? 'warning' : 'success'
}

function errorText(codes: string[]) {
  return codes.map(code => t(`lineupImageMarks_import.err_${code}`)).join('; ')
}
</script>

<template>
  <el-dialog
    :model-value="visible"
    :title="t('lineupImageMarks_import.title')"
    width="1080px"
    append-to-body
    @update:model-value="(value: boolean) => (value ? null : close())"
  >
    <div v-if="step === 'upload'" class="import-form">
      <label>{{ t('lineupImageMarks_import.lineLabel') }}</label>
      <el-select v-model="lineId" filterable :placeholder="t('lineupImageMarks_import.selectLine')">
        <el-option
          v-for="line in lines"
          :key="line.id"
          :label="line.name || String(line.id)"
          :value="line.id"
        />
      </el-select>
      <label>{{ t('lineupImageMarks_import.plcLabel') }}</label>
      <el-select
        v-model="plcId"
        :loading="plcLoading"
        :disabled="!lineId"
        filterable
        :placeholder="t('lineupImageMarks_import.selectPlc')"
      >
        <el-option
          v-for="device in plcDevices"
          :key="device.id"
          :label="device.ip ? `${device.name}（${device.ip}）` : (device.name || String(device.id))"
          :value="device.id"
        />
      </el-select>
      <label>{{ t('lineupImageMarks_import.fileLabel') }}</label>
      <div class="file-row">
        <input type="file" accept=".xlsx,.xls" @change="onFileChange">
        <el-button link type="primary" @click="downloadTemplate">
          {{ t('lineupImageMarks_import.downloadTemplate') }}
        </el-button>
      </div>
      <p class="tip">{{ t('lineupImageMarks_import.lineScopeTip') }}</p>
    </div>
    <div v-else class="preview">
      <div class="summary">
        <el-tag type="info">{{ t('lineupImageMarks_import.totalRows', { count: preview?.totalRows ?? 0 }) }}</el-tag>
        <el-tag type="success">{{ t('lineupImageMarks_import.statusCreate') }} {{ preview?.createCount ?? 0 }}</el-tag>
        <el-tag type="warning">{{ t('lineupImageMarks_import.statusUpdate') }} {{ preview?.updateCount ?? 0 }}</el-tag>
        <el-tag type="danger">{{ t('lineupImageMarks_import.statusError') }} {{ preview?.errorCount ?? 0 }}</el-tag>
      </div>
      <el-table :data="preview?.rows ?? []" height="420" size="small">
        <el-table-column prop="rowIndex" :label="t('lineupImageMarks_import.colRow')" width="60" />
        <el-table-column prop="refNo" :label="t('lineupImageMarks_import.colRefNo')" width="150" show-overflow-tooltip />
        <el-table-column prop="name" :label="t('lineupImageMarks_import.colName')" min-width="110" show-overflow-tooltip />
        <el-table-column prop="tagCode" :label="t('lineupImageMarks_import.colTagCode')" min-width="130" show-overflow-tooltip />
        <el-table-column prop="dbAddress" :label="t('lineupImageMarks_import.colDbAddress')" min-width="110" show-overflow-tooltip />
        <el-table-column prop="modelNo" :label="t('lineupImageMarks_import.colModelNo')" min-width="90" show-overflow-tooltip />
        <el-table-column prop="brand" :label="t('lineupImageMarks_import.colBrand')" min-width="80" show-overflow-tooltip />
        <el-table-column prop="partNo" :label="t('lineupImageMarks_import.colPartNo')" min-width="90" show-overflow-tooltip />
        <el-table-column :label="t('lineupImageMarks_import.colStatus')" min-width="200">
          <template #default="{ row }">
            <el-tag :type="statusTagType(row)">
              {{ row.status === 'update' ? t('lineupImageMarks_import.statusUpdate') : t('lineupImageMarks_import.statusCreate') }}
            </el-tag>
            <span v-if="row.errorCodes.length" class="err">{{ errorText(row.errorCodes) }}</span>
          </template>
        </el-table-column>
      </el-table>
    </div>
    <template #footer>
      <template v-if="step === 'upload'">
        <el-button @click="close">
          {{ t('common.cancel') }}
        </el-button>
        <el-button type="primary" :loading="parsing" :disabled="!canParse" @click="parsePreview">
          {{ t('lineupImageMarks_import.parsePreview') }}
        </el-button>
      </template>
      <template v-else>
        <el-button @click="backToUpload">
          {{ t('lineupImageMarks_import.back') }}
        </el-button>
        <el-button type="primary" :loading="committing" :disabled="!canCommit" @click="commitImport">
          {{ t('lineupImageMarks_import.confirmImport') }}
        </el-button>
      </template>
    </template>
  </el-dialog>
</template>

<style scoped lang="scss">
.import-form {
  display: flex;
  flex-direction: column;
  gap: 6px;

  label {
    font-size: 12px;
    color: var(--el-text-color-secondary);
  }

  .file-row {
    display: flex;
    gap: 12px;
    align-items: center;
  }

  .tip {
    margin: 4px 0 0;
    font-size: 12px;
    color: var(--el-text-color-secondary);
  }
}

.preview {
  .summary {
    display: flex;
    gap: 8px;
    margin-bottom: 8px;
  }

  .err {
    margin-left: 6px;
    font-size: 12px;
    color: var(--el-color-danger);
  }
}
</style>
