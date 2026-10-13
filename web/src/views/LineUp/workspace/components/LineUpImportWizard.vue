<script setup lang="ts">
import type {
  EntityId,
  LineUpImportProgress,
  LineUpWorkspace,
  LineUpWorkspaceImport,
} from '@/api/modules/LineUp/types'
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import messages from './lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}

const props = defineProps<{
  workspace: LineUpWorkspace | null
  lineId: EntityId
  deviceId: EntityId
  fileName: string
  step: 1 | 2 | 3
  progress: LineUpImportProgress | null
  loading: boolean
  uploading: boolean
}>()

const emit = defineEmits<{
  lineChange: [lineId: EntityId]
  deviceChange: [deviceId: EntityId]
  fileSelected: [file: File | null]
  start: []
  openImport: [item: LineUpWorkspaceImport]
}>()

const fileInput = ref<HTMLInputElement | null>(null)

const selectedLine = computed(() => props.workspace?.lines.find(item => item.id === props.lineId))
const selectedDevice = computed(() => props.workspace?.devices.find(item => item.id === props.deviceId))

function selectFile(event: Event) {
  const input = event.target as HTMLInputElement
  emit('fileSelected', input.files?.[0] ?? null)
}

function statusText(status?: string | null) {
  if (status === 'Uploaded') {
    return t('lineupWorkspace_form.statusUploaded')
  }
  if (status === 'Parsing') {
    return t('lineupWorkspace_form.statusParsing')
  }
  if (status === 'PreviewReady') {
    return t('lineupWorkspace_form.statusPreviewReady')
  }
  if (status === 'Committed') {
    return t('lineupWorkspace_form.statusCommitted')
  }
  if (status === 'Failed') {
    return t('lineupWorkspace_form.statusFailed')
  }
  return t('lineupWorkspace_form.statusNotStarted')
}

function canOpen(item: LineUpWorkspaceImport) {
  return item.status === 'PreviewReady' || item.status === 'Parsing' || item.status === 'Uploaded'
}
</script>

