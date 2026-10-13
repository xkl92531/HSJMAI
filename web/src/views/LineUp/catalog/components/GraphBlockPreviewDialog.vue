<script setup lang="ts">
import type { PlcGraphBlockItem } from '@/api/modules/PLC/graphBlock'
import type {
  EntityId,
  LineUpImportNode,
  LineUpImportSequence,
  LineUpWorkspace,
} from '@/api/modules/LineUp/types'
import type { TiaGraphViewState } from '../../components/tiaGraphLayout'
import { ElMessage } from 'element-plus'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import lineUpImportApi from '@/api/modules/LineUp/import'
import GraphPreviewPanel from '../../workspace/components/GraphPreviewPanel.vue'
import HierarchyReviewPanel from '../../workspace/components/HierarchyReviewPanel.vue'
import ImportIssueList from '../../workspace/components/ImportIssueList.vue'
import { createLineUpImportWizard } from '../../workspace/useLineUpImportWizard'
import { selectTiaGraphElement, symbolNamesForSelection, withAdjacentTransitionNetworks } from '../../components/tiaGraphSelection'
import { useI18n } from 'vue-i18n'
import messages from './lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}

/**
 * GRAPH 结构管理页「生成预览」弹框：
 * 从选中块免上传发起 LineUp 预览（服务端取 ai_plc_sourcefile.filecontent 归档原文），
 * 弹框内完成 预览 → 层级决策 → 保存结构（提交入库）全流程，复用 workspace 向导状态机。
 */
const props = defineProps<{
  modelValue: boolean
  block: PlcGraphBlockItem | null
  /** 预填产线（catalog 当前筛选，Basic_ProductionLine.Id） */
  initialLineId?: string
  /** 预填设备（catalog 当前筛选，与 ai_plc_project_new.plcDeviceId 同空间） */
  initialDeviceId?: string
}>()

const emit = defineEmits<{
  'update:modelValue': [value: boolean]
}>()

const visibleProxy = computed({
  get: () => props.modelValue,
  set: (value: boolean) => emit('update:modelValue', value),
})

const workspace = ref<LineUpWorkspace | null>(null)
const workspaceLoading = ref(false)
const lineId = ref<EntityId>('')
const deviceId = ref<EntityId>('')

const wizard = createLineUpImportWizard({ pollIntervalMs: 1000 })
const {
  progress,
  preview,
  receipt,
  errorCode,
  isUploading,
  isPolling,
  isCommitting,
  selectedSequenceKey,
  sequences,
  displaySequences,
  allIssues,
  canCommitStructure,
  runtimeStatusText,
  currentSequenceView,
  hierarchyDecisions,
  batchId,
} = wizard

const currentStep = computed<1 | 2 | 3>(() => (receipt.value ? 3 : preview.value ? 2 : 1))
/** 工步变量（ai_plc_graphsteptag）查询用项目 id：取预览地址解析的权威 PLC 项目 */
const stepTagProjectId = computed(() => preview.value?.documents[0]?.address?.plcProjectId ?? '')
/** 工步变量过滤用 GRAPH FB 类型名：只显示当前 graph 对应实例 DB 的变量 */
const stepTagFbName = computed(() => preview.value?.documents[0]?.blockName ?? '')
/** BOM 点位图子工站：取层级候选的 subStationId（getStation 按子工站查；
 * 候选 stationId 是 Basic_WorkStation 空间，直接用会 404） */
