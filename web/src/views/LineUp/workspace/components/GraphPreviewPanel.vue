<script setup lang="ts">
import type { LineUpGraphPreviewSequenceInput, TiaGraphViewState } from '../../components/tiaGraphLayout'
import type { TiaGraphSelection } from '../../components/tiaGraphSelection'
import type { LineUpGraphRuntimeProjection } from '../../monitor/monitorProjection'
import type { LineUpConditionRuntimeValue } from '@/api/modules/LineUp/monitor'
import { computed } from 'vue'
import LineUpGraphCanvas from '../../components/LineUpGraphCanvas.vue'
import { useI18n } from 'vue-i18n'
import messages from './lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}

const props = withDefaults(defineProps<{
  sequences: LineUpGraphPreviewSequenceInput[]
  modelValue?: string
  bindingStatus?: string
  viewState?: TiaGraphViewState | null
  /** 运行态投影（仅设备运行监控页传入）：本面板只做静态预览，不自行轮询 */
  runtime?: LineUpGraphRuntimeProjection | null
  runtimeValues?: LineUpConditionRuntimeValue[]
  showSequenceTabs?: boolean
  /** 顺控运行态（sourceKey → Error/Online/Warning/Enabled）：来自 Bindings 摘要的 sequences[].runtimeStatus，用于 tab 状态点 */
  sequenceStatuses?: Record<string, string>
  /** 传入后点击 Step 展示 PLC 台账（ai_plc_graphsteptag）工步变量 */
  stepTagProjectId?: number | string
  /** 工步变量按 GRAPH FB 类型过滤（只显示当前 graph 对应实例 DB） */
  stepTagFbName?: string
  /** BOM 图片缩略图（静态预览上下文）：子工站 id，透传给工步/转换弹框 */
  bomSubStationId?: string
  /** 选中节点关联的 PLC 变量编码，透传给弹框的 BOM 图片页签 */
  bomReferences?: string[]
  /** 查看流程图：按节点读已保存的"成品图+红点"快照（透传画布→面板→TiaStepBomImages） */
  savedNodeImages?: { lineId: string | number, blockName: string, nodeSourceKey: string } | null
  /** 紧凑表头（设备运行监控页）：单行小表头替代大标题布局，最大化下方流程图画布 */
  compact?: boolean
  /** 紧凑表头标题：替代顺控名展示的宿主文本（如 产线/工序/工站/工位 路径）；缺省回退顺控名 */
  titleOverride?: string
}>(), {
  modelValue: '',
  bindingStatus: '',
  viewState: null,
  runtime: null,
  runtimeValues: () => [],
  showSequenceTabs: true,
  sequenceStatuses: () => ({}),
  stepTagProjectId: '',
  stepTagFbName: '',
  bomSubStationId: '',
  bomReferences: () => [],
  savedNodeImages: null,
  compact: false,
  titleOverride: '',
})

const emit = defineEmits<{
  'update:modelValue': [sourceKey: string]
  'nodeClick': [sourceKey: string]
  'selectionChange': [selection: TiaGraphSelection | null]
  'viewStateChange': [state: TiaGraphViewState]
}>()

const activeKey = computed(() => {
  if (props.sequences.some(item => item.sourceKey === props.modelValue)) {
    return props.modelValue
  }
  return props.sequences[0]?.sourceKey ?? ''
})

const activeSequence = computed(() =>
  props.sequences.find(item => item.sourceKey === activeKey.value) ?? null,
)

function selectSequence(sourceKey: string) {
  emit('update:modelValue', sourceKey)
}

const sequenceStatusTitles: Record<string, string> = {
  Error: 'statusTitleError',
  Online: 'statusTitleOnline',
  // 2026-09-11 B+C：网关断线类为告警；步超时（Info）不参与分层，图上以淡色标注
  Warning: 'statusTitleWarning',
  Enabled: 'statusTitleEnabled',
}

function sequenceStatusTitle(status: string) {
  const key = sequenceStatusTitles[status]
  return key ? t(`lineupWorkspace_form.${key}`) : status
}
</script>

