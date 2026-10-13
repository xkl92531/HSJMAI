<script setup lang="ts">
import type { TiaGraphViewState } from '../components/tiaGraphLayout'
import type { TiaGraphSelection } from '../components/tiaGraphSelection'
import type { LineUpMonitorEvent } from '@/api/modules/LineUp/monitor'
import type { EntityId } from '@/api/modules/LineUp/types'
import { ElMessage } from 'element-plus'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import lineUpGraphCatalogApi from '@/api/modules/LineUp/catalog'
import lineUpMonitorApi from '@/api/modules/LineUp/monitor'
import LineUpMonitorTree from '../components/LineUpMonitorTree.vue'
import { symbolNamesForSelection, withAdjacentTransitionNetworks } from '../components/tiaGraphSelection'
import { buildImageMarkRoute } from '../image-marks/imageMarkNavigation'
import GraphPreviewPanel from '../workspace/components/GraphPreviewPanel.vue'
import LineUpRuntimeDetail from './components/LineUpRuntimeDetail.vue'
import {
  conditionsForMonitorSelection,
  matchingMonitorSnapshot,
  monitorAvailabilityText,
  normalizeGatewayStatus,
  projectMonitorSnapshot,
} from './monitorProjection'
import { useLineUpMonitor } from './useLineUpMonitor'
import { createLineUpMonitorWorkspace } from './useLineUpMonitorWorkspace'
import { useI18n } from 'vue-i18n'
import messages from './components/lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}

const route = useRoute()
const router = useRouter()
// 嵌入对接模式（/lineup/monitor-embed）：隐藏跳转到平台内其它页面的按钮，
// 页面高度按整屏计算（无 Layout 的 tabbar/toolbar 占位）。
const isEmbeddedRoute = computed(() => route.path.startsWith('/lineup/monitor-embed'))
let refreshPublishedGraph: () => Promise<void> = async () => {}
const live = useLineUpMonitor(undefined, undefined, () => refreshPublishedGraph())
const workspace = createLineUpMonitorWorkspace(lineUpMonitorApi, lineUpGraphCatalogApi, live)
refreshPublishedGraph = workspace.refreshSelectedGraph

const selectedGraphElement = ref<TiaGraphSelection | null>(null)
const viewState = ref<TiaGraphViewState | null>(null)
const events = ref<LineUpMonitorEvent[]>([])
const nextEventId = ref('')
const hasMoreEvents = ref(false)
const loadingEvents = ref(false)
const treeCollapsed = ref(false)
const treeDrawerOpen = ref(false)
const narrowViewport = ref(false)
let removeMediaListener: (() => void) | null = null

const selectedBinding = workspace.selectedBinding
const sequenceStatuses = computed(() => {
  const statuses: Record<string, string> = {}
  for (const sequence of selectedBinding.value?.sequences ?? []) {
    if (sequence.runtimeStatus) {
      statuses[sequence.sequenceSourceKey] = sequence.runtimeStatus
    }
  }
  return statuses
})
const matchingSnapshot = computed(() => workspace.publishedRevisionId.value === workspace.targetRevisionId.value
  ? matchingMonitorSnapshot(live.definition.value, live.snapshot.value, {
      bindingId: selectedBinding.value?.bindingId ?? '',
      publishedRevisionId: workspace.targetRevisionId.value,
      stationId: selectedBinding.value?.stationId ?? '',
      sequenceSourceKey: workspace.selectedSequenceKey.value,
    })
  : null)
const selectedConditions = computed(() => conditionsForMonitorSelection(
  live.definition.value,
  matchingSnapshot.value,
  selectedGraphElement.value,
))
/** 工步详情面板 BOM 图片：选中节点关联的符号名（工步聚合相邻转换条件），
 * 与 Lup_BomDirectory.TagCode 匹配；与 GRAPH 结构管理预览（GraphBlockPreviewDialog）同一套提取规则 */