const bomSubStationId = computed(() => {
  const ids = new Set<string>()
  for (const document of preview.value?.documents ?? []) {
    for (const resolution of document.hierarchy.sequences) {
      for (const candidate of resolution.candidates) {
        if (candidate.subStationId) {
          ids.add(String(candidate.subStationId))
        }
      }
    }
  }
  return [...ids].join(',')
})
/** 选中节点关联的符号名（完整路径+末段裸名）：工步=聚合相邻转换条件，转换=自身条件；与 Lup_BomDirectory.TagCode 匹配 */
const bomReferences = computed(() => {
  const sequence = selectedSequence.value
  const sourceKey = currentSequenceView.value?.selectedNodeKey
  if (!sequence || !sourceKey) {
    return []
  }
  const selection = selectTiaGraphElement(sequence, sourceKey)
  if (!selection) {
    return []
  }
  return symbolNamesForSelection(withAdjacentTransitionNetworks(sequence, selection))
})
/** 右侧问题清单面板默认收起，画布区最大化 */
const issueDockCollapsed = ref(true)
const selectedSequence = computed<LineUpImportSequence | null>(() =>
  sequences.value.find(item => item.sourceKey === selectedSequenceKey.value) ?? null,
)
const selectedNode = computed<LineUpImportNode | null>(() => {
  const sourceKey = currentSequenceView.value?.selectedNodeKey
  return selectedSequence.value?.nodes.find(item => item.sourceKey === sourceKey) ?? null
})
const selectedDisplayName = computed(() => {
  const item = selectedNode.value ?? selectedSequence.value
  if (!item) {
    return ''
  }
  return wizard.displayNameChanges.value.get(item.sourceKey) ?? item.displayName
})

/** 层级改派选择（与工作台面板载荷同构） */
interface HierarchyReassignSelection {
  processId: EntityId
  stationId: EntityId
  subStationId: EntityId
  newProcessCode: string | null
  newStationCode: string | null
  newSubStationCode: string | null
  processCode: string | null
  processName: string | null
  stationCode: string | null
  stationName: string | null
  subStationCode: string | null
  subStationName: string | null
}

interface HierarchyReassignPayload extends HierarchyReassignSelection {
  sequenceSourceKey: string
  persisted?: boolean
  depth?: number
}

interface HierarchyReassignBatchPayload extends HierarchyReassignSelection {
  sequenceSourceKeys: string[]
  persistedKeys?: string[]
  depth?: number
}

/** 未保存绑定的顺控：本地调整预览归属（只动触达层级），「保存 GRAPH 结构」时随全量结构落库 */
function applyHierarchyOverrideLocal(selection: HierarchyReassignSelection & { depth?: number }, keys: string[]) {
  const keySet = new Set(keys)
  const touchProcess = Boolean(selection.processId || selection.newProcessCode)
  const touchStation = Boolean(selection.stationId || selection.newStationCode)
  const touchSubStation = Boolean(selection.subStationId || selection.newSubStationCode)
  for (const document of preview.value?.documents ?? []) {
    for (const resolution of document.hierarchy.sequences) {
      if (!keySet.has(resolution.sequenceSourceKey)) {
        continue
      }
      if (touchProcess) {
        resolution.processId = selection.processId || '0'
      }
      if (touchStation) {
        resolution.stationId = selection.stationId || '0'
      }
      if (touchSubStation) {
        resolution.subStationId = selection.subStationId || '0'
      }
      for (const candidate of resolution.candidates) {
        if (touchProcess) {
          candidate.processId = selection.processId || '0'
          candidate.processCode = selection.processCode
          candidate.processName = selection.processName
        }
        if (touchStation) {
          candidate.stationId = selection.stationId || '0'
          candidate.stationCode = selection.stationCode
          candidate.stationName = selection.stationName
        }
        if (touchSubStation) {
          candidate.subStationId = selection.subStationId || '0'
          candidate.subStationCode = selection.subStationCode
          candidate.subStationName = selection.subStationName
        }
      }
    }
  }
}

