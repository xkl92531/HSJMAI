<script setup lang="ts">
import type { TiaGraphViewState } from '../components/tiaGraphLayout'
import type {
  EntityId,
  LineUpImportNode,
  LineUpImportSequence,
  LineUpWorkspace,
  LineUpWorkspaceImport,
} from '@/api/modules/LineUp/types'
import { ElMessage } from 'element-plus'
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import lineUpImportApi from '@/api/modules/LineUp/import'
import GraphPreviewPanel from './components/GraphPreviewPanel.vue'
import HierarchyReviewPanel from './components/HierarchyReviewPanel.vue'
import ImportIssueList from './components/ImportIssueList.vue'
import LineUpImportWizard from './components/LineUpImportWizard.vue'
import { createLineUpImportWizard } from './useLineUpImportWizard'
import { generateGraphNodeImages } from './generateGraphNodeImages'
import graphNodeImageApi, { type LineUpGraphNodeImageItem } from '../../../api/modules/LineUp/graphNodeImages'
import { useI18n } from 'vue-i18n'
import messages from './components/lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}

const MAX_FILE_SIZE = 25 * 1024 * 1024
const ALLOWED_FILE_EXTENSIONS = new Set(['xml', 'ml', 'zip'])

const workspace = ref<LineUpWorkspace | null>(null)
const lineId = ref<EntityId>('')
const deviceId = ref<EntityId>('')
const selectedFile = ref<File | null>(null)
const workspaceLoading = ref(false)
const dockCollapsed = ref(true)
const dockWidth = ref(380)

const route = useRoute()
const wizard = createLineUpImportWizard({ pollIntervalMs: 3000 })

// 来自 GRAPH 结构管理页「查看流程图」跳转的目标块名，用于引导生成预览
const pendingBlockName = ref('')
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
  canCommitForMonitoring,
  runtimeStatusText,
  currentSequenceView,
  hierarchyDecisions,
  displayNameChanges,
  restoreExistingPreview,
  batchId,
} = wizard

const currentStep = computed<1 | 2 | 3>(() => receipt.value ? 3 : preview.value ? 2 : 1)
/** 工步变量（ai_plc_graphsteptag）查询参数：与预览弹框保持一致，只显示当前 graph 对应实例 DB 的变量 */
const stepTagProjectId = computed(() => preview.value?.documents[0]?.address?.plcProjectId ?? '')
const stepTagFbName = computed(() => preview.value?.documents[0]?.blockName ?? '')
/** 查看流程图 BOM 图片：按选中节点读已保存快照（AI_LineUp_GraphNodeImage，保存结构时生成） */
const savedNodeScope = computed(() => {
  if (!preview.value) {
    return null
  }
  const nodeSourceKey = currentSequenceView.value?.selectedNodeKey ?? ''
  if (!nodeSourceKey) {
    return null
  }
  return {
    lineId: preview.value.lineId,
    blockName: preview.value.documents[0]?.blockName ?? '',
    nodeSourceKey,
  }
})

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
  return displayNameChanges.value.get(item.sourceKey) ?? item.displayName
})
const workbenchStyle = computed(() => ({
  '--dock-width': dockCollapsed.value ? '44px' : `${dockWidth.value}px`,
}))

async function loadWorkspace(requestedLineId?: EntityId, requestedDeviceId?: EntityId) {
  workspaceLoading.value = true
  try {
    const value = await lineUpImportApi.getWorkspace(requestedLineId || null)
    workspace.value = value
    lineId.value = value.selectedLineId || value.lines[0]?.id || ''
    // 优先保留请求指定的设备，其次保留已选设备，最后取第一个
    const candidates = [requestedDeviceId, deviceId.value].filter(Boolean) as EntityId[]
    const matched = candidates.find(id => value.devices.some(item => item.id === id))
    deviceId.value = matched ?? value.devices[0]?.id ?? ''
  }
  catch {
    ElMessage.error(t('lineupWorkspace_form.errWorkspaceLoadFailed'))
  }
  finally {
    workspaceLoading.value = false
  }
}