const bomReferences = computed(() => {
  const sequence = workspace.selectedSequence.value
  const selection = selectedGraphElement.value
  if (!sequence || !selection) {
    return []
  }
  return symbolNamesForSelection(withAdjacentTransitionNetworks(
    { ...sequence, actions: sequence.actions ?? [] },
    selection,
  ))
})
const runtimeProjection = computed(() => matchingSnapshot.value
  ? projectMonitorSnapshot(matchingSnapshot.value)
  : null)
const gatewayStatus = computed(() => matchingSnapshot.value
  ? normalizeGatewayStatus(matchingSnapshot.value.connection.status)
  : '')
const runtimeText = computed(() => monitorAvailabilityText({
  hasSelection: Boolean(workspace.selectedSubStationId.value),
  hasImport: Boolean(workspace.preview.value),
  hasBinding: Boolean(selectedBinding.value),
  bindingStatus: selectedBinding.value?.bindingStatus,
  runtimeBindingReady: selectedBinding.value?.runtimeBindingReady,
  monitoringEnabled: selectedBinding.value?.monitoringEnabled,
  connectionState: live.connectionState.value,
  gatewayStatus: gatewayStatus.value,
  snapshotRevision: live.snapshot.value?.snapshotRevision,
  safeErrorCode: live.safeErrorCode.value || workspace.safeErrorCode.value,
}))
const selectedPathText = computed(() => workspace.selectedPath.value.map(node => (
  node.code || node.name
)).join(' / '))

// 树状态自动刷新（2026-09-21）：选中工位后订阅触发服务端采集，快照状态变化
// （连接态/网关基调改变，如首帧到达「已启用→在线」）时去抖刷新导航树，
// 免去手动刷新才能看到树上状态更新的问题；基调不变则不刷新，避免 500ms 快照流打爆接口。
let navigationRefreshTimer: ReturnType<typeof setTimeout> | undefined
watch([() => live.connectionState.value, gatewayStatus], () => {
  clearTimeout(navigationRefreshTimer)
  navigationRefreshTimer = setTimeout(() => {
    void workspace.refreshNavigation()
  }, 1200)
})
onBeforeUnmount(() => clearTimeout(navigationRefreshTimer))

function clearGraphSelection() {
  selectedGraphElement.value = null
  viewState.value = null
}

async function selectSubStation(subStationId: EntityId) {
  clearGraphSelection()
  clearEvents()
  treeDrawerOpen.value = false
  await workspace.selectSubStation(subStationId)
  await loadEvents(true)
}

// 点击树内 GRAPH 叶子：选中工位并加载指定 graph（binding），顺控默认取第一个。
// 同工位内切换 graph 走轻量 selectBinding（断开重连+重载预览），
// 跨工位才走完整 selectSubStation（重载 bindings/assets）。
async function selectGraph(bindingId: EntityId, subStationId: EntityId) {
  clearGraphSelection()
  clearEvents()
  treeDrawerOpen.value = false
  if (workspace.selectedSubStationId.value === subStationId) {
    await workspace.selectBinding(bindingId)
  }
  else {
    await workspace.selectSubStation(subStationId, bindingId)
  }
  await loadEvents(true)
}

async function selectSequence(sourceKey: string) {
  if (sourceKey === workspace.selectedSequenceKey.value) {
    return
  }
  clearGraphSelection()
  clearEvents()
  await workspace.selectSequence(sourceKey)
  await loadEvents(true)
}

function selectNode(sourceKey: string) {
  viewState.value = {
    ...(viewState.value ?? { zoom: 1, translateX: 0, translateY: 0, selectedNodeKey: '' }),
    selectedNodeKey: sourceKey,
  }
}

function selectGraphElement(selection: TiaGraphSelection | null) {
  selectedGraphElement.value = selection
  if (viewState.value) {
    viewState.value = {
      ...viewState.value,
      selectedNodeKey: selection?.node.sourceKey ?? '',
    }
  }
}