/** 已落库绑定的直接改；未落库的带批次 Id 由后端从预览取元数据直接新建绑定（含新编码新建层级） */
async function callReassignApi(keys: string[], selection: HierarchyReassignSelection) {
  const reassign = lineUpImportApi.reassignHierarchy
  if (!reassign) {
    ElMessage.error('HierarchyReassignUnavailable')
    return { ok: false, created: '' }
  }
  try {
    const receipt = await reassign(lineId.value, deviceId.value, keys.map(sequenceSourceKey => ({
      sequenceSourceKey,
      processId: selection.processId || '0',
      newProcessCode: selection.newProcessCode ?? undefined,
      stationId: selection.stationId || '0',
      newStationCode: selection.newStationCode ?? undefined,
      subStationId: selection.subStationId || '0',
      newSubStationCode: selection.newSubStationCode ?? undefined,
    })), batchId.value || undefined)
    const created = [
      receipt.createdProcessCount > 0 ? t('lineupCatalog_form.processCount', { count: receipt.createdProcessCount }) : '',
      receipt.createdStationCount > 0 ? t('lineupCatalog_form.stationCount', { count: receipt.createdStationCount }) : '',
      receipt.createdSubStationCount > 0 ? t('lineupCatalog_form.subStationCount', { count: receipt.createdSubStationCount }) : '',
      receipt.createdBindingCount > 0 ? t('lineupCatalog_form.structureBindingCount', { count: receipt.createdBindingCount }) : '',
    ].filter(Boolean).join(t('lineupCatalog_form.enumerationSep'))
    return { ok: true, created }
  }
  catch (error) {
    ElMessage.error(error instanceof Error && error.message ? error.message : 'HierarchyReassignFailed')
    return { ok: false, created: '' }
  }
}

async function reassignInDialog(payload: HierarchyReassignPayload) {
  const result = await callReassignApi([payload.sequenceSourceKey], payload)
  if (result.ok) {
    // 2026-09-07: 落库成功后立即重拉预览（服务端会动态合并绑定表最新归属），
    // 取代本地字段补丁 —— 本地补丁不更新 proposedSubStationCode/candidateKind，
    // 顺控行会残留改派前的旧编码（如 OP100-01），造成“改了没生效”的误解。
    try {
      await wizard.refreshPreviewFromServer()
    }
    catch {
      applyHierarchyOverrideLocal(payload, [payload.sequenceSourceKey])
    }
    ElMessage.success(result.created ? t('lineupCatalog_form.hierarchySavedWithCreated', { created: result.created }) : t('lineupCatalog_form.hierarchySaved'))
  }
}

async function reassignBatchInDialog(payload: HierarchyReassignBatchPayload) {
  const keys = payload.sequenceSourceKeys.filter(Boolean)
  if (keys.length === 0) {
    return
  }
  const result = await callReassignApi(keys, payload)
  if (result.ok) {
    // 同 reassignInDialog：批量改派落库后重拉合并绑定的最新预览，避免旧编码残留
    try {
      await wizard.refreshPreviewFromServer()
    }
    catch {
      applyHierarchyOverrideLocal(payload, keys)
    }
    ElMessage.success(result.created
      ? t('lineupCatalog_form.batchSavedWithCreated', { count: keys.length, created: result.created })
      : t('lineupCatalog_form.batchSaved', { count: keys.length }))
  }
}

const ERROR_LABELS = computed<Record<string, string>>(() => ({
  SourceBlockMissing: t('lineupCatalog_form.errSourceBlockMissing'),
  SourceFileMissing: t('lineupCatalog_form.errSourceFileMissing'),
  SourceContentMissing: t('lineupCatalog_form.errSourceContentMissing'),
  InvalidSelection: t('lineupCatalog_form.errInvalidSelection'),
  JobScheduleFailed: t('lineupCatalog_form.errJobScheduleFailed'),
  BatchCreateFailed: t('lineupCatalog_form.errBatchCreateFailed'),
  LineUpRequestFailed: t('lineupCatalog_form.errLineUpRequestFailed'),
}))

const friendlyError = computed(() =>
  ERROR_LABELS.value[errorCode.value] ?? (errorCode.value ? t('lineupCatalog_form.operationIncomplete', { code: errorCode.value }) : ''),
)

