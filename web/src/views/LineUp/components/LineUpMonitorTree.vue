<script setup lang="ts">
import type { LineUpMonitorTreeNode } from './lineUpMonitorTree'
import type {
  LineUpMonitorNavigationNode,
  LineUpMonitorRuntimeStatus,
} from '@/api/modules/LineUp/monitor'
import type { EntityId } from '@/api/modules/LineUp/types'
import { Refresh, Search } from '@element-plus/icons-vue'
import { computed, onBeforeUnmount, ref } from 'vue'
import {
  createLineUpMonitorTreeSearch,
  projectMonitorTree,
} from './lineUpMonitorTree'
import { useI18n } from 'vue-i18n'
import messages from './lang'

const props = defineProps<{
  nodes: LineUpMonitorNavigationNode[]
  selectedSubStationId: EntityId | null
  /** 当前选中的 GRAPH（bindingId），用于 Graph 叶子高亮 */
  selectedBindingId?: EntityId
  loading: boolean
  collapsed: boolean
}>()

const emit = defineEmits<{
  selectSubStation: [subStationId: EntityId]
  selectGraph: [bindingId: EntityId, subStationId: EntityId]
  search: [keyword: string]
  refresh: []
  toggleCollapsed: []
}>()

const keyword = ref('')
const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}
const treeRef = ref<{
  store: { nodesMap: Record<string, { expanded: boolean }> }
} | null>(null)
// data 只依赖 nodes：点击工站/GRAPH 不替换整树 data（防 el-tree 重建丢滚动）。
const tree = computed(() => [...projectMonitorTree(props.nodes)])
const treeProps = { children: 'children', label: 'name' }
const search = createLineUpMonitorTreeSearch(value => emit('search', value))
// 选中 GRAPH 时高亮 graph 节点，否则高亮工位节点
const currentNodeKey = computed(() => {
  if (props.selectedBindingId
    && props.nodes.some(node => node.nodeId === `graph:${props.selectedBindingId}`)) {
    return `graph:${props.selectedBindingId}`
  }
  return props.selectedSubStationId ? `substation:${props.selectedSubStationId}` : undefined
})

const statusText = computed<Record<LineUpMonitorRuntimeStatus, string>>(() => ({
  Unconfigured: t('lineupShared.statusUnconfigured'),
  NeedsPrepare: t('lineupShared.statusNeedsPrepare'),
  NeedsReview: t('lineupShared.statusNeedsReview'),
  Ready: t('lineupShared.statusReady'),
  Enabled: t('lineupShared.statusEnabled'),
  Online: t('lineupShared.statusOnline'),
  Warning: t('lineupShared.statusWarning'),
  Error: t('lineupShared.statusError'),
}))
const statusToken: Record<LineUpMonitorRuntimeStatus, string> = {
  Unconfigured: 'muted',
  NeedsPrepare: 'warning',
  NeedsReview: 'warning',
  Ready: 'ready',
  Enabled: 'enabled',
  Online: 'online',
  Warning: 'warning',
  Error: 'error',
}

function handleSearch(value: string) {
  keyword.value = value
  search.schedule(value)
}

function handleNodeClick(data: LineUpMonitorTreeNode) {
  if (data.nodeType === 'SubStation') {
    emit('selectSubStation', data.entityId)
    return
  }
  if (data.nodeType === 'Graph') {
    const parent = props.nodes.find(node => node.nodeId === data.parentNodeId)
    if (parent?.nodeType === 'SubStation') {
      emit('selectGraph', data.entityId, parent.entityId)
    }
    return
  }
  const node = treeRef.value?.store.nodesMap[data.nodeId]
  if (node) {
    node.expanded = !node.expanded
  }
}

onBeforeUnmount(search.dispose)
</script>

