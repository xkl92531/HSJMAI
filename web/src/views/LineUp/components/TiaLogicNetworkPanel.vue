<script setup lang="ts">
import type { TiaGraphSelection } from './tiaGraphSelection'
import type { TiaLogicPanelTab } from './tiaLogicPanelState'
import type { LineUpConditionRuntimeValue } from '@/api/modules/LineUp/monitor'
import type { LineUpGraphLogicNetwork } from '@/api/modules/LineUp/types'
import type { PlcGraphStepTagRow } from '@/api/modules/PLC/graphStepTag'
import type { FbLocalSymbolMap } from '@/api/modules/PLC/fbLocalSymbol'
import { computed, ref, useSlots, watch } from 'vue'
import { concretizeFbLocals } from '@/api/modules/PLC/fbLocalSymbol'
import { formatMonitorDuration } from '../monitor/monitorProjection'
import TiaActionTable from './TiaActionTable.vue'
import TiaStepBomImages from './TiaStepBomImages.vue'
import TiaLadNetwork from './TiaLadNetwork.vue'
import { useTiaLogicPanelTab } from './tiaLogicPanelState'
import { useI18n } from 'vue-i18n'
import messages from './lang'

const props = withDefaults(defineProps<{
  selection: TiaGraphSelection
  runtimeValues?: LineUpConditionRuntimeValue[]
  activeTab?: TiaLogicPanelTab
  activeDurationMs?: string | null
  /** 当前步在 PLC 台账（ai_plc_graphsteptag）里的注册变量行（预览弹框传入） */
  stepTags?: PlcGraphStepTagRow[]
  /** 宿主是否接通了台账数据源（预览弹框传 stepTagProjectId 时为 true） */
  hasStepTagSource?: boolean
  /** BOM 缩略图数据源（静态预览上下文）：子工站 id（空则不显示该页签） */
  bomSubStationId?: string
  /** 选中节点关联的 PLC 变量编码（工步=相邻转换条件变量；转换=自身条件变量） */
  bomReferences?: string[]
  /** 查看流程图：按节点读已保存快照（透传 TiaStepBomImages） */
  savedNode?: { lineId: string | number, blockName: string, nodeSourceKey: string } | null
  /** PLC 项目上下文（与 stepTag 同源）：传入后对 LAD 图里的 FB 局部变量做全局符号路径具体化 */
  plcProjectId?: number | string
  /** 当前 GRAPH FB 类型名（与 stepTag 同源）：局部变量具体化的锚点块名 */
  graphFbName?: string
}>(), {
  runtimeValues: () => [],
  activeDurationMs: null,
  stepTags: () => [],
  hasStepTagSource: false,
  bomSubStationId: '',
  bomReferences: () => [],
  savedNode: null,
  plcProjectId: '',
  graphFbName: '',
})


const emit = defineEmits<{
  'update:activeTab': [value: TiaLogicPanelTab]
}>()
const slots = useSlots()

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}
const controlledTab = computed(() => props.activeTab)
const selectionSourceKey = computed(() => props.selection.node.sourceKey)
const hasRuntimeDetail = computed(() => Boolean(slots['runtime-detail']))
const hasBomImages = computed(() => Boolean(props.bomSubStationId || props.savedNode))
const { effectiveTab, selectTab } = useTiaLogicPanelTab(
  controlledTab,
  selectionSourceKey,
  value => emit('update:activeTab', value),
  hasRuntimeDetail.value && !props.hasStepTagSource ? 'runtime-step' : 'step-tags',
)

const runtimeDetailTab = computed(() => ({
  'runtime-step': 'step',
  'runtime-conditions': 'conditions',
  'runtime-faults': 'faults',
  'runtime-events': 'events',
  'runtime-bom': 'bom',
} as const)[effectiveTab.value as 'runtime-step' | 'runtime-conditions' | 'runtime-faults' | 'runtime-events' | 'runtime-bom'] ?? 'step')
const isRuntimeDetailTab = computed(() => effectiveTab.value.startsWith('runtime-'))