function statusText(status?: string | null) {
  const labels: Record<string, string> = {
    Uploaded: t('lineupCatalog_form.statusUploaded'),
    Parsing: t('lineupCatalog_form.statusParsing'),
    PreviewReady: t('lineupCatalog_form.statusPreviewReady'),
    Committed: t('lineupCatalog_form.statusCommitted'),
    Failed: t('lineupCatalog_form.stepFailed'),
  }
  return labels[status ?? ''] ?? t('lineupCatalog_form.statusNotStarted')
}

/** 逐设备探测该块的最近预览批次，返回命中的设备 id（找不到返回 null） */
async function pickDeviceWithExistingBatch(devices: { id: EntityId }[]): Promise<EntityId | null> {
  const blockName = props.block?.block?.blockName
  if (!blockName || !lineUpImportApi.getLatestPreviewBatch || devices.length === 0) {
    return null
  }
  for (const device of devices) {
    try {
      const latest = await lineUpImportApi.getLatestPreviewBatch(lineId.value, device.id, blockName)
      if (latest?.batchId) {
        return device.id
      }
    }
    catch {
      // 单设备探测失败继续下一个
    }
  }
  return null
}

async function loadWorkspace(requestedLineId?: EntityId) {
  workspaceLoading.value = true
  try {
    const value = await lineUpImportApi.getWorkspace(requestedLineId || null)
    workspace.value = value
    lineId.value = value.selectedLineId || value.lines[0]?.id || ''
    const candidates = [props.initialDeviceId, deviceId.value].filter(Boolean) as EntityId[]
    const matched = candidates.find(id => value.devices.some(item => item.id === id))
    if (matched) {
      deviceId.value = matched
    }
    else {
      // 无显式选择时优先挑「该块有历史预览批次」的设备，避免落到 devices[0] 上重新生成出 PlcProjectMissing 的降级预览
      deviceId.value = await pickDeviceWithExistingBatch(value.devices) ?? value.devices[0]?.id ?? ''
    }
    await probeExistingPreview()
  }
  catch {
    ElMessage.error(t('lineupCatalog_form.lineDeviceLoadFailed'))
  }
  finally {
    workspaceLoading.value = false
  }
}

/** 当前产线/设备下该块已有可预览批次时的直达入口（免上传归档缺失或想看上次结果时用） */
const existingBatchId = ref<EntityId>('')

async function probeExistingPreview() {
  existingBatchId.value = ''
  const blockName = props.block?.block?.blockName
  if (!blockName || !lineId.value || !deviceId.value) {
    return
  }
  try {
    const latest = await lineUpImportApi.getLatestPreviewBatch?.(
      lineId.value,
      deviceId.value,
      blockName,
    )
    existingBatchId.value = latest?.batchId ?? ''
  }
  catch {
    // 查不到已有批次属正常情况（从未生成过），静默忽略
  }
}

async function openExistingPreview() {
  if (!existingBatchId.value) {
    return
  }
  try {
    await wizard.restoreExistingPreview(existingBatchId.value)
  }
  catch {
    ElMessage.error(t('lineupCatalog_form.existingPreviewOpenFailed'))
  }
}

async function handleLineChange(value: EntityId) {
  lineId.value = value
  deviceId.value = ''
  await loadWorkspace(value)
}

async function generate() {
  if (!props.block || !lineId.value || !deviceId.value) {
    ElMessage.warning(t('lineupCatalog_form.selectLineAndDevice'))
    return
  }
  try {
    await wizard.startPreviewFromBlock(String(props.block.source.plcBlockId), lineId.value, deviceId.value)
  }
  catch {
    // errorCode 已在弹框内展示
  }
}

function selectSequence(sourceKey: string) {
  wizard.selectSequence(sourceKey)
}

function updateViewState(state: TiaGraphViewState) {
  if (selectedSequenceKey.value) {
    wizard.updateSequenceView(selectedSequenceKey.value, state)
  }
}

