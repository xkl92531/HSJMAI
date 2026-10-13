<script setup lang="ts">
import type { TreeNodeData } from 'element-plus'
import type { LineUpGraphCatalogLookup } from '@/api/modules/LineUp/catalog'
import type {
  EntityId,
  LineUpHierarchyCandidate,
  LineUpImportDocument,
} from '@/api/modules/LineUp/types'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import lineUpGraphCatalogApi from '@/api/modules/LineUp/catalog'
import {
  normalizeCandidateKind,
  normalizeConfidence,
} from '@/api/modules/LineUp/types'
import { useI18n } from 'vue-i18n'
import messages from './lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}

interface HierarchyTreeNode {
  id: string
  label: string
  kind: 'document' | 'process' | 'station' | 'sequence' | 'candidate' | 'empty'
  sequenceSourceKey?: string
  /** document 节点携带：用于启动监控按钮定位对应文档（与 documents 里的 blockName 匹配） */
  blockName?: string
  candidate?: LineUpHierarchyCandidate
  /** process/station 节点携带：批量改派按钮预填当前层级用 */
  processId?: EntityId
  stationId?: EntityId
  children?: HierarchyTreeNode[]
}

/** 层级改派载荷：带选中层级的编码/名称，便于父级直接刷新左侧树显示 */
interface HierarchyReassignPayload {
  sequenceSourceKey: string
  sequenceLabel: string
  /** 该顺控已有落库绑定时为 true（父级走改派 API 落库）；否则父级本地调整，保存结构时落库 */
  persisted?: boolean
  /** 本次编辑触达的深度：1=工序 2=工站 3=子工站（父级本地覆盖只动触达层级） */
  depth?: number
  /** 既有层级 Id；'' 表示该级用 new*Code 新建 */
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

const props = defineProps<{
  documents: LineUpImportDocument[]
  decisions: Map<string, string>
  selectedSequenceKey: string
  lineId?: number | string
  deviceId?: number | string
}>()

/** 批量改派载荷：对全部可改派顺控统一应用同一组层级目标 */
interface HierarchyReassignBatchPayload {
  sequenceSourceKeys: string[]
  /** 已落库绑定的顺控子集（父级对这批走改派 API，其余本地调整） */
  persistedKeys?: string[]
  /** 本次批量编辑触达的深度：1=工序 2=工站 */
  depth?: number
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

const emit = defineEmits<{
  selectCandidate: [sequenceSourceKey: string, candidateKey: string]
  selectSequence: [sourceKey: string]
  reassignHierarchy: [payload: HierarchyReassignPayload]
  reassignHierarchyBatch: [payload: HierarchyReassignBatchPayload]
}>()

const searchText = ref('')
const reviewOnly = ref(false)

// el-tree-v2 虚拟滚动需要显式高度：跟随容器实测高度，避免写死 540 在矮容器里被裁剪后无法滚动
const treeShell = ref<HTMLDivElement | null>(null)
const treeHeight = ref(540)
let treeShellObserver: ResizeObserver | null = null

onMounted(() => {
  treeShellObserver = new ResizeObserver((entries) => {
    const height = entries[0]?.contentRect.height ?? 0
    if (height > 0) {
      treeHeight.value = Math.round(height)
    }
  })
  if (treeShell.value) {
    treeShellObserver.observe(treeShell.value)
  }
})

onBeforeUnmount(() => {
  treeShellObserver?.disconnect()
  treeShellObserver = null
})

function compact(...values: Array<string | null | undefined>) {
  return values.map(value => value?.trim()).filter(Boolean).join(' · ')
}

/** 名称优先展示；候选缺 code/name 时用层级基础数据反查（避免显示裸 Id） */
function lookupLabel(
  list: Array<{ id: EntityId, code: string | null, name: string | null }>,
  id: EntityId | number | null | undefined,
) {
  if (id == null || id === '') {
    return ''
  }
  const found = list.find(item => sameId(item.id, id))
  return found ? (compact(found.name, found.code) || String(found.id)) : ''
}

function processLabel(candidate: LineUpHierarchyCandidate) {
  return compact(candidate.processName, candidate.processCode)
    || lookupLabel(lookups.value?.processes ?? [], candidate.processId)
    || t('lineupWorkspace_form.fallbackProcess', { id: candidate.processId })
}

function stationLabel(candidate: LineUpHierarchyCandidate) {
  return compact(candidate.stationName, candidate.stationCode)
    || lookupLabel(lookups.value?.stations ?? [], candidate.stationId)
    || t('lineupWorkspace_form.fallbackStation', { id: candidate.stationId })
}

function subStationLabel(candidate: LineUpHierarchyCandidate) {
  const kind = normalizeCandidateKind(candidate.candidateKind)
  if (kind === 'ProposedSubStation') {
    return compact(candidate.proposedSubStationName, candidate.proposedSubStationCode) || t('lineupWorkspace_form.newSubStationLabel')
  }
  if (candidate.subStationId) {
    return compact(candidate.subStationName, candidate.subStationCode)
      || lookupLabel(lookups.value?.subStations ?? [], candidate.subStationId)
      || t('lineupWorkspace_form.fallbackSubStation', { id: candidate.subStationId })
  }
  return t('lineupWorkspace_form.pendingSubStation')
}

/** 可信度排序：Automatic 最可信（无绑定顺控默认展示可信度最高的候选） */
const CONFIDENCE_ORDER: Record<string, number> = { Automatic: 0, Suggested: 1, NeedsReview: 2 }

/** 顺控的生效候选：人工决策 > 服务端选中 > 可信度最高。有绑定时服务端已选中 ExistingBinding。 */
function effectiveCandidate(resolution: LineUpImportDocument['hierarchy']['sequences'][number]): LineUpHierarchyCandidate | null {
  const decisionKey = props.decisions.get(resolution.sequenceSourceKey)
  if (decisionKey) {
    const decided = resolution.candidates.find(c => c.candidateKey === decisionKey)
    if (decided) {
      return decided
    }
  }
  if (resolution.selectedCandidateKey) {
    const selected = resolution.candidates.find(c => c.candidateKey === resolution.selectedCandidateKey)
    if (selected) {
      return selected
    }
  }
  return [...resolution.candidates].sort((a, b) =>
    (CONFIDENCE_ORDER[normalizeConfidence(a.confidenceLevel)] ?? 9)
      - (CONFIDENCE_ORDER[normalizeConfidence(b.confidenceLevel)] ?? 9))[0] ?? null
}

function buildDocumentTree(document: LineUpImportDocument): HierarchyTreeNode {
  const sequenceByKey = new Map(document.sequences.map(item => [item.sourceKey, item]))
  const processGroups = new Map<string, {
    label: string
    stations: Map<string, { label: string, choices: Array<{ sequenceKey: string, candidate: LineUpHierarchyCandidate }> }>
  }>()
  const unresolved: HierarchyTreeNode[] = []

  // 每个顺控只展示一条生效路径（绑定值或可信度最高的候选），不铺开全部候选组合
  for (const resolution of document.hierarchy.sequences) {
    const sequence = sequenceByKey.get(resolution.sequenceSourceKey)
    const sequenceLabel = sequence?.displayName || sequence?.sourceName || resolution.sequenceSourceKey
    const candidate = effectiveCandidate(resolution)
    if (!candidate) {
      unresolved.push({
        id: `empty:${document.sourceSha256}:${resolution.sequenceSourceKey}`,
        label: t('lineupWorkspace_form.noCandidates', { name: sequenceLabel }),
        kind: 'empty',
        sequenceSourceKey: resolution.sequenceSourceKey,
      })
      continue
    }
    // 「待确认」视图：只显示生效层级仍非自动确认（或已绑定）的顺控
    if (reviewOnly.value
      && normalizeConfidence(candidate.confidenceLevel) === 'Automatic'
      && normalizeCandidateKind(candidate.candidateKind) !== 'ExistingBinding') {
      continue
    }

    const processKey = candidate.processId || `process:${candidate.candidateKey}`
    const stationKey = candidate.stationId || `station:${candidate.candidateKey}`
    if (!processGroups.has(processKey)) {
      processGroups.set(processKey, { label: processLabel(candidate), stations: new Map() })
    }
    const process = processGroups.get(processKey)!
    if (!process.stations.has(stationKey)) {
      process.stations.set(stationKey, { label: stationLabel(candidate), choices: [] })
    }
    process.stations.get(stationKey)!.choices.push({
      sequenceKey: resolution.sequenceSourceKey,
      candidate,
    })
  }

  const children = [...processGroups.entries()].map(([processId, process]) => ({
    id: `process:${document.sourceSha256}:${processId}`,
    label: t('lineupWorkspace_form.groupProcess', { name: process.label }),
    kind: 'process' as const,
    processId: /^-?\d+$/.test(String(processId)) ? String(processId) : undefined,
    children: [...process.stations.entries()].map(([stationId, station]) => ({
      id: `station:${document.sourceSha256}:${stationId}`,
      label: t('lineupWorkspace_form.groupStation', { name: station.label }),
      kind: 'station' as const,
      processId: /^-?\d+$/.test(String(processId)) ? String(processId) : undefined,
      stationId: /^-?\d+$/.test(String(stationId)) ? String(stationId) : undefined,
      children: station.choices.map(({ sequenceKey, candidate }) => {
        const sequence = sequenceByKey.get(sequenceKey)
        return {
          id: `candidate:${candidate.candidateKey}`,
          label: t('lineupWorkspace_form.candidateNode', { name: sequence?.displayName || sequence?.sourceName || sequenceKey, target: subStationLabel(candidate) }),
          kind: 'candidate' as const,
          sequenceSourceKey: sequenceKey,
          candidate,
        }
      }),
    })),
  }))

  return {
    id: `document:${document.sourceSha256}`,
    label: `${document.blockName} / FB${document.blockNumber}`,
    kind: 'document',
    blockName: document.blockName,
    children: [...children, ...unresolved],
  }
}

function filterTree(nodes: HierarchyTreeNode[], keyword: string): HierarchyTreeNode[] {
  if (!keyword) {
    return nodes
  }
  const normalized = keyword.toLocaleLowerCase('zh-CN')
  return nodes.flatMap((node) => {
    const children = node.children ? filterTree(node.children, keyword) : []
    const selfMatches = `${node.label} ${node.sequenceSourceKey ?? ''} ${node.candidate?.candidateKey ?? ''}`
      .toLocaleLowerCase('zh-CN')
      .includes(normalized)
    if (!selfMatches && children.length === 0) {
      return []
    }
    return [{ ...node, children: node.children ? (selfMatches ? node.children : children) : undefined }]
  })
}

const treeData = computed(() => filterTree(
  props.documents.map(buildDocumentTree),
  searchText.value.trim(),
))

const defaultExpandedKeys = computed(() => treeData.value.flatMap((document) => {
  const firstProcess = document.children?.[0]
  const firstStation = firstProcess?.children?.[0]
  return [document.id, firstProcess?.id, firstStation?.id].filter(Boolean) as string[]
}))

function handleNodeClick(value: TreeNodeData) {
  const data = value as HierarchyTreeNode
  if (data.sequenceSourceKey) {
    emit('selectSequence', data.sequenceSourceKey)
  }
  if (data.candidate && data.sequenceSourceKey) {
    emit('selectCandidate', data.sequenceSourceKey, data.candidate.candidateKey)
  }
}

function confidenceText(candidate?: LineUpHierarchyCandidate) {
  if (!candidate) {
    return ''
  }
  const value = normalizeConfidence(candidate.confidenceLevel)
  if (value === 'Automatic') {
    return t('lineupWorkspace_form.confidenceAutomatic')
  }
  if (value === 'Suggested') {
    return t('lineupWorkspace_form.confidenceSuggested')
  }
  return t('lineupWorkspace_form.confidenceNeedsReview')
}

// ---- 层级改派（自动识别结果可编辑：工序/工站/子工站级联选择，保存到既有结构绑定） ----
const lookups = ref<LineUpGraphCatalogLookup | null>(null)
const lookupsLoading = ref(false)
const editorVisible = ref(false)
const editorMode = ref<'single' | 'batch'>('single')
/** 编辑深度：1=只改工序（批量，工站/子工站保持不变）；2=改工序+工站（批量，子工站保持不变）；3=改子工站（单顺控） */
const editorDepth = ref<1 | 2 | 3>(3)
const editorSequenceKey = ref('')
const editorSequenceLabel = ref('')
const editProcessId = ref('')
const editStationId = ref('')
const editSubStationId = ref('')
/** 无绑定顺控的单条编辑：三级级联全开（工序→工站→子工站一次选全，保存结构时落库） */
const editorFullCascade = ref(false)

function sameId(a: number | string | null | undefined, b: number | string | null | undefined) {
  return a != null && b != null && String(a) === String(b)
}

async function ensureLookups() {
  if (lookups.value || lookupsLoading.value || !props.lineId) {
    return
  }
  lookupsLoading.value = true
  try {
    lookups.value = await lineUpGraphCatalogApi.getLookups({ lineId: String(props.lineId) })
  }
  catch {
    ElMessage.error(t('lineupWorkspace_form.errLookupsLoadFailed'))
  }
  finally {
    lookupsLoading.value = false
  }
}

const lineProcesses = computed(() =>
  (lookups.value?.processes ?? []).filter(item => sameId(item.lineId, props.lineId)))

const editStations = computed(() =>
  (lookups.value?.stations ?? []).filter(item =>
    sameId(item.lineId, props.lineId) && sameId(item.processId, editProcessId.value)))

const editSubStations = computed(() =>
  (lookups.value?.subStations ?? []).filter(item =>
    sameId(item.lineId, props.lineId)
    && sameId(item.stationId, editStationId.value)
    && item.plcId != null && sameId(item.plcId, props.deviceId)))

/** 全线工站全集（不按当前选择过滤），用于识别“选了存在但层级不符”的误选 */
const stationsOfLine = computed(() =>
  (lookups.value?.stations ?? []).filter(item => sameId(item.lineId, props.lineId)))

watch(editProcessId, () => {
  if (!editStations.value.some(item => sameId(item.id, editStationId.value))) {
    editStationId.value = ''
  }
})

watch(editStationId, () => {
  if (!editSubStations.value.some(item => sameId(item.id, editSubStationId.value))) {
    editSubStationId.value = ''
  }
})

/** 顺控节点一律可「改」：已保存绑定的走改派落库；未保存的本地调整归属，保存结构时落库 */
function canReassign(node: HierarchyTreeNode): boolean {
  return Boolean(node.candidate && node.sequenceSourceKey)
}

/** 全部顺控（「改」按钮对所有顺控可用） */
const reassignableKeys = computed(() => {
  const keys = new Set<string>()
  for (const document of props.documents) {
    for (const resolution of document.hierarchy.sequences) {
      keys.add(resolution.sequenceSourceKey)
    }
  }
  return [...keys]
})

/** 已落库既有绑定（ExistingBinding）的顺控：这些可以直接调后端改派 API 落库 */
const persistedKeys = computed(() => {
  const keys = new Set<string>()
  for (const document of props.documents) {
    for (const resolution of document.hierarchy.sequences) {
      if (resolution.candidates.some(candidate =>
        normalizeCandidateKind(candidate.candidateKind) === 'ExistingBinding')) {
        keys.add(resolution.sequenceSourceKey)
      }
    }
  }
  return [...keys]
})

/** 工序/工站层级节点的批量「改」：对全部可改派顺控统一应用同一目标 */
function canBatchReassign(node: HierarchyTreeNode): boolean {
  return (node.kind === 'process' || node.kind === 'station') && reassignableKeys.value.length > 0
}

async function openHierarchyEditor(node: HierarchyTreeNode) {
  if (!canReassign(node) || !node.candidate) {
    return
  }
  await ensureLookups()
  editorMode.value = 'single'
  editorDepth.value = 3
  editorSequenceKey.value = node.sequenceSourceKey ?? ''
  editorSequenceLabel.value = node.label
  // 已落库绑定预填当前值；未保存绑定的顺控显示空，三级级联全开由用户选全目标层级
  const persisted = normalizeCandidateKind(node.candidate.candidateKind) === 'ExistingBinding'
  editorFullCascade.value = !persisted
  editProcessId.value = persisted && node.candidate.processId ? String(node.candidate.processId) : ''
  editStationId.value = persisted && node.candidate.stationId ? String(node.candidate.stationId) : ''
  editSubStationId.value = persisted && node.candidate.subStationId ? String(node.candidate.subStationId) : ''
  editorVisible.value = true
}

async function openHierarchyBatchEditor(node: HierarchyTreeNode) {
  if (!canBatchReassign(node)) {
    return
  }
  await ensureLookups()
  editorMode.value = 'batch'
  editorDepth.value = node.kind === 'process' ? 1 : 2
  editorFullCascade.value = false
  editorSequenceKey.value = ''
  editorSequenceLabel.value = ''
  editProcessId.value = node.processId ?? ''
  editStationId.value = node.kind === 'station' ? (node.stationId ?? '') : ''
  editSubStationId.value = ''
  editorVisible.value = true
}

function optionLabel(code: string | null, name: string | null, fallbackId: EntityId) {
  return compact(name, code) || String(fallbackId)
}

const editorTitle = computed(() => {
  if (editorMode.value === 'batch') {
    const count = reassignableKeys.value.length
    return editorDepth.value === 1
      ? t('lineupWorkspace_form.batchEditProcessTitle', { count })
      : t('lineupWorkspace_form.batchEditStationTitle', { count })
  }
  return t('lineupWorkspace_form.adjustSubStationTitle', { name: editorSequenceLabel.value })
})

/** 按编辑深度生成保存影响范围提示 */
const editorEffectHint = computed(() => {
  if (editorMode.value === 'batch') {
    const count = reassignableKeys.value.length
    return editorDepth.value === 1
      ? t('lineupWorkspace_form.batchEditProcessHint', { count })
      : t('lineupWorkspace_form.batchEditStationHint', { count })
  }
  return t('lineupWorkspace_form.adjustSubStationHint')
})

/**
 * 三级均可选择既有项或输入新编码；输入的新编码保存前二次确认。
 * 层级一致性前置校验：选了“存在但不属于当前父级”的项时提示重选，而不是误当新增。
 */
async function confirmReassign() {
  const depth = editorDepth.value
  const processValue = editProcessId.value.trim()
  const stationValue = editStationId.value.trim()
  const subStationValue = editSubStationId.value.trim()
  // 按编辑深度校验：depth1 只需工序；depth2 需工序+工站；depth3 只改子工站（工序/工站沿用当前绑定）
  if (depth === 1) {
    if (!processValue) {
      ElMessage.warning(t('lineupWorkspace_form.warnSelectProcess'))
      return
    }
  }
  else if (depth === 2) {
    if (!processValue || !stationValue) {
      ElMessage.warning(t('lineupWorkspace_form.warnSelectProcessStation'))
      return
    }
  }
  else if (!subStationValue) {
    ElMessage.warning(t('lineupWorkspace_form.warnSelectSubStation'))
    return
  }

  const enteredCodes = depth === 1
    ? [processValue]
    : depth === 2 ? [processValue, stationValue] : [subStationValue]
  if (enteredCodes.some(value => value.length > 64)) {
    ElMessage.warning(t('lineupWorkspace_form.warnCodeTooLong'))
    return
  }

  const process = lineProcesses.value.find(item => sameId(item.id, processValue)) ?? null
  const station = depth >= 2
    ? (editStations.value.find(item => sameId(item.id, stationValue)) ?? null)
    : null
  const subStation = depth === 3
    ? (editSubStations.value.find(item => sameId(item.id, subStationValue)) ?? null)
    : null

  if (!process && depth >= 1 && lineProcesses.value.length > 0 && /^[0-9]+$/.test(processValue) &&
      lineProcesses.value.some(item => sameId(item.id, processValue))) {
    ElMessage.warning(t('lineupWorkspace_form.warnInvalidProcess'))
    return
  }
  if (!station && depth >= 2 && stationsOfLine.value.some(item => sameId(item.id, stationValue))) {
    ElMessage.warning(t('lineupWorkspace_form.warnStationNotInProcess'))
    return
  }
  if (!subStation && depth === 3) {
    // 子工站不支持新建：只能选当前工站下的既有子工站；缺失时先去主数据维护页补建
    ElMessage.error(t('lineupWorkspace_form.errInvalidSubStation'))
    return
  }
  if (station && !process) {
    ElMessage.warning(t('lineupWorkspace_form.warnStationNeedsProcess'))
    return
  }
  if (subStation && !station) {
    ElMessage.warning(t('lineupWorkspace_form.warnSubStationNeedsStation'))
    return
  }

  const creates: string[] = []
  if (depth >= 1 && !process) {
    creates.push(t('lineupWorkspace_form.createItemProcess', { value: processValue }))
  }
  if (depth >= 2 && !station) {
    creates.push(t('lineupWorkspace_form.createItemStation', { value: stationValue }))
  }
  if (creates.length > 0) {
    try {
      await ElMessageBox.confirm(
        t('lineupWorkspace_form.confirmCreateMessage', { items: creates.map(item => `· ${item}`).join('\n') }),
        t('lineupWorkspace_form.confirmCreateTitle'),
        { type: 'warning', confirmButtonText: t('lineupWorkspace_form.createAndSave'), cancelButtonText: t('common.cancel') },
      )
    }
    catch {
      return
    }
  }

  // 未编辑到的层级字段置空：后端按字段是否为空判定改派深度（空=不改该级，下级保持不变）
  const selection = {
    processId: depth >= 1 && process ? String(process.id) : '',
    newProcessCode: depth >= 1 && !process ? processValue : null,
    stationId: depth >= 2 && station ? String(station.id) : '',
    newStationCode: depth >= 2 && !station ? stationValue : null,
    subStationId: depth === 3 && subStation ? String(subStation.id) : '',
    newSubStationCode: null, // 子工站不支持新建：仅允许选择既有子工站
    processCode: depth >= 1 ? (process?.code ?? processValue) : '',
    processName: depth >= 1 ? (process?.name ?? processValue) : '',
    stationCode: depth >= 2 ? (station?.code ?? stationValue) : '',
    stationName: depth >= 2 ? (station?.name ?? stationValue) : '',
    subStationCode: depth === 3 ? (subStation?.code ?? subStationValue) : '',
    subStationName: depth === 3 ? (subStation?.name ?? subStationValue) : '',
  } as const
  if (editorMode.value === 'batch') {
    const keys = reassignableKeys.value
    const persisted = keys.filter(key => persistedKeys.value.includes(key))
    emit('reassignHierarchyBatch', {
      sequenceSourceKeys: keys,
      persistedKeys: persisted,
      depth: editorDepth.value,
      ...selection,
    })
  }
  else {
    const persisted = persistedKeys.value.includes(editorSequenceKey.value)
    emit('reassignHierarchy', {
      sequenceSourceKey: editorSequenceKey.value,
      sequenceLabel: editorSequenceLabel.value,
      persisted,
      depth: editorDepth.value,
      ...selection,
    })
  }
  editorVisible.value = false
  lookups.value = null // 新建后失效缓存，下次打开重新拉取
}
</script>

<template>
  <section class="hierarchy-review-panel">
    <header>
      <div>
        <span class="eyebrow">{{ $t('lineupWorkspace_form.autoRecognizedTitle') }}</span>
        <h3>{{ $t('lineupWorkspace_form.hierarchyHeading') }}</h3>
      </div>
      <el-switch v-model="reviewOnly" inline-prompt :active-text="$t('lineupWorkspace_form.switchPendingOnly')" :inactive-text="$t('lineupWorkspace_form.switchAll')" />
    </header>

