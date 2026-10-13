<script setup lang="ts">
import type { TiaGraphSelection } from '../../components/tiaGraphSelection'
import type { LineUpConditionRuntimeValue, LineUpMonitorEvent, LineUpMonitorSnapshot } from '@/api/modules/LineUp/monitor'
import { computed, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { disableTagCollection, enableTagCollection, listTagCollectionRules } from '@/api/modules/LineUp/monitor'
import { eventsForMonitorSelection, exceptionsForMonitorSelection, formatMonitorDuration, normalizeQuality, normalizeStepStatus, normalizeTransitionStatus } from '../monitorProjection'
import LineUpFaultLocation from './LineUpFaultLocation.vue'
import { useI18n } from 'vue-i18n'
import messages from './lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}

type LineUpRuntimeDetailTab = 'step' | 'conditions' | 'faults' | 'events' | 'bom'

const props = withDefaults(defineProps<{
  selection?: TiaGraphSelection | null
  conditions?: LineUpConditionRuntimeValue[]
  snapshot?: LineUpMonitorSnapshot | null
  events?: LineUpMonitorEvent[]
  stationId?: string
  subStationId?: string
  loadingEvents?: boolean
  hasMoreEvents?: boolean
  activeTab?: LineUpRuntimeDetailTab
  embedded?: boolean
  /** 当前绑定所属 PLC 项目 id；有效时「实时条件」行显示采集开关 */
  plcProjectId?: string
}>(), {
  selection: null,
  conditions: () => [],
  snapshot: null,
  events: () => [],
  stationId: '',
  subStationId: '',
  loadingEvents: false,
  hasMoreEvents: false,
  activeTab: undefined,
  embedded: false,
  plcProjectId: '',
})

const emit = defineEmits<{
  'loadMoreEvents': []
  'update:activeTab': [value: LineUpRuntimeDetailTab]
}>()
const internalActiveTab = ref<LineUpRuntimeDetailTab>('step')
const effectiveActiveTab = computed(() => props.activeTab ?? internalActiveTab.value)

const selectedNode = computed(() => props.selection?.node ?? null)
const stepState = computed(() => props.snapshot?.steps.find(item => item.sourceKey === selectedNode.value?.sourceKey) ?? null)
const transitionState = computed(() => props.snapshot?.transitions.find(item => item.sourceKey === selectedNode.value?.sourceKey) ?? null)
const nodeEvents = computed(() => eventsForMonitorSelection(props.events, props.selection))
const nodeExceptions = computed(() => exceptionsForMonitorSelection(props.snapshot?.exceptions ?? [], props.selection))

// ===== 变量采集开关（2026-09-12）=====
// 屏蔽 = 变量不进运行时读计划，引用它的转移条件求值落 Unknown（不降级连接状态）。
const collectionManaging = ref(false)
const disabledPaths = ref<Set<string>>(new Set())
const canManageCollection = computed(() => Boolean(props.plcProjectId))

async function loadCollectionRules() {
  if (!props.plcProjectId) return
  try {
    const paths = await listTagCollectionRules(props.plcProjectId)
    disabledPaths.value = new Set(paths)
  } catch {
    // 规则加载失败不阻断页面（开关显示为全部采集）
  }
}

watch(() => props.plcProjectId, loadCollectionRules, { immediate: true })

function isCollected(item: LineUpConditionRuntimeValue) {
  const path = item.variablePath || ''
  return path === '' || !disabledPaths.value.has(path)
}

async function toggleCollection(item: LineUpConditionRuntimeValue, collected: string | number | boolean) {
  const path = item.variablePath || ''
  if (!props.plcProjectId || collectionManaging.value || !path) return
  collectionManaging.value = true
  try {
    const next = Boolean(collected)
    if (next) {
      await enableTagCollection(props.plcProjectId, path)
      disabledPaths.value.delete(path)
      disabledPaths.value = new Set(disabledPaths.value)
      ElMessage.success(t('lineupMonitor_form.msgCollectionResumed', { path }))
    } else {
      await disableTagCollection(props.plcProjectId, path, '监控页手动屏蔽（转移持续 Bad）')
      disabledPaths.value.add(path)
      disabledPaths.value = new Set(disabledPaths.value)
      ElMessage.success(t('lineupMonitor_form.msgCollectionBlocked', { path }))
    }
  } catch (error) {
    ElMessage.error(t('lineupMonitor_form.errCollectionToggleFailed', { message: (error as Error).message }))
  } finally {
    collectionManaging.value = false
  }
}

function formatRuntimeValue(value: unknown) {
  if (value === null || value === undefined) {
    return '—'
  }
  if (typeof value === 'string') {
    return value.slice(0, 128)
  }
  if (typeof value === 'boolean' || typeof value === 'number') {
    return String(value)
  }
  return '—'
}

function qualityTagType(quality: LineUpConditionRuntimeValue['quality']) {
  return quality === 'Good' ? 'success' : quality === 'Bad' ? 'danger' : quality === 'Stale' ? 'warning' : 'info'
}

function exceptionTagType(severity: string) {
  const normalized = severity.toLowerCase()
  return normalized === 'error' || normalized === 'critical'
    ? 'danger'
    : normalized === 'warning'
      ? 'warning'
      : 'info'
}

function updateActiveTab(value: string | number) {
  const requested = String(value) as LineUpRuntimeDetailTab
  if (props.activeTab === undefined) {
    internalActiveTab.value = requested
  }
  emit('update:activeTab', requested)
}
</script>

<template>
  <div class="runtime-detail" :class="{ 'is-embedded': embedded }">
    <el-tabs :model-value="effectiveActiveTab" stretch @update:model-value="updateActiveTab">
      <el-tab-pane :label="$t('lineupMonitor_form.tabStep')" name="step">
        <template v-if="selectedNode">
          <h3>{{ selectedNode.displayName || selectedNode.sourceName }}</h3>
          <dl>
            <div><dt>{{ $t('lineupMonitor_form.fieldTypeNumber') }}</dt><dd>{{ selectedNode.nodeKind }} · {{ selectedNode.number }}</dd></div>
            <div><dt>{{ $t('lineupMonitor_form.fieldSourceIdentity') }}</dt><dd>{{ selectedNode.sourceKey }}</dd></div>
            <div><dt>{{ $t('lineupMonitor_form.fieldStation') }}</dt><dd>{{ stationId || '—' }}</dd></div>
            <div v-if="stepState">
              <dt>{{ $t('lineupMonitor_form.fieldLiveStatus') }}</dt><dd>{{ normalizeStepStatus(stepState.status) }}</dd>
            </div>
            <div v-if="stepState">
              <dt>{{ $t('lineupMonitor_form.fieldQuality') }}</dt><dd>{{ normalizeQuality(stepState.quality) }}</dd>
            </div>
            <div v-if="stepState">
              <dt>{{ $t('lineupMonitor_form.fieldActiveDuration') }}</dt><dd>{{ formatMonitorDuration(stepState.activeDurationMs) }}</dd>
            </div>
            <div v-if="stepState">
              <dt>{{ $t('lineupMonitor_form.fieldQualityCode') }}</dt><dd>{{ stepState.qualityCode || '—' }}</dd>
            </div>
            <div v-if="stepState">
              <dt>{{ $t('lineupMonitor_form.fieldStateTime') }}</dt><dd>{{ stepState.stateChangedAtUtc || '—' }}</dd>
            </div>
            <div v-if="transitionState">
              <dt>{{ $t('lineupMonitor_form.fieldTransitionStatus') }}</dt><dd>{{ normalizeTransitionStatus(transitionState.status) }}</dd>
            </div>
            <div><dt>{{ $t('lineupMonitor_form.fieldComment') }}</dt><dd>{{ selectedNode.comment || '—' }}</dd></div>
          </dl>
        </template>
        <el-empty v-else :image-size="64" :description="$t('lineupMonitor_form.emptyPickGraphNode')" />
      </el-tab-pane>

      <el-tab-pane :label="$t('lineupMonitor_form.tabConditions')" name="conditions">
        <div v-if="conditions.length" class="condition-list">
          <article v-for="item in conditions" :key="item.canonicalKey">
            <strong>{{ item.variablePath || item.partSourceKey }}</strong>
            <span>{{ $t('lineupMonitor_form.fieldValue') }} {{ formatRuntimeValue(item.value) }}</span>
            <span>{{ $t('lineupMonitor_form.fieldQuality') }} {{ normalizeQuality(item.quality) }}</span>
            <span>{{ $t('lineupMonitor_form.fieldCode') }} {{ item.qualityCode || '—' }}</span>
            <time>{{ $t('lineupMonitor_form.fieldTime') }} {{ item.sourceTimestamp || '—' }}</time>
            <el-tag size="small" :type="qualityTagType(item.quality)">
              {{ normalizeQuality(item.quality) }}
            </el-tag>
            <el-switch
              v-if="canManageCollection && item.variablePath"
              :model-value="isCollected(item)"
              :disabled="collectionManaging"
              size="small"
              inline-prompt
              :active-text="$t('lineupMonitor_form.collectionOn')"
              :inactive-text="$t('lineupMonitor_form.collectionOff')"
              @change="toggleCollection(item, $event)"
            />
          </article>
        </div>
        <el-empty v-else :image-size="58" :description="$t('lineupMonitor_form.emptyNoConditions')" />
      </el-tab-pane>

      <el-tab-pane :label="nodeExceptions.length ? $t('lineupMonitor_form.tabFaultsWithCount', { count: nodeExceptions.length }) : $t('lineupMonitor_form.tabFaults')" name="faults">
        <div v-if="nodeExceptions.length" class="exception-list">
          <article
            v-for="item in nodeExceptions"
            :key="`${item.code}:${item.ownerSourceKey}:${item.occurredAtUtc}`"
            class="exception-card"
          >
            <header>
              <strong>{{ item.safeMessage || item.code }}</strong>
              <el-tag size="small" effect="dark" :type="exceptionTagType(item.severity)">
                {{ item.severity || 'Unknown' }}
              </el-tag>
            </header>
            <dl>
              <div><dt>{{ $t('lineupMonitor_form.fieldExceptionCode') }}</dt><dd>{{ item.code || '—' }}</dd></div>
              <div><dt>{{ $t('lineupMonitor_form.fieldExceptionMessage') }}</dt><dd>{{ item.safeMessage || '—' }}</dd></div>
              <div><dt>{{ $t('lineupMonitor_form.fieldSeverity') }}</dt><dd>{{ item.severity || '—' }}</dd></div>
              <div><dt>{{ $t('lineupMonitor_form.fieldOccurredAt') }}</dt><dd>{{ item.occurredAtUtc ? new Date(item.occurredAtUtc).toLocaleString() : '—' }}</dd></div>
            </dl>
          </article>
        </div>
        <el-empty v-else :image-size="52" :description="$t('lineupMonitor_form.emptyNoExceptions')" />
      </el-tab-pane>

      <el-tab-pane :label="nodeEvents.length ? $t('lineupMonitor_form.tabEventsWithCount', { count: nodeEvents.length }) : $t('lineupMonitor_form.tabEvents')" name="events">
        <div v-if="nodeEvents.length" class="event-list">
          <article v-for="item in nodeEvents" :key="item.id">
            <time>{{ new Date(item.occurredAtUtc).toLocaleString() }}</time>
            <strong>{{ item.eventType }}</strong>
            <span>{{ item.ownerSourceKey || $t('lineupMonitor_form.runtimeConnection') }}</span>
            <small>{{ item.previousState || '—' }} → {{ item.currentState || '—' }}</small>
          </article>
          <el-button v-if="hasMoreEvents" :loading="loadingEvents" text type="primary" @click="emit('loadMoreEvents')">
            {{ $t('lineupMonitor_form.loadMore') }}
          </el-button>
        </div>
        <el-empty v-else :image-size="58" :description="$t('lineupMonitor_form.emptyNoEvents')" />
      </el-tab-pane>

      <el-tab-pane :label="$t('lineupMonitor_form.tabBom')" name="bom" lazy>
        <LineUpFaultLocation
          :sub-station-id="subStationId"
          :selection="selection"
          :conditions="conditions"
        />
      </el-tab-pane>
    </el-tabs>
  </div>
</template>

<style scoped>
.runtime-detail {
  min-width: 0;
}

.runtime-detail.is-embedded :deep(.el-tabs__header) {
  display: none;
}

.runtime-detail.is-embedded :deep(.el-tabs__content) {
  overflow: visible;
}

.runtime-detail h3 {
  margin: 4px 0 14px;
  font-size: 17px;
}

.runtime-detail dl {
  display: grid;
  gap: 10px;
  margin: 0;
}

.runtime-detail dl div {
  display: grid;
  grid-template-columns: 78px minmax(0, 1fr);
  gap: 8px;
  font-size: 12px;
}

.runtime-detail dt {
  color: var(--el-text-color-secondary);
}

.runtime-detail dd {
  min-width: 0;
  margin: 0;
  overflow-wrap: anywhere;
}

.condition-list,
.exception-list,
.event-list {
  display: grid;
  gap: 8px;
}

.condition-list article {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 8px;
  align-items: center;
  padding: 8px;
  font-size: 11px;
  background: var(--el-fill-color-light);
  border-radius: 5px;
}

.condition-list strong {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
}

.exception-card {
  display: grid;
  gap: 10px;
  padding: 12px;
  background: var(--el-color-danger-light-9);
  border: 1px solid var(--el-color-danger-light-5);
  border-radius: 6px;
}

.exception-card header {
  display: flex;
  gap: 12px;
  align-items: flex-start;
  justify-content: space-between;
}

.exception-card header strong {
  min-width: 0;
  overflow-wrap: anywhere;
}

.exception-card dl {
  gap: 6px;
}

.condition-list span,
.condition-list time {
  color: var(--el-text-color-secondary);
  overflow-wrap: anywhere;
}

.event-list article {
  display: grid;
  grid-template-columns: auto 1fr;
  gap: 3px 8px;
  padding: 8px 0;
  font-size: 11px;
  border-bottom: 1px solid var(--el-border-color-lighter);
}

.event-list time,
.event-list small {
  color: var(--el-text-color-secondary);
}

.event-list span,
.event-list small {
  grid-column: 2;
  overflow-wrap: anywhere;
}

.tab-hint {
  font-size: 12px;
  line-height: 1.6;
}
</style>