async function initialize() {
  try {
    const restored = await workspace.initializeRoute({
      subStationId: String(route.query.subStationId ?? ''),
      bindingId: String(route.query.bindingId ?? ''),
      sequenceSourceKey: String(route.query.sequenceSourceKey ?? ''),
      lineId: String(route.query.lineId ?? ''),
      deviceId: String(route.query.deviceId ?? ''),
      stationId: String(route.query.stationId ?? ''),
      subStationCode: String(route.query.subStationCode ?? ''),
      lineCode: String(route.query.lineCode ?? ''),
    })
    if (!restored) {
      // 直达参数无法还原时清空 query 重进；用 route.path 而非硬编码路径，
      // 保证 /lineup/monitor-embed 嵌入页不会跳回带布局的 /lineup/monitor。
      await router.replace({ path: route.path })
    }
    else {
      await loadEvents(true)
    }
  }
  catch {
    ElMessage.error(t('lineupMonitor_form.errMonitorLoadFailed'))
  }
}

async function refresh() {
  clearGraphSelection()
  clearEvents()
  await workspace.refresh()
  await loadEvents(true)
}

async function loadEvents(reset = false) {
  const bindingId = selectedBinding.value?.bindingId
  const sequenceSourceKey = workspace.selectedSequenceKey.value
  if (!bindingId || !sequenceSourceKey) {
    clearEvents()
    return
  }
  loadingEvents.value = true
  try {
    const page = await lineUpMonitorApi.getEvents(
      bindingId,
      sequenceSourceKey,
      reset ? undefined : nextEventId.value || undefined,
      50,
    )
    events.value = reset ? page.items : [...events.value, ...page.items]
    nextEventId.value = page.nextBeforeEventId
    hasMoreEvents.value = page.hasMore
  }
  catch {
    ElMessage.warning(t('lineupMonitor_form.errEventsLoadFailed'))
  }
  finally {
    loadingEvents.value = false
  }
}

function clearEvents() {
  events.value = []
  nextEventId.value = ''
  hasMoreEvents.value = false
}

// 工站/Sequence 切换不回写路由 query（2026-09-04 修复）：
// 布局层 RouterView 以 route.fullPath 作 :key，query 变化会整页销毁重建 + 重复初始化，
// 表现为「切工站时整页重新加载」。改为纯组件内状态切换；入口仍读一次 query 支持直达链接。

function historyLink() {
  return {
    path: '/lineup/history',
    query: {
      subStationId: workspace.selectedSubStationId.value ?? undefined,
      bindingId: selectedBinding.value?.bindingId || undefined,
      sequenceSourceKey: workspace.selectedSequenceKey.value || undefined,
    },
  }
}

function openImageMarks() {
  const subStationId = workspace.selectedSubStationId.value
  if (!subStationId) {
    return
  }
  void router.push(buildImageMarkRoute(subStationId))
}

function installResponsiveTree() {
  const query = window.matchMedia('(width <= 900px)')
  const update = () => {
    narrowViewport.value = query.matches
    if (!query.matches) {
      treeDrawerOpen.value = false
    }
  }
  update()
  query.addEventListener('change', update)
  removeMediaListener = () => query.removeEventListener('change', update)
}

onMounted(() => {
  installResponsiveTree()
  void initialize()
})
onBeforeUnmount(() => {
  removeMediaListener?.()
  void workspace.dispose()
})
</script>