async function handleLineChange(value: EntityId) {
  lineId.value = value
  deviceId.value = ''
  await loadWorkspace(value)
}

function handleFileSelected(file: File | null) {
  // 目标块引导完成（文件已选定，进入生成预览）
  pendingBlockName.value = ''
  if (!file) {
    selectedFile.value = null
    return
  }
  const extension = file.name.split('.').pop()?.toLocaleLowerCase() ?? ''
  if (!ALLOWED_FILE_EXTENSIONS.has(extension)) {
    selectedFile.value = null
    ElMessage.warning(t('lineupWorkspace_form.warnFileExtension'))
    return
  }
  if (file.size > MAX_FILE_SIZE) {
    selectedFile.value = null
    ElMessage.warning(t('lineupWorkspace_form.warnFileSize'))
    return
  }
  selectedFile.value = file
}

async function startPreview() {
  if (!selectedFile.value || !lineId.value || !deviceId.value) {
    ElMessage.warning(t('lineupWorkspace_form.warnMissingSelection'))
    return
  }
  try {
    await wizard.startPreview(selectedFile.value, lineId.value, deviceId.value)
  }
  catch {
    ElMessage.error(errorCode.value || t('lineupWorkspace_form.errPreviewStartFailed'))
  }
}

async function openImport(item: LineUpWorkspaceImport) {
  try {
    selectedFile.value = new File([], item.sourceFileName)
    lineId.value = item.lineId
    deviceId.value = item.deviceId
    if (item.status === 'PreviewReady') {
      wizard.acceptPreview(await lineUpImportApi.getPreview(item.batchId))
    }
    else {
      wizard.startPolling(item.batchId)
    }
  }
  catch {
    ElMessage.error(t('lineupWorkspace_form.errPreviewLoadFailed'))
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
    ElMessage.success(t('lineupWorkspace_form.msgStructureSaved'))
    void generateNodeImagesAfterCommit()
    await loadWorkspace(lineId.value)
  }
  catch {
    ElMessage.error(errorCode.value || t('lineupWorkspace_form.errCommitFailed'))
  }
}

/** 保存结构成功后：按节点渲染"成品图+匹配BOM红点"并上传 AI_LineUp_GraphNodeImage（查看流程图直接读库）。
 * onlyIfMissing=true 供「查看流程图」打开时自愈补生成（提交请求曾超时/中断时快照会缺失）。 */
async function generateNodeImagesAfterCommit(options?: { onlyIfMissing?: boolean }) {
  try {
    const batch = wizard.batchId.value
    const previewData = preview.value
    if (!batch || !previewData) {
      return
    }
    const subStationBySequence = new Map<EntityId, EntityId>()
    for (const document of previewData.documents) {
      for (const resolution of document.hierarchy.sequences) {
        const subStationId = resolution.subStationId
          ?? resolution.candidates.find(candidate => candidate.subStationId)?.subStationId
          ?? null
        if (subStationId) {
          subStationBySequence.set(resolution.sequenceSourceKey, subStationId)
        }
      }
    }
    if (subStationBySequence.size === 0) {
      return
    }
    if (options?.onlyIfMissing) {
      const seqs = wizard.sequences.value
      const firstNode = seqs.find(seq => subStationBySequence.has(seq.sourceKey))?.nodes[0]
      if (firstNode) {
        try {
          const existing = await graphNodeImageApi.listByNode(previewData.lineId, previewData.documents[0]?.blockName ?? '', firstNode.sourceKey)
          if (existing.some((item: LineUpGraphNodeImageItem) => String(item.batchId) === String(batch))) {
            return
          }
        }
        catch {
          // 探测失败按缺失处理，交给生成流程本身
        }
      }
    }
    const result = await generateGraphNodeImages({
      batchId: batch,
      lineId: previewData.lineId,
      deviceId: previewData.deviceId,
      blockName: previewData.documents[0]?.blockName ?? '',
      sequences: wizard.sequences.value,
      subStationBySequence: subStationBySequence as unknown as Map<string, EntityId>,
    })
    if (result.uploaded > 0) {
      ElMessage.success(options?.onlyIfMissing
        ? t('lineupWorkspace_form.msgSnapshotsRegenerated', { count: result.uploaded })
        : t('lineupWorkspace_form.msgSnapshotsGenerated', { count: result.uploaded }))
    }
  }
  catch {
    // 快照生成失败不阻断保存结构
  }
}