function selectNode(sourceKey: string) {
  const current = currentSequenceView.value ?? {
    zoom: 1,
    translateX: 0,
    translateY: 0,
    selectedNodeKey: '',
  }
  updateViewState({ ...current, selectedNodeKey: sourceKey })
}

async function commitStructure() {
  try {
    await wizard.commitStructure()
    ElMessage.success(t('lineupCatalog_form.structureSaved'))
  }
  catch {
    ElMessage.error(ERROR_LABELS.value[errorCode.value] ?? errorCode.value ?? t('lineupCatalog_form.saveFailedRetry'))
  }
}

function finishAndClose() {
  wizard.stopPolling()
  visibleProxy.value = false
}

watch(
  () => props.modelValue,
  (value) => {
    if (value) {
      void loadWorkspace((props.initialLineId || '') as EntityId)
    }
    else {
      wizard.stopPolling()
    }
  },
)

// 产线/设备切换后重新探测该块是否已有可预览批次（决定是否显示直达按钮）
watch([lineId, deviceId], () => {
  if (props.modelValue && currentStep.value === 1) {
    void probeExistingPreview()
  }
})

onMounted(() => {
  if (props.modelValue) {
    void loadWorkspace((props.initialLineId || '') as EntityId)
  }
})
onBeforeUnmount(() => wizard.dispose())
</script>