    <el-input v-model="searchText" clearable :placeholder="$t('lineupWorkspace_form.searchPlaceholder')">
      <template #prefix>
        <span aria-hidden="true">⌕</span>
      </template>
    </el-input>

    <div ref="treeShell" class="tree-shell">
      <el-tree-v2
        :key="`${reviewOnly}:${searchText}`"
        :data="treeData"
        :props="{ value: 'id', label: 'label', children: 'children' }"
        :height="treeHeight"
        :item-size="48"
        :default-expanded-keys="defaultExpandedKeys"
        @node-click="handleNodeClick"
      >
        <template #default="{ data }">
          <div
            class="tree-row"
            :class="{
              'is-selected': data.sequenceSourceKey === selectedSequenceKey,
              'is-decided': data.candidate && decisions.get(data.sequenceSourceKey) === data.candidate.candidateKey,
            }"
          >
            <span class="tree-row__icon" :data-kind="data.kind" aria-hidden="true" />
            <span class="tree-row__label" :title="data.label">{{ data.label }}</span>
            <span v-if="data.candidate" class="tree-row__badge">
              {{ confidenceText(data.candidate) }}
            </span>
            <button
              v-if="canReassign(data)"
              type="button"
              class="tree-row__edit"
              :title="$t('lineupWorkspace_form.editSingleNodeTip')"
              @click.stop="openHierarchyEditor(data)"
            >
              {{ $t('lineupWorkspace_form.editShort') }}
            </button>
            <button
              v-else-if="canBatchReassign(data)"
              type="button"
              class="tree-row__edit"
              :title="data.kind === 'process'
                ? $t('lineupWorkspace_form.batchEditProcessTip', { count: reassignableKeys.length })
                : $t('lineupWorkspace_form.batchEditStationTip', { count: reassignableKeys.length })"
              @click.stop="openHierarchyBatchEditor(data)"
            >
              {{ $t('lineupWorkspace_form.editShort') }}
            </button>
            <span
              v-if="data.candidate && decisions.get(data.sequenceSourceKey) === data.candidate.candidateKey"
              class="tree-row__check"
              :aria-label="$t('lineupWorkspace_form.ariaSelected')"
            >✓</span>
          </div>
        </template>
      </el-tree-v2>
    </div>