<template>
  <section
    class="graph-preview-panel"
    :class="{
      'graph-preview-panel--without-tabs': !showSequenceTabs || sequences.length <= 1,
      'graph-preview-panel--compact': compact,
    }"
  >
    <!-- 紧凑表头（设备运行监控页）：顺控名让位给宿主路径（titleOverride，如 CX1 / OP010 / OP010 / OP010-1），
         页面动作按钮经 header-actions 插槽上表头，整页只保留这一条页头，最大化下方流程图画布 -->
    <header v-if="compact" class="graph-preview-panel__bar">
      <div class="graph-preview-panel__bar-main">
        <div class="graph-preview-panel__bar-meta">
          <span class="graph-preview-panel__eyebrow graph-preview-panel__eyebrow--bar">
            {{ runtime ? $t('lineupWorkspace_form.titleLiveMonitor') : $t('lineupWorkspace_form.titleStaticPreview') }}
          </span>
          <span class="graph-preview-panel__bar-counts">
            {{ $t('lineupWorkspace_form.nodeCount', { n: activeSequence?.nodes.length ?? 0 }) }}
            /
            {{ $t('lineupWorkspace_form.edgeCount', { n: activeSequence?.edges.length ?? 0 }) }}
          </span>
          <strong class="graph-preview-panel__status-badge" :title="bindingStatus || $t('lineupWorkspace_form.defaultBindingStatus')">
            {{ bindingStatus || $t('lineupWorkspace_form.defaultBindingStatus') }}
          </strong>
        </div>
        <h3 class="graph-preview-panel__bar-title" :title="titleOverride || activeSequence?.displayName || activeSequence?.sourceName || ''">
          {{ titleOverride || activeSequence?.displayName || activeSequence?.sourceName || $t('lineupWorkspace_form.noSequenceParsed') }}
        </h3>
      </div>
      <div v-if="$slots['header-actions']" class="graph-preview-panel__bar-actions">
        <slot name="header-actions" />
      </div>
    </header>

    <header v-else class="graph-preview-panel__header">
      <div>
        <div class="graph-preview-panel__eyebrow">
          {{ runtime ? $t('lineupWorkspace_form.titleLiveMonitor') : $t('lineupWorkspace_form.titleStaticPreview') }}
        </div>
        <h3>{{ activeSequence?.displayName || activeSequence?.sourceName || $t('lineupWorkspace_form.noSequenceParsed') }}</h3>
      </div>
      <div class="graph-preview-panel__summary">
        <span>{{ $t('lineupWorkspace_form.nodeCount', { n: activeSequence?.nodes.length ?? 0 }) }}</span>
        <span>{{ $t('lineupWorkspace_form.edgeCount', { n: activeSequence?.edges.length ?? 0 }) }}</span>
        <strong>{{ bindingStatus || t('lineupWorkspace_form.defaultBindingStatus') }}</strong>
      </div>
    </header>

    <nav v-if="showSequenceTabs && sequences.length > 1" class="graph-preview-panel__tabs" :aria-label="$t('lineupWorkspace_form.ariaSequenceSwitch')">
      <button
        v-for="sequence in sequences"
        :key="sequence.sourceKey"
        type="button"
        :class="{ 'is-active': sequence.sourceKey === activeKey }"
        @click="selectSequence(sequence.sourceKey)"
      >
        <span
          v-if="sequenceStatuses[sequence.sourceKey]"
          class="graph-preview-panel__tab-status"
          :data-status="sequenceStatuses[sequence.sourceKey].toLowerCase()"
          :title="sequenceStatusTitle(sequenceStatuses[sequence.sourceKey])"
        />
        {{ sequence.displayName || sequence.sourceName }}
        <small>{{ sequence.nodes.length }} / {{ sequence.edges.length }}</small>
      </button>
    </nav>

    <div class="graph-preview-panel__canvas">
      <LineUpGraphCanvas
        :sequence="activeSequence"
        :view-state="viewState"
        :runtime="runtime"
        :runtime-values="runtimeValues"
        :step-tag-project-id="stepTagProjectId"
        :step-tag-fb-name="stepTagFbName"
        :bom-sub-station-id="bomSubStationId"
        :bom-references="bomReferences"
        :saved-node-images="savedNodeImages"
        @node-click="emit('nodeClick', $event)"
        @selection-change="emit('selectionChange', $event)"
        @view-state-change="emit('viewStateChange', $event)"
      >
        <template v-if="$slots['runtime-detail']" #runtime-detail="runtimeDetailProps">
          <slot name="runtime-detail" v-bind="runtimeDetailProps" />
        </template>
      </LineUpGraphCanvas>
    </div>
  </section>
</template>

<style scoped>
.graph-preview-panel {
  display: grid;
  grid-template-rows: auto auto minmax(280px, 1fr);
  min-width: 0;
  min-height: 0;
  overflow: hidden;
  background: var(--el-bg-color);
  border: 1px solid var(--el-border-color-light);
  border-radius: 8px;
}

.graph-preview-panel--without-tabs {
  grid-template-rows: auto minmax(280px, 1fr);
}

.graph-preview-panel__header {
  display: flex;
  gap: 20px;
  align-items: center;
  justify-content: space-between;
  padding: 14px 16px 12px;
  border-bottom: 1px solid var(--el-border-color-lighter);
}