<template>
  <div class="lineup-monitor-shell" :class="{ 'is-embedded': isEmbeddedRoute }">
    <LineUpMonitorTree
      v-if="!narrowViewport"
      class="desktop-tree"
      :nodes="workspace.navigationNodes.value"
      :selected-sub-station-id="workspace.selectedSubStationId.value"
      :selected-binding-id="selectedBinding?.bindingId || ''"
      :loading="workspace.navigationLoading.value"
      :collapsed="treeCollapsed"
      @select-sub-station="selectSubStation"
      @select-graph="selectGraph"
      @search="workspace.refreshNavigation"
      @refresh="workspace.refreshNavigation()"
      @toggle-collapsed="treeCollapsed = !treeCollapsed"
    />
    <el-drawer v-model="treeDrawerOpen" :title="$t('lineupMonitor_form.drawerTitle')" direction="ltr" size="min(86vw, 320px)">
      <LineUpMonitorTree
        :nodes="workspace.navigationNodes.value"
        :selected-sub-station-id="workspace.selectedSubStationId.value"
        :selected-binding-id="selectedBinding?.bindingId || ''"
        :loading="workspace.navigationLoading.value"
        :collapsed="false"
        @select-sub-station="selectSubStation"
        @select-graph="selectGraph"
        @search="workspace.refreshNavigation"
        @refresh="workspace.refreshNavigation()"
        @toggle-collapsed="treeDrawerOpen = false"
      />
    </el-drawer>

    <main v-loading="workspace.contentLoading.value" class="lineup-monitor">
      <el-alert
        v-if="live.isStaleOrOffline.value"
        :title="$t('lineupMonitor_form.qualityAlertTitle')"
        type="warning"
        :closable="false"
        show-icon
      />

      <!-- 2026-09：整行页头（设备运行监控大标题）移除，动作按钮上 GRAPH 面板表头（#header-actions）；
           无图可看的空态下面板表头不在场，用这条极简工具条安放按钮，不占流程图空间 -->
      <template v-if="!workspace.selectedSubStationId.value || (!workspace.preview.value && !workspace.contentLoading.value)">
        <div class="monitor-mini-bar">
          <span class="monitor-mini-bar__path">{{ selectedPathText || $t('lineupMonitor_form.defaultPathHint') }}</span>
          <div class="monitor-actions">
            <el-button v-if="narrowViewport" @click="treeDrawerOpen = true">
              {{ $t('lineupMonitor_form.pickSubStation') }}
            </el-button>
            <el-button @click="refresh">
              {{ $t('lineupMonitor_form.refresh') }}
            </el-button>
            <el-button v-if="!isEmbeddedRoute" @click="router.push(historyLink())">
              {{ $t('lineupMonitor_form.viewHistory') }}
            </el-button>
            <el-button v-if="!isEmbeddedRoute" @click="router.push({ path: '/lineup/bindings', query: { subStationId: workspace.selectedSubStationId.value ?? undefined } })">
              {{ $t('lineupMonitor_form.bindingSettings') }}
            </el-button>
            <el-button v-if="!isEmbeddedRoute" :disabled="!workspace.selectedSubStationId.value" @click="openImageMarks">
              {{ $t('lineupMonitor_form.bomImageBinding') }}
            </el-button>
          </div>
        </div>
        <el-empty
          v-if="!workspace.selectedSubStationId.value"
          class="monitor-empty"
          :description="$t('lineupMonitor_form.emptyPickSubStation')"
        />
        <el-empty
          v-else
          class="monitor-empty"
          :description="$t('lineupMonitor_form.emptyNoPublishedGraph')"
        />
      </template>

      <section v-else class="monitor-workbench">
        <GraphPreviewPanel
          compact
          :title-override="selectedPathText"
          :sequences="workspace.sequences.value"
          :model-value="workspace.selectedSequenceKey.value"
          :binding-status="runtimeText"
          :view-state="viewState"
          :runtime="runtimeProjection"
          :runtime-values="selectedConditions"
          :sequence-statuses="sequenceStatuses"
          :step-tag-project-id="selectedBinding?.plcProjectId || ''"
          :step-tag-fb-name="selectedBinding?.graphSourceName || ''"
          :bom-sub-station-id="workspace.selectedSubStationId.value || ''"
          :bom-references="bomReferences"
          @update:model-value="selectSequence"
          @node-click="selectNode"
          @selection-change="selectGraphElement"
          @view-state-change="viewState = $event"
        >
          <template #header-actions>
            <div class="monitor-actions">
              <el-button v-if="narrowViewport" @click="treeDrawerOpen = true">
                {{ $t('lineupMonitor_form.pickSubStation') }}
              </el-button>
              <el-button @click="refresh">
                {{ $t('lineupMonitor_form.refresh') }}
              </el-button>
              <el-button v-if="!isEmbeddedRoute" @click="router.push(historyLink())">
                {{ $t('lineupMonitor_form.viewHistory') }}
              </el-button>
              <el-button v-if="!isEmbeddedRoute" @click="router.push({ path: '/lineup/bindings', query: { subStationId: workspace.selectedSubStationId.value ?? undefined } })">
                {{ $t('lineupMonitor_form.bindingSettings') }}
              </el-button>
              <el-button v-if="!isEmbeddedRoute" :disabled="!workspace.selectedSubStationId.value" @click="openImageMarks">
                {{ $t('lineupMonitor_form.bomImageBinding') }}
              </el-button>
            </div>
          </template>
          <template #runtime-detail="{ activeTab }">
            <LineUpRuntimeDetail
              :selection="selectedGraphElement"
              :conditions="selectedConditions"
              :snapshot="matchingSnapshot"
              :events="events"
              :station-id="selectedBinding?.stationId || ''"
              :sub-station-id="workspace.selectedSubStationId.value || ''"
              :plc-project-id="selectedBinding?.plcProjectId || ''"
              :loading-events="loadingEvents"
              :has-more-events="hasMoreEvents"
              :active-tab="activeTab"
              embedded
              @load-more-events="loadEvents(false)"
            />
          </template>
        </GraphPreviewPanel>
      </section>
    </main>
  </div>