const title = computed(() => {
  const prefix = props.selection.kind === 'step' ? 'S' : 'T'
  const name = props.selection.node.displayName || props.selection.node.sourceName
  return `${prefix}${props.selection.node.number} · ${name}`
})

const visibleNetworks = computed<LineUpGraphLogicNetwork[]>(() => {
  if (props.selection.kind === 'transition') {
    return props.selection.networks.filter(network => network.role === 'TransitionCondition')
  }
  const role = effectiveTab.value === 'interlock' ? 'StepInterlock' : 'StepSupervision'
  return effectiveTab.value === 'actions'
    ? []
    : props.selection.networks.filter(network => network.role === role)
})

// ── FB 局部变量 → 全局符号路径 映射（LAD 梯形图展示用，2026-10-07）──
// GRAPH 转换条件 LAD 里的 #S_AutoRunMode / #I_Mode.DryRunMode 这类 LocalVariable
// 部件，按 FbLocalVariableConcretizer 的结果显示为实际对应的全局符号路径
// （IDB150_xxx.S_AutoRunMode），与 IGW 采集点位名一致。缺 PLC 上下文或具体化
// 失败时按原样显示（裸名），不影响任何既有渲染。
const fbLocalSymbolMap = ref<FbLocalSymbolMap>({})
let fbLocalSymbolToken = 0

const fbLocalCandidates = computed<string[]>(() => {
  const names = new Set<string>()
  for (const network of props.selection.networks) {
    for (const part of network.parts) {
      if (part.scope === 'LocalVariable' && part.symbolPath.length > 0) {
        names.add(part.symbolPath.join('.'))
      }
    }
  }
  return [...names]
})

watch(
  [fbLocalCandidates, () => props.plcProjectId, () => props.graphFbName],
  async ([candidates, projectId, fbName]) => {
    const token = ++fbLocalSymbolToken
    if (!projectId || !fbName || candidates.length === 0) {
      fbLocalSymbolMap.value = {}
      return
    }
    try {
      const map = await concretizeFbLocals(projectId, fbName, candidates)
      if (token === fbLocalSymbolToken) {
        fbLocalSymbolMap.value = map
      }
    }
    catch {
      // 具体化失败不阻断面板：按原样显示局部符号名
      if (token === fbLocalSymbolToken) {
        fbLocalSymbolMap.value = {}
      }
    }
  },
  { immediate: true },
)

const STEP_TAG_STATUS_LABELS = computed<Record<string, string>>(() => ({
  Verified: t('lineupShared.stepTagVerified'),
  Registered: t('lineupShared.stepTagRegistered'),
  Pending: t('lineupShared.stepTagPending'),
  RegFailed: t('lineupShared.stepTagRegFailed'),
  VerifyFailed: t('lineupShared.stepTagVerifyFailed'),
}))
</script>