<template>
  <el-dialog
    v-model="visibleProxy"
    :title="$t('lineupCatalog_form.previewTitle', { name: block?.block?.blockName ?? '' })"
    width="92%"
    top="3vh"
    :close-on-click-modal="false"
    append-to-body
    class="graph-block-preview-dialog"
  >
    <div v-if="currentStep === 1" class="dialog-step dialog-step--start">
      <div class="block-brief">
        <div>
          <span>{{ $t('lineupCatalog_form.graphBlockColumn') }}</span>
          <strong>{{ block?.block?.blockName }}</strong>
        </div>
        <div>
          <span>{{ $t('lineupCatalog_form.typeNumberLabel') }}</span>
          <strong>{{ block?.block?.blockType }}{{ block?.block?.blockNumber ?? '' }}</strong>
        </div>
        <div>
          <span>BlockId</span>
          <strong>{{ block?.source?.plcBlockId }}</strong>
        </div>
        <div>
          <span>ParseRun</span>
          <strong>{{ block?.source?.parseRunId ?? '—' }}</strong>
        </div>
      </div>

      <div class="selection-grid">
        <label>
          <span>{{ $t('lineupCatalog_form.line') }}</span>
          <el-select
            v-model="lineId"
            filterable
            :placeholder="$t('lineupCatalog_form.selectLinePlaceholder')"
            :loading="workspaceLoading"
            @change="handleLineChange"
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
          <span>{{ $t('lineupCatalog_form.plcDevice') }}</span>
          <el-select
            v-model="deviceId"
            filterable
            :placeholder="$t('lineupCatalog_form.selectPlcDevicePlaceholder')"
            :loading="workspaceLoading"
          >
            <el-option
              v-for="device in workspace?.devices ?? []"
              :key="device.id"
              :label="`${device.name} · ${device.projectName}`"
              :value="device.id"
            />
          </el-select>
        </label>
        <el-button
          type="primary"
          size="large"
          :loading="isUploading || isPolling"
          :disabled="!lineId || !deviceId"
          @click="generate"
        >
          {{ $t('lineupCatalog_form.generatePreviewNoUpload') }}
        </el-button>
        <el-button
          v-if="existingBatchId"
          size="large"
          :disabled="isUploading || isPolling"
          @click="openExistingPreview"
        >
          {{ $t('lineupCatalog_form.openExistingPreview', { id: String(existingBatchId).slice(-6) }) }}
        </el-button>
      </div>

      <div v-if="progress" class="progress-strip" :data-status="progress.status">
        <span class="progress-strip__pulse" />
        <strong>{{ statusText(progress.status) }}</strong>
        <span>{{ $t('lineupCatalog_form.batchLabel', { id: progress.batchId }) }}</span>
        <span v-if="progress.errorCode">{{ $t('lineupCatalog_form.errorCodeLabel', { code: progress.errorCode }) }}</span>
      </div>

      <el-alert
        v-if="friendlyError"
        :title="friendlyError"
        type="error"
        show-icon
        :closable="false"
      />
    </div>

    <div v-else-if="currentStep === 2 && preview" class="dialog-step dialog-step--preview">
      <div class="preview-toolbar">
        <div class="preview-summary">
          <span><strong>{{ preview.summary.documentCount }}</strong> {{ $t('lineupCatalog_form.unitDocuments') }}</span>
          <span><strong>{{ preview.summary.sequenceCount }}</strong> Sequence</span>
          <span><strong>{{ preview.summary.stepCount }}</strong> {{ $t('lineupCatalog_form.unitSteps') }}</span>
          <span><strong>{{ preview.summary.transitionCount }}</strong> {{ $t('lineupCatalog_form.unitTransitions') }}</span>
          <span><strong>{{ preview.summary.actionCount }}</strong> {{ $t('lineupCatalog_form.unitActions') }}</span>
        </div>
        <div class="commit-actions">
          <span class="runtime-state">{{ runtimeStatusText }}</span>
          <el-button
            type="primary"
            :disabled="!canCommitStructure"
            :loading="isCommitting"
            @click="commitStructure"
          >
            {{ $t('lineupCatalog_form.saveGraphStructure') }}
          </el-button>
        </div>
      </div>

      <div class="preview-body">
        <HierarchyReviewPanel
          :documents="preview.documents"
          :decisions="hierarchyDecisions"
          :selected-sequence-key="selectedSequenceKey"
          :line-id="lineId"
          :device-id="deviceId"
          @select-candidate="wizard.selectHierarchyCandidate"
          @select-sequence="selectSequence"
          @reassign-hierarchy="reassignInDialog"
          @reassign-hierarchy-batch="reassignBatchInDialog"
        />

        <GraphPreviewPanel
          :sequences="displaySequences"
          :model-value="selectedSequenceKey"
          :binding-status="runtimeStatusText"
          :view-state="currentSequenceView"
          :step-tag-project-id="stepTagProjectId"
          :step-tag-fb-name="stepTagFbName"
          :bom-sub-station-id="bomSubStationId"
          :bom-references="bomReferences"
          @update:model-value="selectSequence"
          @node-click="selectNode"
          @view-state-change="updateViewState"
        />

        <aside class="issue-dock" :class="{ 'is-collapsed': issueDockCollapsed }">
          <button
            type="button"
            class="issue-dock__toggle"
            :title="issueDockCollapsed ? $t('lineupCatalog_form.expandIssues') : $t('lineupCatalog_form.collapseIssues')"
            @click="issueDockCollapsed = !issueDockCollapsed"
          >
            {{ issueDockCollapsed ? $t('lineupCatalog_form.issuesCollapsed') : '▶' }}
          </button>
          <div v-show="!issueDockCollapsed" class="issue-dock__body">
            <ImportIssueList
              :issues="allIssues"
              :sequence="selectedSequence"
              :node="selectedNode"
              :renamed-value="selectedDisplayName"
              @rename="wizard.rename"
            />
          </div>
        </aside>
      </div>
    </div>

    <div v-else class="dialog-step dialog-step--done">
      <div class="completion-icon">✓</div>
      <div class="completion-text">
        <span>{{ $t('lineupCatalog_form.structureSaveComplete') }}</span>
        <h3>{{ $t('lineupCatalog_form.sequencesWritten', { count: preview?.summary.sequenceCount ?? 0 }) }}</h3>
        <p>{{ $t('lineupCatalog_form.batchAndRevision', { batch: receipt?.batchId, revision: receipt?.previewRevision }) }}</p>
      </div>
      <el-button type="primary" @click="finishAndClose">
        {{ $t('lineupCatalog_form.stepDone') }}
      </el-button>
    </div>
  </el-dialog>