<template>
  <aside class="monitor-tree" :class="{ 'monitor-tree--collapsed': collapsed }">
    <button
      v-if="collapsed"
      class="monitor-tree__expand"
      type="button"
      :aria-label="$t('lineupShared.expandNavAria')"
      @click="emit('toggleCollapsed')"
    >
      {{ $t('lineupShared.collapsedStation') }}
    </button>
    <template v-else>
      <header class="monitor-tree__header">
        <div>
          <strong>{{ $t('lineupShared.treeHeaderTitle') }}</strong>
          <small>{{ $t('lineupShared.treeHeaderHint') }}</small>
        </div>
        <div class="monitor-tree__tools">
          <el-button text :icon="Refresh" :loading="loading" :aria-label="$t('lineupShared.refreshAria')" @click="emit('refresh')" />
          <el-button text :aria-label="$t('lineupShared.collapseAria')" @click="emit('toggleCollapsed')">
            «
          </el-button>
        </div>
      </header>
      <el-input
        :model-value="keyword"
        :prefix-icon="Search"
        clearable
        :placeholder="$t('lineupShared.searchPlaceholder')"
        @update:model-value="handleSearch"
      />
      <div v-loading="loading" class="monitor-tree__scroll">
        <el-tree
          ref="treeRef"
          :data="tree"
          :props="treeProps"
          :indent="14"
          node-key="nodeId"
          :expand-on-click-node="false"
          :highlight-current="true"
          :current-node-key="currentNodeKey"
          default-expand-all
          @node-click="handleNodeClick"
        >
          <template #default="{ data }: { data: LineUpMonitorTreeNode }">
            <div class="monitor-tree__node" :class="{ 'is-leaf': data.isLeaf, 'is-graph': data.nodeType === 'Graph' }">
              <span class="monitor-tree__identity">
                <b>{{ data.code || data.name }}</b>
                <small v-if="data.code && data.name">{{ data.name }}</small>
              </span>
              <span
                v-if="data.showStatus"
                class="monitor-tree__status"
                :data-token="statusToken[data.runtimeStatus]"
              >
                {{ statusText[data.runtimeStatus] }}
              </span>
            </div>
          </template>
        </el-tree>
      </div>
    </template>
  </aside>
</template>

<style scoped lang="scss">
.monitor-tree {
  display: flex;
  flex-direction: column;
  gap: 12px;
  width: 320px;
  min-width: 280px;
  height: 100%;
  min-height: 0;
  padding: 14px;
  overflow: hidden;
  background: var(--el-bg-color);
  border-right: 1px solid var(--el-border-color-light);
}

.monitor-tree--collapsed {
  width: 44px;
  min-width: 44px;
  padding: 8px 5px;
}

.monitor-tree__expand {
  padding: 10px 6px;
  color: var(--el-color-primary);
  cursor: pointer;
  background: transparent;
  border: 0;
  writing-mode: vertical-rl;
}

.monitor-tree__header,
.monitor-tree__tools,
.monitor-tree__node,
.monitor-tree__identity {
  display: flex;
  align-items: center;
}

.monitor-tree__header {
  justify-content: space-between;
}

.monitor-tree__header small,
.monitor-tree__identity small {
  color: var(--el-text-color-secondary);
}

.monitor-tree__header > div:first-child,
.monitor-tree__identity {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  min-width: 0;
}

.monitor-tree__scroll {
  flex: 1;
  min-height: 0;
  overflow: hidden auto;
}

.monitor-tree__scroll :deep(.el-tree-node__content) {
  align-items: stretch;
  height: auto;
  min-height: 44px;
  padding-block: 3px;
}

.monitor-tree__scroll :deep(.el-tree-node__expand-icon) {
  flex: none;
  align-self: center;
}

.monitor-tree__node {
  flex: 1;
  gap: 10px;
  justify-content: space-between;
  width: auto;
  min-width: 0;
  padding: 3px 8px 3px 0;
}

/* GRAPH 叶子节点（工位下的 graph 块）比层级节点更紧凑 */
.monitor-tree__node.is-graph {
  min-height: 34px;
  padding-block: 1px;
}

.monitor-tree__node.is-graph b {
  font-size: 12px;
  font-weight: 600;
  font-family: Consolas, monospace;
}

.monitor-tree__identity {
  flex: 1;
  gap: 2px;
  overflow: hidden;
}

.monitor-tree__identity b,
.monitor-tree__identity small {
  width: 100%;
  max-width: none;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.monitor-tree__status {
  flex: none;
  padding: 2px 7px;
  font-size: 11px;
  color: var(--el-text-color-secondary);
  background: var(--el-fill-color-light);
  border-radius: 999px;
}

.monitor-tree__status[data-token="online"] {
  color: var(--el-color-success);
  background: var(--el-color-success-light-9);
}

.monitor-tree__status[data-token="error"] {
  color: var(--el-color-danger);
  background: var(--el-color-danger-light-9);
}

.monitor-tree__status[data-token="warning"] {
  color: var(--el-color-warning);
  background: var(--el-color-warning-light-9);
}

.monitor-tree__status[data-token="ready"],
.monitor-tree__status[data-token="enabled"] {
  color: var(--el-color-primary);
  background: var(--el-color-primary-light-9);
}
</style>
