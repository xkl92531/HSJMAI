<script setup lang="ts">
import type { PlcGraphBlockItem } from '@/api/modules/PLC/graphBlock'
import { ElMessage } from 'element-plus'
import { computed, onActivated, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import GraphBlockPreviewDialog from './components/GraphBlockPreviewDialog.vue'
import GraphStationBindingDialog from './components/GraphStationBindingDialog.vue'
import { useGraphBlockCatalog } from './useGraphBlockCatalog'
import { useI18n } from 'vue-i18n'
import messages from './components/lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}

const route = useRoute()
const router = useRouter()
const state = useGraphBlockCatalog()

function friendlyError(error: unknown) {
  const code = error instanceof Error ? error.message : state.safeErrorCode.value
  const labels: Record<string, string> = {
    GraphBlockRequestFailed: t('lineupCatalog_form.errGraphBlockRequestFailed'),
  }
  return labels[code] ?? t('lineupCatalog_form.operationIncomplete', { code: code || 'Unknown' })
}

function optionLabel(code: string | null | undefined, name: string | null | undefined) {
  return ((code || '') + ' ' + (name || '')).trim()
}

function projectName(row: PlcGraphBlockItem) {
  return state.projectNameById.value.get(String(row.source.plcProjectId)) || t('lineupCatalog_form.projectFallback', { id: row.source.plcProjectId })
}

/** 解析任务 ID 尾段（plc_upload_{projectId}_{时间戳}_{GUID} 截 GUID 展示） */
function parseRunTail(parseRunId: string | null) {
  if (!parseRunId) {
    return '—'
  }
  const parts = parseRunId.split('_')
  return parts.length > 1 ? parts[parts.length - 1] : parseRunId
}

function shortHash(sourceHash: string | null) {
  return sourceHash ? sourceHash.slice(0, 16) : '—'
}

async function search() {
  try {
    await state.search()
  }
  catch (error) {
    ElMessage.error(friendlyError(error))
  }
}

async function changeLine(lineId: unknown) {
  try {
    await state.selectLine(String(lineId || ''))
  }
  catch (error) {
    ElMessage.error(friendlyError(error))
  }
}

/** 跳转 GRAPH 智能导入工作台，直接进入该块的生成预览（携带产线/设备定位与块名） */
function openGraphImport(row: PlcGraphBlockItem) {
  router.push(state.buildGraphImportRoute(row.block.blockName))
}

/** 弹框内免上传生成预览（服务端取 ai_plc_sourcefile.filecontent 归档原文） */
const previewVisible = ref(false)
const previewRow = ref<PlcGraphBlockItem | null>(null)

/** 批量绑定 GRAPH 与工站弹框（表格标题区入口） */
const bindingVisible = ref(false)

function bindingChanged() {
  state.refresh().catch(() => {})
}

function openPreviewDialog(row: PlcGraphBlockItem) {
  previewRow.value = row
  previewVisible.value = true
}

function closePreviewDialog() {
  previewVisible.value = false
  previewRow.value = null
}

function changePage(value: number) {
  state.filters.page = value
}

function changePageSize(value: number) {
  state.filters.pageSize = value
  state.filters.page = 1
}

const filterSummary = computed(() => {
  const parts: string[] = []
  if (state.selectedDevice.value) {
    parts.push(t('lineupCatalog_form.filterDevice', { name: state.selectedDevice.value.name || state.selectedDevice.value.plcCode || state.selectedDevice.value.id }))
  }
  if (state.selectedProject.value) {
    parts.push(t('lineupCatalog_form.filterProject', { name: state.selectedProject.value.name }))
  }
  if (state.selectedStation.value) {
    parts.push(t('lineupCatalog_form.filterStation', { name: optionLabel(state.selectedStation.value.code, state.selectedStation.value.name) }))
  }
  if (state.filters.keyword.trim()) {
    parts.push(t('lineupCatalog_form.filterKeyword', { keyword: state.filters.keyword.trim() }))
  }
  return parts.length ? parts.join(' · ') : t('lineupCatalog_form.noFilterSet')
})

onMounted(async () => {
  const projectId = Array.isArray(route.query.plcProjectId)
    ? route.query.plcProjectId[0]
    : route.query.plcProjectId
  mountedYet.value = true
  try {
    await state.initialize(String(projectId || ''))
  }
  catch (error) {
    ElMessage.error(friendlyError(error))
  }
})

// 布局对页面做 KeepAlive 缓存：从工作台返回本页不会重新挂载，onMounted 不会再跑，
// 列表（含 hasPreviewBatch 按钮态）会停留在离开前的快照 —— 激活时静默刷新。
const mountedYet = ref(false)
onActivated(() => {
  if (!mountedYet.value) {
    return
  }
  // 首次挂载紧跟的那次激活由 onMounted 全量初始化负责，跳过
  if (activatedOnce.value) {
    state.refresh().catch(() => {})
  }
  activatedOnce.value = true
})
const activatedOnce = ref(false)
</script>

<template>
  <main class="catalog-page">
    <header class="page-header">
      <div>
        <span>PLC PROJECT / GRAPH BLOCKS</span>
        <h2>{{ $t('lineupCatalog_form.pageTitle') }}</h2>
        <p>{{ $t('lineupCatalog_form.pageIntro') }}</p>
      </div>
      <el-alert
        :title="$t('lineupCatalog_form.dataSourceTip')"
        type="info"
        :closable="false"
        show-icon
      />
    </header>

    <section class="filter-card">
      <div class="filter-strip">
        <label>
          <span>{{ $t('lineupCatalog_form.line') }}</span>
          <el-select v-model="state.filters.lineId" clearable filterable :placeholder="$t('lineupCatalog_form.allLines')" @change="changeLine">
            <el-option v-for="item in state.lines.value" :key="item.id" :value="item.id" :label="optionLabel(item.code, item.name)" />
          </el-select>
        </label>
        <label>
          <span>{{ $t('lineupCatalog_form.process') }}</span>
          <el-select v-model="state.filters.processId" clearable filterable :placeholder="$t('lineupCatalog_form.allProcesses')" @change="state.selectProcess(String($event || ''))">
            <el-option v-for="item in state.availableProcesses.value" :key="item.id" :value="item.id" :label="optionLabel(item.code, item.name)" />
          </el-select>
        </label>
        <label>
          <span>{{ $t('lineupCatalog_form.station') }}</span>
          <el-select v-model="state.filters.stationId" clearable filterable :placeholder="$t('lineupCatalog_form.allStations')" @change="state.selectStation(String($event || ''))">
            <el-option v-for="item in state.availableStations.value" :key="item.id" :value="item.id" :label="optionLabel(item.code, item.name)" />
          </el-select>
        </label>
        <label>
          <span>{{ $t('lineupCatalog_form.plcDevice') }}</span>
          <el-select v-model="state.filters.deviceId" clearable filterable :placeholder="$t('lineupCatalog_form.allDevicesInUse')" @change="state.selectDevice(String($event || ''))">
            <el-option v-for="item in state.availableDevices.value" :key="item.id" :value="item.id" :label="item.name || item.plcCode || item.id" />
          </el-select>
        </label>
        <label>
          <span>{{ $t('lineupCatalog_form.plcProject') }}</span>
          <el-select v-model="state.filters.projectId" clearable filterable :placeholder="$t('lineupCatalog_form.allProjectsInUse')" @change="state.selectProject(String($event || ''))">
            <el-option v-for="item in state.availableProjects.value" :key="item.id" :value="item.id" :label="item.name" />
          </el-select>
        </label>
        <label class="keyword-filter">
          <span>{{ $t('lineupCatalog_form.blockName') }}</span>
          <el-input v-model="state.filters.keyword" clearable placeholder="FB260 / CacheGraph" @keyup.enter="search" />
        </label>
        <el-button type="primary" :loading="state.loading.value" @click="search">
          {{ $t('lineupCatalog_form.query') }}
        </el-button>
      </div>
      <p class="filter-summary">
        {{ filterSummary }}
      </p>
    </section>

    <section class="summary-grid">
      <article>
        <span>{{ $t('lineupCatalog_form.queryResult') }}</span>
        <strong>{{ state.items.value.length }}</strong>
        <small>{{ $t('lineupCatalog_form.graphBlockColumn') }}</small>
      </article>
      <article>
        <span>{{ $t('lineupCatalog_form.pageDisplay') }}</span>
        <strong>{{ state.pagedItems.value.length }}</strong>
        <small>{{ $t('lineupCatalog_form.pageN', { n: state.filters.page }) }}</small>
      </article>
      <article>
        <span>{{ $t('lineupCatalog_form.pageProjects') }}</span>
        <strong>{{ state.pageSummary.value.projectCount }}</strong>
        <small>{{ $t('lineupCatalog_form.plcProject') }}</small>
      </article>
      <article>
        <span>{{ $t('lineupCatalog_form.pageBlockTypes') }}</span>
        <strong>{{ state.pageSummary.value.blockTypeCount }}</strong>
        <small>{{ $t('lineupCatalog_form.blockTypeKinds') }}</small>
      </article>
    </section>

    <section class="table-card">
      <div class="table-heading">
        <div>
          <span>PARSED GRAPH BLOCKS</span>
          <h3>{{ $t('lineupCatalog_form.parseResultHeading') }}</h3>
        </div>
        <div class="table-heading-actions">
          <el-button type="primary" plain @click="bindingVisible = true">
            {{ $t('lineupCatalog_form.batchBindStations') }}
          </el-button>
        <small>{{ $t('lineupCatalog_form.deviceSourceTip') }}</small>
        </div>
      </div>
      <el-table v-loading="state.loading.value" :data="state.pagedItems.value" height="calc(100vh - 470px)" min-height="300" stripe border>
        <el-table-column :label="$t('lineupCatalog_form.graphBlockColumn')" min-width="260" fixed="left">
          <template #default="{ row }">
            <div class="block-cell">
              <strong>{{ row.block.blockName }}</strong>
              <span>{{ row.block.blockType }}{{ row.block.blockNumber ?? '' }} · BlockId {{ row.source.plcBlockId }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column :label="$t('lineupCatalog_form.plcProjectColumn')" min-width="200" show-overflow-tooltip>
          <template #default="{ row }">
            {{ projectName(row) }}
          </template>
        </el-table-column>
        <el-table-column :label="$t('lineupCatalog_form.programmingLanguage')" width="110" align="center">
          <template #default="{ row }">
            <el-tag effect="plain">
              {{ row.block.programmingLanguage }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="$t('lineupCatalog_form.parseTraceColumn')" min-width="300">
          <template #default="{ row }">
            <div class="revision-cell">
              <span :title="row.source.parseRunId">ParseRun {{ parseRunTail(row.source.parseRunId) }}</span>
              <span :title="row.source.sourceHash">SHA256 {{ shortHash(row.source.sourceHash) }}…</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column :label="$t('common.operate')" width="180" fixed="right" align="center">
          <template #default="{ row }">
            <el-button type="primary" link @click="openPreviewDialog(row)">
              {{ row.hasPreviewBatch ? $t('lineupCatalog_form.regeneratePreview') : $t('lineupCatalog_form.generatePreview') }}
            </el-button>
            <el-button v-if="row.hasPreviewBatch" link @click="openGraphImport(row)">
              {{ $t('lineupCatalog_form.viewFlowchart') }}
            </el-button>
          </template>
        </el-table-column>
      </el-table>
      <div class="pagination-row">
        <el-pagination
          :current-page="state.filters.page"
          :page-size="state.filters.pageSize"
          :total="state.items.value.length"
          :page-sizes="[10, 20, 50, 100]"
          layout="total, sizes, prev, pager, next, jumper"
          @current-change="changePage"
          @size-change="changePageSize"
        />
      </div>
    </section>

    <GraphBlockPreviewDialog
      v-if="previewVisible && previewRow"
      v-model="previewVisible"
      :block="previewRow"
      :initial-line-id="state.filters.lineId"
      :initial-device-id="state.filters.deviceId"
      @closed="closePreviewDialog"
    />
    <GraphStationBindingDialog
      v-model="bindingVisible"
      :initial-line-id="state.filters.lineId || ''"
      :initial-project-id="state.filters.projectId || ''"
      @changed="bindingChanged"
    />
  </main>
</template>

<style scoped>
.catalog-page {
  display: grid;
  gap: 12px;
  min-width: 0;
  min-height: 100%;
  padding: 14px;
  background: var(--el-fill-color-lighter);
}

.page-header,
.filter-card,
.table-card,
.summary-grid article {
  background: var(--el-bg-color);
  border: 1px solid var(--el-border-color-light);
  border-radius: 8px;
}

.page-header {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(400px, 44%);
  gap: 24px;
  align-items: center;
  padding: 16px 18px;
}

.page-header > div > span,
.table-heading span {
  font-size: 10px;
  font-weight: 700;
  color: var(--el-color-primary);
  letter-spacing: 0.1em;
}

.page-header h2,
.table-heading h3 {
  margin: 3px 0;
}

.page-header p,
.table-heading small {
  margin: 0;
  color: var(--el-text-color-secondary);
}

.filter-card {
  min-width: 0;
  padding: 12px;
  overflow-x: auto;
}

.filter-strip {
  display: grid;
  grid-template-columns: repeat(5, minmax(150px, 1fr)) minmax(190px, 1.2fr) auto;
  gap: 10px;
  align-items: end;
  min-width: 1150px;
}

.filter-strip label {
  display: grid;
  gap: 5px;
}

.filter-strip label > span {
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.filter-summary {
  margin: 8px 2px 0;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.summary-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 10px;
}

.summary-grid article {
  display: grid;
  grid-template-columns: 1fr auto;
  gap: 2px 10px;
  padding: 11px 14px;
}

.summary-grid article > span,
.summary-grid small {
  color: var(--el-text-color-secondary);
}

.summary-grid strong {
  grid-row: span 2;
  font-size: 28px;
  color: var(--el-color-primary);
}

.table-card {
  min-width: 0;
  padding: 12px;
  overflow: hidden;
}

.table-heading,
.pagination-row {
  display: flex;
  gap: 18px;
  align-items: center;
  justify-content: space-between;
}

.table-heading {
  padding: 0 2px 10px;
}

.pagination-row {
  justify-content: flex-end;
  padding-top: 10px;
}

.block-cell,
.revision-cell {
  display: grid;
  gap: 3px;
}

.block-cell span,
.revision-cell span {
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

@media (width <= 1000px) {
  .page-header {
    grid-template-columns: 1fr;
  }

  .summary-grid {
    grid-template-columns: repeat(2, 1fr);
  }
}
.table-heading-actions {
  display: flex;
  gap: 12px;
  align-items: center;
}
</style>