</template>

<style scoped>
.dialog-step {
  display: grid;
  gap: 14px;
}

.block-brief {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 10px;
  padding: 12px 14px;
  border: 1px solid var(--el-border-color-light);
  border-radius: 8px;
  background: var(--el-fill-color-lighter);
}

.block-brief > div {
  display: grid;
  gap: 2px;
  min-width: 0;
}

.block-brief span {
  font-size: 11px;
  color: var(--el-text-color-secondary);
}

.block-brief strong {
  font-size: 13px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.selection-grid {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr) auto;
  gap: 12px;
  align-items: end;
}

.selection-grid label {
  display: grid;
  gap: 4px;
}

.selection-grid label > span {
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.progress-strip {
  display: flex;
  gap: 12px;
  align-items: center;
  padding: 8px 14px;
  border-radius: 6px;
  border: 1px solid var(--el-border-color-light);
  background: var(--el-fill-color-light);
  font-size: 13px;
}

.progress-strip__pulse {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: var(--el-color-primary);
  animation: preview-pulse 1.2s ease-in-out infinite;
}

.progress-strip[data-status='Failed'] .progress-strip__pulse {
  background: var(--el-color-danger);
  animation: none;
}

@keyframes preview-pulse {
  50% {
    opacity: 0.35;
  }
}

.dialog-step--preview {
  gap: 10px;
}

.preview-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 8px 12px;
  border: 1px solid var(--el-border-color-light);
  border-radius: 8px;
  background: var(--el-fill-color-lighter);
}

.preview-summary {
  display: flex;
  gap: 14px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
  flex-wrap: wrap;
}

.preview-summary strong {
  color: var(--el-text-color-primary);
  font-size: 14px;
}

.commit-actions {
  display: flex;
  align-items: center;
  gap: 12px;
}

.runtime-state {
  font-size: 12px;
  color: var(--el-color-primary);
}

.preview-body {
  display: grid;
  grid-template-columns: 300px minmax(0, 1fr) auto;
  gap: 10px;
  height: calc(85vh - 210px);
  min-height: 420px;
}

.preview-body > * {
  min-width: 0;
  overflow: auto;
}

.issue-dock {
  display: flex;
  width: 300px;
  flex-direction: column;
  overflow: hidden;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 8px;
  padding: 8px;
  background: var(--el-bg-color);
  transition: width 0.18s ease;
}

.issue-dock.is-collapsed {
  width: 40px;
  padding: 8px 4px;
}

.issue-dock__toggle {
  flex-shrink: 0;
  padding: 4px 2px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
  white-space: nowrap;
  cursor: pointer;
  background: none;
  border: none;
  border-radius: 4px;
}

.issue-dock__toggle:hover {
  color: var(--el-color-primary);
  background: var(--el-fill-color-light);
}

.issue-dock.is-collapsed .issue-dock__toggle {
  writing-mode: vertical-rl;
  letter-spacing: 4px;
}

.issue-dock__body {
  min-height: 0;
  flex: 1;
  overflow: auto;
}

.dialog-step--done {
  justify-items: center;
  align-content: center;
  gap: 18px;
  padding: 42px 0;
}

.completion-icon {
  display: grid;
  place-items: center;
  width: 64px;
  height: 64px;
  border-radius: 50%;
  background: color-mix(in srgb, var(--el-color-success) 15%, transparent);
  color: var(--el-color-success);
  font-size: 30px;
}

.completion-text {
  text-align: center;
  display: grid;
  gap: 4px;
}

.completion-text span {
  font-size: 12px;
  color: var(--el-text-color-secondary);
  letter-spacing: 0.1em;
}

.completion-text h3 {
  margin: 0;
}

.completion-text p {
  margin: 0;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}
</style>