.graph-preview-panel__eyebrow {
  margin-bottom: 3px;
  font-size: 11px;
  font-weight: 700;
  color: var(--el-color-primary);
  letter-spacing: 0.08em;
}

.graph-preview-panel h3 {
  margin: 0;
  font-size: 16px;
  color: var(--el-text-color-primary);
}

.graph-preview-panel__summary {
  display: flex;
  flex-wrap: wrap;
  gap: 8px 12px;
  align-items: center;
  justify-content: flex-end;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.graph-preview-panel__summary strong {
  padding: 4px 8px;
  font-weight: 600;
  color: var(--el-color-warning-dark-2);
  background: var(--el-color-warning-light-9);
  border-radius: 4px;
}

.graph-preview-panel__tabs {
  display: flex;
  gap: 6px;
  max-width: 100%;
  padding: 8px 12px;
  overflow-x: auto;
  border-bottom: 1px solid var(--el-border-color-lighter);
}

.graph-preview-panel__tabs button {
  display: inline-flex;
  flex: 0 0 auto;
  gap: 10px;
  align-items: center;
  padding: 6px 10px;
  color: var(--el-text-color-regular);
  cursor: pointer;
  background: var(--el-fill-color-light);
  border: 1px solid transparent;
  border-radius: 4px;
}

.graph-preview-panel__tabs button.is-active {
  color: var(--el-color-primary);
  background: var(--el-color-primary-light-9);
  border-color: var(--el-color-primary-light-5);
}

.graph-preview-panel__tabs small {
  font-family: Consolas, monospace;
  color: var(--el-text-color-secondary);
}

.graph-preview-panel__tab-status {
  flex: none;
  width: 8px;
  height: 8px;
  background: var(--el-text-color-placeholder);
  border-radius: 50%;
}

.graph-preview-panel__tab-status[data-status="error"] {
  background: var(--el-color-danger);
  box-shadow: 0 0 0 3px var(--el-color-danger-light-8);
}

.graph-preview-panel__tab-status[data-status="online"] {
  background: var(--el-color-success);
}

.graph-preview-panel__tab-status[data-status="warning"] {
  background: var(--el-color-warning);
  box-shadow: 0 0 0 3px var(--el-color-warning-light-8);
}

.graph-preview-panel__tab-status[data-status="enabled"] {
  background: var(--el-color-primary);
  opacity: 0.55;
}

/* —— 紧凑表头（compact，设备运行监控页）：单条小页头 + 紧凑 tabs，最大化流程图画布 —— */
.graph-preview-panel__bar {
  display: flex;
  gap: 12px;
  align-items: center;
  justify-content: space-between;
  min-width: 0;
  padding: 6px 12px;
  border-bottom: 1px solid var(--el-border-color-lighter);
}

.graph-preview-panel__bar-main {
  flex: 1;
  min-width: 0;
}

.graph-preview-panel__bar-meta {
  display: flex;
  gap: 8px;
  align-items: center;
  min-width: 0;
}

.graph-preview-panel__eyebrow--bar {
  margin-bottom: 0;
  line-height: 1.2;
}

.graph-preview-panel__bar-counts {
  flex: none;
  font-size: 11px;
  color: var(--el-text-color-secondary);
  white-space: nowrap;
}

.graph-preview-panel__status-badge {
  flex: none;
  max-width: 320px;
  padding: 2px 8px;
  overflow: hidden;
  font-size: 11px;
  font-weight: 600;
  white-space: nowrap;
  text-overflow: ellipsis;
  color: var(--el-color-warning-dark-2);
  background: var(--el-color-warning-light-9);
  border-radius: 4px;
}

.graph-preview-panel__bar-title {
  margin: 2px 0 0;
  overflow: hidden;
  font-size: 14px;
  white-space: nowrap;
  text-overflow: ellipsis;
  color: var(--el-text-color-primary);
}

.graph-preview-panel__bar-actions {
  display: flex;
  flex: none;
  gap: 6px;
  align-items: center;
}

.graph-preview-panel--compact .graph-preview-panel__tabs {
  padding: 4px 8px;
}

.graph-preview-panel--compact .graph-preview-panel__tabs button {
  padding: 3px 8px;
  font-size: 12px;
}

.graph-preview-panel--compact .graph-preview-panel__canvas {
  padding: 6px;
}

.graph-preview-panel__canvas {
  min-width: 0;
  min-height: 0;
  padding: 10px;
}

@media (width <= 900px) {
  .graph-preview-panel__header {
    flex-direction: column;
    align-items: flex-start;
  }

  .graph-preview-panel__summary {
    justify-content: flex-start;
  }
}
</style>