<template>
  <section class="import-wizard" :class="{ 'is-compact': step > 1 }">
    <div class="step-rail" :aria-label="$t('lineupWorkspace_form.ariaSteps')">
      <div v-for="item in 3" :key="item" class="step-item" :class="{ 'is-active': step === item, 'is-done': step > item }">
        <span>{{ step > item ? '✓' : item }}</span>
        <strong>{{ item === 1 ? $t('lineupWorkspace_form.stepSelectImport') : item === 2 ? $t('lineupWorkspace_form.stepReviewPreview') : $t('lineupWorkspace_form.stepSaveStructure') }}</strong>
      </div>
    </div>

    <template v-if="step === 1">
      <div class="configuration-grid">
        <label>
          <span>{{ $t('lineupWorkspace_form.fieldLine') }}</span>
          <el-select
            :model-value="lineId"
            filterable
            :placeholder="$t('lineupWorkspace_form.placeholderLine')"
            :loading="loading"
            @change="emit('lineChange', $event)"
          >
            <el-option
              v-for="line in workspace?.lines ?? []"
              :key="line.id"
              :label="`${line.code} · ${line.name}`"
              :value="line.id"
            />
          </el-select>
        </label>
        <label>
          <span>{{ $t('lineupWorkspace_form.fieldPlcDevice') }}</span>
          <el-select
            :model-value="deviceId"
            filterable
            :placeholder="$t('lineupWorkspace_form.placeholderDevice')"
            :loading="loading"
            @change="emit('deviceChange', $event)"
          >
            <el-option
              v-for="device in workspace?.devices ?? []"
              :key="device.id"
              :label="`${device.name} · ${device.projectName}`"
              :value="device.id"
            />
          </el-select>
        </label>
        <label class="file-field">
          <span>{{ $t('lineupWorkspace_form.fieldGraphFile') }}</span>
          <button type="button" class="file-picker" @click="fileInput?.click()">
            <strong>{{ fileName || $t('lineupWorkspace_form.pickerEmpty') }}</strong>
            <small>{{ fileName ? $t('lineupWorkspace_form.pickerReselect') : $t('lineupWorkspace_form.pickerSizeHint') }}</small>
          </button>
          <input ref="fileInput" type="file" accept=".xml,.ml,.zip" @change="selectFile">
        </label>
        <el-button
          type="primary"
          size="large"
          :loading="uploading"
          :disabled="!lineId || !deviceId || !fileName"
          @click="emit('start')"
        >
          {{ $t('lineupWorkspace_form.startImportPreview') }}
        </el-button>
      </div>

      <div v-if="progress" class="progress-strip" :data-status="progress.status">
        <span class="progress-strip__pulse" />
        <strong>{{ statusText(progress.status) }}</strong>
        <span>{{ $t('lineupWorkspace_form.batchLabel', { id: progress.batchId }) }}</span>
        <span v-if="progress.errorCode">{{ $t('lineupWorkspace_form.errorCodeLabel', { code: progress.errorCode }) }}</span>
      </div>

      <div class="workspace-overview">
        <div class="summary-cards">
          <article><strong>{{ workspace?.summary.deviceCount ?? 0 }}</strong><span>{{ $t('lineupWorkspace_form.statDevices') }}</span></article>
          <article><strong>{{ workspace?.summary.previewReadyCount ?? 0 }}</strong><span>{{ $t('lineupWorkspace_form.statPendingReview') }}</span></article>
          <article><strong>{{ workspace?.summary.committedCount ?? 0 }}</strong><span>{{ $t('lineupWorkspace_form.statCommitted') }}</span></article>
          <article><strong>{{ workspace?.summary.failedCount ?? 0 }}</strong><span>{{ $t('lineupWorkspace_form.statFailed') }}</span></article>
        </div>
        <div class="recent-imports">
          <div class="recent-imports__title">
            {{ $t('lineupWorkspace_form.recentImportsTitle') }}
          </div>
          <button
            v-for="item in workspace?.recentImports ?? []"
            :key="item.batchId"
            type="button"
            :disabled="!canOpen(item)"
            @click="emit('openImport', item)"
          >
            <span class="file-name" :title="item.sourceFileName">{{ item.sourceFileName }}</span>
            <span>{{ item.summary.sequenceCount }} Sequence</span>
            <strong :data-status="item.status">{{ statusText(item.status) }}</strong>
          </button>
          <el-empty v-if="(workspace?.recentImports.length ?? 0) === 0" :image-size="44" :description="$t('lineupWorkspace_form.emptyNoImports')" />
        </div>
      </div>
    </template>

    <div v-else class="compact-summary">
      <span><small>{{ $t('lineupWorkspace_form.fieldLine') }}</small>{{ selectedLine?.code }} · {{ selectedLine?.name }}</span>
      <span><small>PLC</small>{{ selectedDevice?.name }} · {{ selectedDevice?.projectName }}</span>
      <span><small>{{ $t('lineupWorkspace_form.fieldSource') }}</small>{{ fileName }}</span>
      <strong>{{ statusText(progress?.status) }}</strong>
    </div>
  </section>
</template>

<style scoped>
.import-wizard {
  padding: 18px;
  container-type: inline-size;
  background: var(--el-bg-color);
  border: 1px solid var(--el-border-color-light);
  border-radius: 9px;
}

.import-wizard.is-compact {
  padding: 10px 14px;
}

.step-rail {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 8px;
  max-width: 760px;
  margin: 0 auto 20px;
}

.is-compact .step-rail {
  max-width: 560px;
  margin-bottom: 8px;
}

.step-item {
  position: relative;
  display: flex;
  gap: 8px;
  align-items: center;
  font-size: 12px;
  color: var(--el-text-color-placeholder);
}

.step-item:not(:last-child)::after {
  position: absolute;
  top: 13px;
  right: 8px;
  left: 96px;
  height: 1px;
  content: "";
  background: var(--el-border-color);
}

.step-item > span {
  display: grid;
  flex: 0 0 auto;
  place-items: center;
  width: 26px;
  height: 26px;
  font-family: Consolas, monospace;
  border: 1px solid var(--el-border-color);
  border-radius: 50%;
}