    <footer>
      {{ $t('lineupWorkspace_form.footerHint') }}
    </footer>

    <el-dialog
      v-model="editorVisible"
      :title="editorTitle"
      width="440px"
      append-to-body
    >
      <el-form label-width="64px" @submit.prevent>
        <el-form-item v-if="editorMode === 'batch' || editorFullCascade" :label="$t('lineupWorkspace_form.fieldProcess')">
          <el-select
            v-model="editProcessId"
            filterable
            allow-create
            default-first-option
            :reserve-keyword="false"
            :placeholder="$t('lineupWorkspace_form.phProcess')"
            style="width: 100%"
            :loading="lookupsLoading"
          >
            <el-option
              v-for="item in lineProcesses"
              :key="item.id"
              :value="String(item.id)"
              :label="optionLabel(item.code, item.name, item.id)"
            />
          </el-select>
        </el-form-item>
        <el-form-item v-if="(editorMode === 'batch' && editorDepth >= 2) || editorFullCascade" :label="$t('lineupWorkspace_form.fieldStation')">
          <el-select
            v-model="editStationId"
            filterable
            allow-create
            default-first-option
            :reserve-keyword="false"
            :placeholder="$t('lineupWorkspace_form.phStation')"
            style="width: 100%"
            :disabled="!editProcessId"
          >
            <el-option
              v-for="item in editStations"
              :key="item.id"
              :value="String(item.id)"
              :label="optionLabel(item.code, item.name, item.id)"
            />
          </el-select>
        </el-form-item>
        <el-form-item v-if="editorDepth === 3" :label="$t('lineupWorkspace_form.fieldSubStation')">
          <el-select
            v-model="editSubStationId"
            filterable
            :reserve-keyword="false"
            :placeholder="$t('lineupWorkspace_form.phSubStation')"
            style="width: 100%"
            :disabled="!editStationId"
          >
            <el-option
              v-for="item in editSubStations"
              :key="item.id"
              :value="String(item.id)"
              :label="optionLabel(item.code, item.name, item.id)"
            />
          </el-select>
        </el-form-item>
      </el-form>
      <p class="hierarchy-edit-hint">
        {{ editorEffectHint }}{{ $t('lineupWorkspace_form.editorHintSuffix') }}
      </p>
      <template #footer>
        <el-button @click="editorVisible = false">{{ $t('common.cancel') }}</el-button>
        <el-button type="primary" @click="confirmReassign">{{ $t('lineupWorkspace_form.saveHierarchy') }}</el-button>
      </template>
    </el-dialog>
  </section>
</template>

<style scoped>
.hierarchy-review-panel {
  display: grid;
  grid-template-rows: auto auto minmax(0, 1fr) auto;
  gap: 12px;
  min-width: 0;
  min-height: 0;
  padding: 14px;
  overflow: hidden;
  background: var(--el-bg-color);
  border: 1px solid var(--el-border-color-light);
  border-radius: 8px;
}

header {
  display: flex;
  gap: 12px;
  align-items: center;
  justify-content: space-between;
}

.eyebrow {
  font-size: 11px;
  font-weight: 700;
  color: var(--el-color-primary);
  letter-spacing: 0.08em;
}

h3 {
  margin: 3px 0 0;
  font-size: 15px;
}

.tree-shell {
  min-height: 0;
  overflow: hidden;
}

.tree-shell :deep(.el-vl__wrapper) {
  scrollbar-gutter: stable;
}

.tree-row {
  display: flex;
  gap: 7px;
  align-items: center;
  width: calc(100% - 8px);
  min-width: 0;
  height: 38px;
  padding: 0 8px;
  border: 1px solid transparent;
  border-radius: 5px;
}

.tree-row.is-selected {
  background: var(--el-color-primary-light-9);
}

.tree-row.is-decided {
  border-color: var(--el-color-success-light-5);
}

.tree-row__icon {
  flex: 0 0 auto;
  width: 8px;
  height: 8px;
  background: var(--el-text-color-placeholder);
  border-radius: 2px;
}

.tree-row__icon[data-kind="document"] { background: #344054; }
.tree-row__icon[data-kind="process"] { background: #1570ef; }
.tree-row__icon[data-kind="station"] { background: #7f56d9; }

.tree-row__icon[data-kind="candidate"] {
  background: #039855;
  border-radius: 50%;
}

.tree-row__icon[data-kind="empty"] {
  background: #d92d20;
  border-radius: 50%;
}

.tree-row__label {
  flex: 1 1 auto;
  overflow: hidden;
  text-overflow: ellipsis;
  font-size: 12px;
  white-space: nowrap;
}

.tree-row__badge {
  flex: 0 0 auto;
  padding: 2px 5px;
  font-size: 10px;
  color: var(--el-text-color-secondary);
  background: var(--el-fill-color-light);
  border-radius: 9px;
}

.tree-row__check {
  flex: 0 0 auto;
  font-weight: 800;
  color: var(--el-color-success);
}

.reassign-empty-hint {
  margin: 0 0 8px;
  padding: 6px 10px;
  border-radius: 4px;
  background: var(--el-color-warning-light-9);
  border-left: 3px solid var(--el-color-warning);
  color: var(--el-text-color-secondary);
  font-size: 12px;
  line-height: 1.6;
}

.tree-row__edit {
  flex: 0 0 auto;
  padding: 1px 8px;
  font-size: 11px;
  line-height: 1.6;
  color: var(--el-color-primary);
  cursor: pointer;
  background: transparent;
  border: 1px solid var(--el-color-primary-light-5);
  border-radius: 999px;
  transition: background-color 0.15s ease;
}

.tree-row__edit:hover {
  background: var(--el-color-primary-light-9);
}

.hierarchy-edit-hint {
  margin: 0 0 4px;
  font-size: 12px;
  line-height: 1.5;
  color: var(--el-text-color-secondary);
}

footer {
  font-size: 11px;
  line-height: 1.5;
  color: var(--el-text-color-secondary);
}
</style>