async function commitForMonitoring() {
  try {
    await wizard.commitForMonitoring()
    ElMessage.success(t('lineupWorkspace_form.msgMonitoringEnabled'))
    void generateNodeImagesAfterCommit()
    await loadWorkspace(lineId.value)
  }
  catch {
    ElMessage.error(errorCode.value || t('lineupWorkspace_form.errMonitoringEnableFailed'))
  }
}

/** 面板层级改派的目标层级选择：三级既有 Id 或新编码（Id 为空表示该级新建），带标签用于原地刷新左侧树 */
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
  /** 已落库绑定时走改派 API；否则本地调整，保存结构时落库 */
  persisted?: boolean
}

interface HierarchyReassignBatchPayload extends HierarchyReassignSelection {
  sequenceSourceKeys: string[]
  /** 已落库绑定的顺控子集（走 API），其余本地调整 */
  persistedKeys?: string[]
}

/** 层级改派：已保存绑定的直接改，未保存的带批次 Id 由后端直接新建绑定（含新编码新建层级） */
async function reassignHierarchyBinding(payload: HierarchyReassignPayload) {
  const result = await reassignHierarchySelection([payload.sequenceSourceKey], payload)
  announceReassignResult(result, 1)
}

/** 批量层级改派：工序/工站行「改」→ 全部顺控统一改指到同一组层级目标 */
async function reassignHierarchyBindingsBatch(payload: HierarchyReassignBatchPayload) {
  const keys = payload.sequenceSourceKeys.filter(Boolean)
  if (keys.length === 0) {
    return
  }
  const result = await reassignHierarchySelection(keys, payload)
  announceReassignResult(result, keys.length)
}

async function reassignHierarchySelection(
  keys: string[],
  selection: HierarchyReassignSelection,
): Promise<{ ok: boolean, created: string }> {
  if (!lineId.value || !deviceId.value || !preview.value || keys.length === 0) {
    return { ok: false, created: '' }
  }
  const reassign = lineUpImportApi.reassignHierarchy
  if (!reassign) {
    ElMessage.error(t('lineupWorkspace_form.errHierarchyReassignUnavailable'))
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
    // 2026-09-07: 落库成功后重拉预览（读接口动态合并绑定表最新归属），
    // 本地字段补丁不更新 proposedSubStationCode/candidateKind，会残留改派前旧编码造成误解。
    try {
      await wizard.refreshPreviewFromServer()
    }
    catch {
      applyHierarchyOverride(selection, keys)
    }
    const created = [
      receipt.createdProcessCount > 0 ? t('lineupWorkspace_form.createdProcess', { count: receipt.createdProcessCount }) : '',
      receipt.createdStationCount > 0 ? t('lineupWorkspace_form.createdStation', { count: receipt.createdStationCount }) : '',
      receipt.createdSubStationCount > 0 ? t('lineupWorkspace_form.createdSubStation', { count: receipt.createdSubStationCount }) : '',
      receipt.createdBindingCount > 0 ? t('lineupWorkspace_form.createdBinding', { count: receipt.createdBindingCount }) : '',
    ].filter(Boolean).join('、')
    return { ok: true, created }
  }
  catch (error) {
    ElMessage.error(error instanceof Error && error.message ? error.message : t('lineupWorkspace_form.errHierarchyReassignFailed'))
    return { ok: false, created: '' }
  }
}

function announceReassignResult(result: { ok: boolean, created: string }, count: number) {
  if (!result.ok) {
    return
  }
  const scope = count > 1
    ? t('lineupWorkspace_form.msgBatchReassigned', { count })
    : t('lineupWorkspace_form.msgHierarchySaved')
  ElMessage.success(result.created ? t('lineupWorkspace_form.msgSavedWithCreated', { scope, created: result.created }) : scope)
}