<template>
  <section class="tia-logic-panel" :aria-label="title">
    <header class="panel-title">
      <strong>{{ title }}</strong>
      <span>{{ selection.node.sourceKey }}</span>
    </header>

    <nav v-if="selection.kind === 'step' || hasRuntimeDetail || hasBomImages" class="panel-tabs" :aria-label="$t('lineupShared.panelAria')">
      <template v-if="selection.kind === 'step'">
        <button
          v-if="hasStepTagSource"
          type="button"
          :class="{ active: effectiveTab === 'step-tags' }"
          @click="selectTab('step-tags')"
        >
          {{ $t('lineupShared.tabStepDetail') }}
        </button>
        <button
          type="button"
          :class="{ active: effectiveTab === 'actions' }"
          @click="selectTab('actions')"
        >
          {{ $t('lineupShared.tabActions') }}
        </button>
        <button
          type="button"
          :class="{ active: effectiveTab === 'interlock' }"
          @click="selectTab('interlock')"
        >
          {{ $t('lineupShared.tabInterlock') }}
        </button>
        <button
          type="button"
          :class="{ active: effectiveTab === 'supervision' }"
          @click="selectTab('supervision')"
        >
          {{ $t('lineupShared.tabSupervision') }}
        </button>
      </template>
      <button
        v-else
        type="button"
        :class="{ active: !isRuntimeDetailTab && effectiveTab !== 'bom-images' }"
        @click="selectTab('interlock')"
      >
        {{ $t('lineupShared.tabTransition') }}
      </button>
      <template v-if="hasRuntimeDetail">
        <button type="button" :class="{ active: effectiveTab === 'runtime-step' }" @click="selectTab('runtime-step')">
          {{ $t('lineupShared.tabRuntime') }}
        </button>
        <button type="button" :class="{ active: effectiveTab === 'runtime-conditions' }" @click="selectTab('runtime-conditions')">
          {{ $t('lineupShared.tabConditions') }}
        </button>
        <button type="button" :class="{ active: effectiveTab === 'runtime-faults' }" @click="selectTab('runtime-faults')">
          {{ $t('lineupShared.tabFaults') }}
        </button>
        <button type="button" :class="{ active: effectiveTab === 'runtime-events' }" @click="selectTab('runtime-events')">
          {{ $t('lineupShared.tabEvents') }}
        </button>
        <button
          v-if="!hasBomImages"
          type="button"
          :class="{ active: effectiveTab === 'runtime-bom' }"
          @click="selectTab('runtime-bom')"
        >
          {{ $t('lineupShared.tabBomImages') }}
        </button>
      </template>
      <button
        v-if="hasBomImages"
        type="button"
        :class="{ active: effectiveTab === 'bom-images' }"
        @click="selectTab('bom-images')"
      >
        {{ $t('lineupShared.tabBomImages') }}
      </button>
    </nav>

    <div v-if="isRuntimeDetailTab" class="panel-runtime">
      <slot name="runtime-detail" :active-tab="runtimeDetailTab" />
    </div>

    <div v-else-if="effectiveTab === 'bom-images'" class="panel-body">
      <TiaStepBomImages :sub-station-id="bomSubStationId" :references="bomReferences" :saved-node="savedNode" />
    </div>

    <div v-else-if="selection.kind === 'step' && effectiveTab === 'step-tags'" class="panel-body">
      <p v-if="stepTags.length === 0" class="empty-message">
        {{ $t('lineupShared.noStepTags') }}
      </p>
      <table v-else class="step-tag-table">
        <thead>
          <tr>
            <th>{{ $t('lineupShared.colSymbol') }}</th>
            <th>{{ $t('lineupShared.colRegStatus') }}</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in stepTags" :key="row.id">
            <td class="symbol-cell">{{ row.symbol }}</td>
            <td>
              <span class="tag-status" :class="'is-' + (row.regStatus || 'unknown').toLowerCase()">
                {{ STEP_TAG_STATUS_LABELS[row.regStatus || ''] || row.regStatus || $t('lineupShared.unknownStatus') }}
              </span>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <div v-else-if="selection.kind === 'step'" class="panel-body">
      <TiaActionTable
        v-if="effectiveTab === 'actions'"
        :actions="selection.actions"
        :title="selection.node.actionGroupTitle"
        :detail-unavailable="selection.detailUnavailable"
      />
      <div v-else class="network-list">
        <dl v-if="effectiveTab === 'supervision'" class="timing-summary">
          <div><dt>WarningTime</dt><dd>{{ selection.node.warningTime || '—' }}</dd></div>
          <div><dt>MaximumStepTime</dt><dd>{{ selection.node.maximumStepTime || '—' }}</dd></div>
          <div><dt>{{ $t('lineupShared.activeDuration') }}</dt><dd>{{ formatMonitorDuration(activeDurationMs) }}</dd></div>
        </dl>
        <TiaLadNetwork
          v-for="network in visibleNetworks"
          :key="network.sourceKey"
          :network="network"
          :runtime-values="runtimeValues"
          :local-symbol-map="fbLocalSymbolMap"
        />
        <p v-if="visibleNetworks.length === 0" class="empty-message">
          {{ selection.detailUnavailable ? $t('lineupShared.detailUnavailableTip') : $t('lineupShared.graphUndefined') }}
        </p>
      </div>
    </div>

    <div v-else class="panel-body transition-condition-view">
      <TiaLadNetwork
        v-for="network in visibleNetworks"
        :key="network.sourceKey"
        :network="network"
        :runtime-values="runtimeValues"
        :local-symbol-map="fbLocalSymbolMap"
      />
      <p v-if="visibleNetworks.length === 0" class="empty-message">
        {{ selection.detailUnavailable ? $t('lineupShared.detailUnavailableTip') : $t('lineupShared.graphUndefined') }}
      </p>
    </div>
  </section>