.step-item.is-active,
.step-item.is-done {
  color: var(--el-color-primary);
}

.step-item.is-active > span {
  color: white;
  background: var(--el-color-primary);
  border-color: var(--el-color-primary);
}

.step-item.is-done > span {
  color: var(--el-color-success);
  border-color: var(--el-color-success);
}

.configuration-grid {
  display: grid;
  grid-template-columns: minmax(200px, 1fr) minmax(240px, 1.25fr) minmax(260px, 1.35fr) auto;
  gap: 12px;
  align-items: end;
}

.configuration-grid label {
  display: grid;
  gap: 6px;
  min-width: 0;
}

.configuration-grid label > span {
  font-size: 12px;
  font-weight: 600;
}

.file-field input {
  position: absolute;
  width: 1px;
  height: 1px;
  pointer-events: none;
  opacity: 0;
}

.file-picker {
  display: grid;
  min-width: 0;
  padding: 8px 12px;
  text-align: left;
  cursor: pointer;
  background: var(--el-fill-color-blank);
  border: 1px dashed var(--el-border-color);
  border-radius: 4px;
}

.file-picker strong {
  overflow: hidden;
  text-overflow: ellipsis;
  font-size: 12px;
  white-space: nowrap;
}

.file-picker small {
  color: var(--el-text-color-secondary);
}

.progress-strip {
  display: flex;
  flex-wrap: wrap;
  gap: 8px 16px;
  align-items: center;
  padding: 9px 12px;
  margin-top: 12px;
  font-size: 12px;
  background: var(--el-color-primary-light-9);
  border-radius: 5px;
}

.progress-strip__pulse {
  width: 8px;
  height: 8px;
  background: var(--el-color-primary);
  border-radius: 50%;
  box-shadow: 0 0 0 4px var(--el-color-primary-light-7);
}

.workspace-overview {
  display: grid;
  grid-template-columns: minmax(420px, 0.9fr) minmax(400px, 1.1fr);
  gap: 14px;
  margin-top: 16px;
}

.summary-cards {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 8px;
}

.summary-cards article {
  display: grid;
  gap: 2px;
  padding: 14px;
  background: var(--el-fill-color-lighter);
  border-radius: 6px;
}

.summary-cards strong {
  font: 700 22px Consolas, monospace;
  color: var(--el-text-color-primary);
}

.summary-cards span {
  font-size: 11px;
  color: var(--el-text-color-secondary);
}

.recent-imports {
  max-height: 190px;
  overflow: auto;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 6px;
}

.recent-imports__title {
  position: sticky;
  top: 0;
  z-index: 1;
  padding: 8px 10px;
  font-size: 12px;
  font-weight: 700;
  background: var(--el-bg-color);
  border-bottom: 1px solid var(--el-border-color-lighter);
}

.recent-imports button {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto auto;
  gap: 12px;
  width: 100%;
  padding: 9px 10px;
  text-align: left;
  cursor: pointer;
  background: transparent;
  border: 0;
  border-bottom: 1px solid var(--el-border-color-lighter);
}

.recent-imports button:disabled {
  cursor: default;
  opacity: 0.55;
}

.recent-imports .file-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.recent-imports button span {
  font-size: 11px;
  color: var(--el-text-color-secondary);
}

.recent-imports button strong {
  font-size: 11px;
  color: var(--el-color-primary);
}

.recent-imports button strong[data-status="Failed"] {
  color: var(--el-color-danger);
}

.recent-imports button strong[data-status="Committed"] {
  color: var(--el-color-success);
}

.compact-summary {
  display: flex;
  flex-wrap: wrap;
  gap: 8px 18px;
  align-items: center;
  padding-top: 7px;
  font-size: 12px;
  border-top: 1px solid var(--el-border-color-lighter);
}

.compact-summary span {
  display: flex;
  gap: 6px;
  min-width: 0;
}

.compact-summary small {
  color: var(--el-text-color-secondary);
}

.compact-summary strong {
  margin-left: auto;
  color: var(--el-color-primary);
}

@container (width <= 1100px) {
  .configuration-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .workspace-overview {
    grid-template-columns: 1fr;
  }
}
</style>