</template>

<style scoped>
.lineup-monitor-shell {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr);
  min-width: 0;
  height: calc(100vh - 116px);
  min-height: 620px;
  overflow: hidden;
  background: var(--el-fill-color-lighter);
}

/* 嵌入页无 Layout（无 tabbar/toolbar/copyright 占位），整屏可用 */
.lineup-monitor-shell.is-embedded {
  height: 100vh;
}

.lineup-monitor {
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-width: 0;
  min-height: 0;
  padding: 10px;
  overflow: hidden auto;
}

/* 空态（未选工位/无已发布图）工具条：面板表头不在场时安放动作按钮 */
.monitor-mini-bar {
  display: flex;
  flex: none;
  gap: 12px;
  align-items: center;
  justify-content: space-between;
  padding: 6px 12px;
  background: var(--el-bg-color);
  border: 1px solid var(--el-border-color-light);
  border-radius: 6px;
}

.monitor-mini-bar__path {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  font-size: 12px;
  white-space: nowrap;
  text-overflow: ellipsis;
  color: var(--el-text-color-secondary);
}

.monitor-actions {
  display: flex;
  flex: none;
  gap: 6px;
  align-items: center;
}

/* 按钮上表头后统一小号，压缩表头高度让位给流程图画布 */
.monitor-actions :deep(.el-button) {
  height: 26px;
  padding: 5px 10px;
  font-size: 12px;
}

.monitor-empty,
.monitor-workbench {
  flex: 1;
  min-height: 0;
}

.monitor-workbench {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  min-width: 0;
  min-height: 0;
}

@media (width <= 900px) {
  .lineup-monitor-shell {
    grid-template-columns: minmax(0, 1fr);
  }

  .desktop-tree {
    display: none;
  }
}

@media (width <= 720px) {
  .lineup-monitor-shell {
    height: auto;
    min-height: calc(100vh - 100px);
    overflow: visible;
  }

  .lineup-monitor {
    overflow: visible;
  }

  .monitor-mini-bar {
    flex-wrap: wrap;
  }

  .monitor-actions {
    flex-wrap: wrap;
  }
}
</style>