</template>

<style scoped>
.tia-logic-panel {
  display: flex;
  flex-direction: column;
  min-width: 0;
  height: 100%;
  min-height: 0;
  overflow: hidden;
  background: #f5f6f8;
  border: 1px solid #8e959f;
  box-shadow: 0 7px 20px rgb(15 23 42 / 18%);
}

.panel-body {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
}

.panel-runtime {
  flex: 1;
  min-height: 0;
  padding: 10px;
  overflow: auto;
  background: #fff;
}

.panel-title {
  display: flex;
  gap: 12px;
  justify-content: space-between;
  padding: 8px 10px;
  color: #f5f7fa;
  background: #3f4650;
}

.panel-title strong,
.panel-title span {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.panel-title strong {
  font-size: 13px;
}

.panel-title span {
  font-size: 10px;
  color: #d7dce3;
}

.panel-tabs {
  display: flex;
  gap: 0;
  padding: 0 8px;
  background: #e6e9ed;
  border-bottom: 1px solid #b8bdc5;
}

.panel-tabs button {
  padding: 7px 15px 6px;
  font: inherit;
  font-size: 12px;
  color: #4b5563;
  cursor: pointer;
  background: transparent;
  border: 0;
  border-bottom: 2px solid transparent;
}

.panel-tabs button.active {
  font-weight: 600;
  color: #165d9c;
  background: #f8f9fb;
  border-bottom-color: #165d9c;
}

.tia-action-table,
.network-list,
.transition-condition-view {
  flex: 1;
  min-height: 0;
  margin: 10px;
}

.network-list,
.transition-condition-view {
  display: grid;
  gap: 10px;
  overflow: auto;
}

.timing-summary {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 8px;
  margin: 0;
}

.timing-summary div {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr);
  gap: 6px;
  padding: 6px 8px;
  font-size: 11px;
  overflow-wrap: anywhere;
  background: #e9ecf0;
}

.timing-summary dt {
  color: #5b6470;
}

.timing-summary dd {
  min-width: 0;
  margin: 0;
  color: #1f2937;
}

.empty-message {
  padding: 24px 12px;
  margin: 0;
  font-size: 12px;
  color: #6b7280;
  text-align: center;
  background: #fff;
  border: 1px dashed #b7bdc6;
}

.step-tag-table {
  width: 100%;
  font-size: 12px;
  border-collapse: collapse;
}

.step-tag-table th,
.step-tag-table td {
  padding: 6px 8px;
  text-align: left;
  border-bottom: 1px solid #e5e8eb;
}

.step-tag-table th {
  position: sticky;
  top: 0;
  color: #475467;
  font-weight: 600;
  background: #f1f3f4;
}

.symbol-cell {
  max-width: 380px;
  overflow-wrap: anywhere;
  font-family: Consolas, "Microsoft YaHei", monospace;
  color: #0f5f8d;
}

.tag-status {
  display: inline-block;
  padding: 1px 8px;
  font-size: 11px;
  border-radius: 8px;
  white-space: nowrap;
}

.tag-status.is-verified {
  color: #389e0d;
  background: #d9f7be;
}

.tag-status.is-registered {
  color: #005f73;
  background: #d6e4ff;
}

.tag-status.is-pending {
  color: #6b7280;
  background: #f0f2f5;
}

.tag-status.is-regfailed,
.tag-status.is-verifyfailed {
  color: #cf1322;
  background: #ffccc7;
}
</style>