function applyHierarchyOverride(selection: HierarchyReassignSelection & { depth?: number }, keys: string[]) {
  const keySet = new Set(keys)
  // 只覆盖本次编辑触达的层级：id 与 new*Code 均为空表示该级未编辑，保持原值
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

function startNewImport() {
  wizard.stopPolling()
  window.location.reload()
}

onMounted(async () => {
  // 支持 GRAPH 结构管理页「查看流程图」跳转带入的产线/设备定位参数与目标块名
  const queryLineId = String(route.query.lineId ?? '')
  const queryDeviceId = String(route.query.deviceId ?? '')
  pendingBlockName.value = String(route.query.blockName ?? '')
  await loadWorkspace(queryLineId || undefined, queryDeviceId || undefined)
  // 该块已有可预览批次时，直达最新一条预览（跳过手动选文件步骤）
  if (pendingBlockName.value && lineId.value && deviceId.value) {
    try {
      const latest = await lineUpImportApi.getLatestPreviewBatch(lineId.value, deviceId.value, pendingBlockName.value)
      if (latest?.batchId) {
        await restoreExistingPreview(latest.batchId)
        ElMessage.success(t('lineupWorkspace_form.msgAutoLoadedPreview', { name: pendingBlockName.value }))
        // 自愈：提交请求曾超时/中断导致快照缺失时，打开流程图即补生成（已有则秒跳过）
        void generateNodeImagesAfterCommit({ onlyIfMissing: true })
      }
    }
    catch {
      // 无可预览批次或加载失败：保持第一步引导用户选择文件
    }
  }
})
onBeforeUnmount(() => wizard.dispose())
</script>

<template>
  <main class="lineup-workspace">
    <header class="page-heading">
      <div>
        <span>LINEUP / GRAPH IMPORT</span>
        <h2>{{ $t('lineupWorkspace_form.title') }}</h2>
        <p>{{ $t('lineupWorkspace_form.intro') }}</p>
      </div>
      <div class="phase-badge">
        <strong>Phase 1</strong>
        <span>{{ $t('lineupWorkspace_form.phaseLabel') }}</span>
      </div>
    </header>

    <el-alert
      v-if="pendingBlockName && currentStep === 1"
      type="info"
      show-icon
      :closable="true"
      class="graph-block-hint"
      @close="pendingBlockName = ''"
    >
      <template #title>
        {{ $t('lineupWorkspace_form.hintFlowHead') }}<strong>{{ pendingBlockName }}</strong>{{ $t('lineupWorkspace_form.hintFlowMid') }}<strong>{{ pendingBlockName }}.xml</strong>{{ $t('lineupWorkspace_form.hintFlowTail') }}
      </template>
    </el-alert>

    <LineUpImportWizard
      :workspace="workspace"
      :line-id="lineId"
      :device-id="deviceId"
      :file-name="selectedFile?.name ?? preview?.sourceFileName ?? ''"
      :step="currentStep"
      :progress="progress"
      :loading="workspaceLoading"
      :uploading="isUploading || isPolling"
      @line-change="handleLineChange"
      @device-change="deviceId = $event"
      @file-selected="handleFileSelected"
      @start="startPreview"
      @open-import="openImport"
    />

    <template v-if="currentStep === 2 && preview">
      <section class="preview-toolbar">
        <div class="preview-summary">
          <span><strong>{{ preview.summary.documentCount }}</strong> {{ $t('lineupWorkspace_form.unitDocuments') }}</span>
          <span><strong>{{ preview.summary.sequenceCount }}</strong> Sequence</span>
          <span><strong>{{ preview.summary.stepCount }}</strong> {{ $t('lineupWorkspace_form.unitSteps') }}</span>
          <span><strong>{{ preview.summary.transitionCount }}</strong> {{ $t('lineupWorkspace_form.unitTransitions') }}</span>
          <span><strong>{{ preview.summary.actionCount }}</strong> {{ $t('lineupWorkspace_form.unitActions') }}</span>
          <span><strong>{{ preview.summary.edgeCount }}</strong> {{ $t('lineupWorkspace_form.unitEdges') }}</span>
        </div>
        <div class="commit-actions">
          <span class="runtime-state">{{ runtimeStatusText }}</span>
          <el-tooltip :content="$t('lineupWorkspace_form.tipCommitMonitoring')" placement="bottom">
            <el-button :disabled="!canCommitForMonitoring" :loading="isCommitting" @click="commitForMonitoring">
              {{ $t('lineupWorkspace_form.saveEnableMonitoring') }}
            </el-button>
          </el-tooltip>
          <el-button type="primary" :disabled="!canCommitStructure" :loading="isCommitting" @click="commitStructure">
            {{ $t('lineupWorkspace_form.saveGraphStructure') }}
          </el-button>
        </div>
      </section>

      <section class="preview-workbench" :style="workbenchStyle">
        <HierarchyReviewPanel
          :documents="preview.documents"
          :decisions="hierarchyDecisions"
          :selected-sequence-key="selectedSequenceKey"
          :line-id="lineId"
          :device-id="deviceId"
          @select-candidate="wizard.selectHierarchyCandidate"
          @select-sequence="selectSequence"
          @reassign-hierarchy="reassignHierarchyBinding"
          @reassign-hierarchy-batch="reassignHierarchyBindingsBatch"
        />

        <GraphPreviewPanel
          :sequences="displaySequences"
          :model-value="selectedSequenceKey"
          :binding-status="runtimeStatusText"
          :view-state="currentSequenceView"
          :step-tag-project-id="stepTagProjectId"
          :step-tag-fb-name="stepTagFbName"
          :saved-node-images="savedNodeScope"
          @update:model-value="selectSequence"
          @node-click="selectNode"
          @view-state-change="updateViewState"
        />

        <aside class="detail-dock" :class="{ 'is-collapsed': dockCollapsed }">
          <button type="button" class="dock-toggle" :title="dockCollapsed ? $t('lineupWorkspace_form.expandDetail') : $t('lineupWorkspace_form.collapseDetail')" @click="dockCollapsed = !dockCollapsed">
            {{ dockCollapsed ? '‹' : '›' }}
          </button>
          <template v-if="!dockCollapsed">
            <div class="dock-resize">
              <span>{{ $t('lineupWorkspace_form.panelWidth') }}</span>
              <el-slider v-model="dockWidth" :min="320" :max="560" :show-tooltip="false" />
              <strong>{{ dockWidth }}px</strong>
            </div>
            <ImportIssueList
              :issues="allIssues"
              :sequence="selectedSequence"
              :node="selectedNode"
              :renamed-value="selectedDisplayName"
              @rename="wizard.rename"
            />
          </template>
        </aside>
      </section>
    </template>

    <section v-else-if="currentStep === 3 && receipt" class="completion-card">
      <div class="completion-icon">
        ✓
      </div>
      <div>
        <span>{{ $t('lineupWorkspace_form.structureSaved') }}</span>
        <h3>{{ $t('lineupWorkspace_form.sequencesWritten', { n: preview?.summary.sequenceCount ?? 0 }) }}</h3>
        <p>{{ $t('lineupWorkspace_form.receiptSummary', { batch: receipt.batchId, revision: receipt.previewRevision }) }}</p>
      </div>
      <div class="completion-actions">
        <el-button type="primary" @click="startNewImport">
          {{ $t('lineupWorkspace_form.continueImport') }}
        </el-button>
        <el-tooltip :content="$t('lineupWorkspace_form.tipEnterMonitor')">
          <el-button :disabled="!canCommitForMonitoring" @click="startNewImport">
            {{ $t('lineupWorkspace_form.enterMonitor') }}
          </el-button>
        </el-tooltip>
      </div>
    </section>
  </main>
</template>

<style scoped>
.lineup-workspace {
  --workspace-gap: 12px;

  display: grid;
  gap: var(--workspace-gap);
  min-width: 0;
  min-height: calc(100vh - 122px);
  padding: 14px;
  background:
    linear-gradient(135deg, color-mix(in srgb, var(--el-color-primary) 5%, transparent), transparent 34%),
    var(--el-bg-color-page);
}

.page-heading {
  display: flex;
  gap: 20px;
  align-items: center;
  justify-content: space-between;
}

.page-heading > div:first-child > span {
  font: 700 10px Consolas, monospace;
  color: var(--el-color-primary);
  letter-spacing: 0.12em;
}

.page-heading h2 {
  margin: 3px 0;
  font-size: 22px;
}

.page-heading p {
  margin: 0;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.phase-badge {
  display: grid;
  padding: 8px 12px;
  text-align: right;
  background: var(--el-color-primary-light-9);
  border: 1px solid var(--el-color-primary-light-7);
  border-radius: 6px;
}

.phase-badge strong {
  font: 700 12px Consolas, monospace;
  color: var(--el-color-primary);
}

.phase-badge span {
  font-size: 10px;
  color: var(--el-text-color-secondary);
}

.preview-toolbar {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  align-items: center;
  justify-content: space-between;
  padding: 9px 12px;
  background: var(--el-bg-color);
  border: 1px solid var(--el-border-color-light);
  border-radius: 7px;
}

.preview-summary,
.commit-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px 14px;
  align-items: center;
}

.preview-summary span {
  font-size: 11px;
  color: var(--el-text-color-secondary);
}

.preview-summary strong {
  font-family: Consolas, monospace;
  color: var(--el-text-color-primary);
}

.runtime-state {
  padding: 4px 8px;
  font-size: 11px;
  color: var(--el-color-warning-dark-2);
  background: var(--el-color-warning-light-9);
  border-radius: 4px;
}

.preview-workbench {
  display: grid;
  grid-template-columns: minmax(280px, 340px) minmax(0, 1fr) var(--dock-width);
  gap: var(--workspace-gap);
  min-width: 0;
  height: calc(100vh - 460px);
  min-height: 340px;
  overflow: hidden;
}

.detail-dock {
  position: relative;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
  background: var(--el-bg-color);
  border: 1px solid var(--el-border-color-light);
  border-radius: 8px;
}

.detail-dock.is-collapsed {
  background: var(--el-fill-color-light);
}

.dock-toggle {
  position: absolute;
  top: 8px;
  right: 8px;
  z-index: 3;
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  font-size: 19px;
  cursor: pointer;
  background: var(--el-bg-color);
  border: 1px solid var(--el-border-color);
  border-radius: 4px;
}

.detail-dock.is-collapsed .dock-toggle {
  right: 7px;
}

.dock-resize {
  display: grid;
  grid-template-columns: auto minmax(60px, 1fr) auto;
  gap: 8px;
  align-items: center;
  height: 45px;
  padding: 0 46px 0 14px;
  font-size: 10px;
  color: var(--el-text-color-secondary);
  border-bottom: 1px solid var(--el-border-color-lighter);
}

.dock-resize strong {
  font-family: Consolas, monospace;
}

.detail-dock :deep(.issue-panel) {
  height: calc(100% - 45px);
}

.completion-card {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr) auto;
  gap: 18px;
  align-items: center;
  min-height: 260px;
  padding: 30px;
  background: var(--el-bg-color);
  border: 1px solid var(--el-color-success-light-5);
  border-radius: 9px;
}

.completion-icon {
  display: grid;
  place-items: center;
  width: 58px;
  height: 58px;
  font-size: 30px;
  color: white;
  background: var(--el-color-success);
  border-radius: 50%;
}

.completion-card span {
  font-size: 12px;
  font-weight: 700;
  color: var(--el-color-success);
}

.completion-card h3 {
  margin: 4px 0;
}

.completion-card p {
  margin: 0;
  color: var(--el-text-color-secondary);
}

.completion-actions {
  display: flex;
  gap: 8px;
}

@media (width <= 1280px) {
  .preview-workbench {
    grid-template-columns: 290px minmax(0, 1fr) var(--dock-width);
  }
}

@media (width <= 980px) {
  .preview-workbench {
    grid-template-columns: minmax(240px, 300px) minmax(0, 1fr);
    overflow: auto;
  }

  .detail-dock {
    grid-column: 1 / -1;
    min-height: 480px;
  }

  .detail-dock.is-collapsed {
    min-height: 44px;
  }
}
</style>
