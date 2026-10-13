<route lang="yaml">
meta:
  title: 产线信号实况
</route>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { useI18n } from 'vue-i18n'
import StopCauseDiagnosisAPI from '@/api/modules/PLC/stopCauseDiagnosis'
import ProjectAPI from '@/api/modules/PLC/project'
import messages from './signal-live.lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const route = useRoute()
const router = useRouter()

// ── 项目选择 ──
const projectList = ref<{ id: string, projectName: string }[]>([])
const selectedProjectId = ref<string>(route.query.projectId as string || '')

async function loadProjects() {
  try {
    const res: any = await ProjectAPI.list({ page: 1, limit: 200 })
    projectList.value = res?.data || []
    // 如果 URL 没有 projectId 且有项目，默认选第一个
    if (!selectedProjectId.value && projectList.value.length > 0) {
      selectedProjectId.value = String(projectList.value[0].id)
    }
  }
  catch { /* 忽略 */ }
}

function onProjectChange(id: string) {
  selectedProjectId.value = id
  router.replace({ query: { ...route.query, projectId: id } })
  fetchData()
}

const projectId = computed(() => selectedProjectId.value)

const loading = ref(false)
const stations = ref<any[]>([])
const lastRefreshTime = ref('')
const autoRefreshEnabled = ref(true)
const autoRefreshSeconds = ref(10)
const expandedStations = ref<Set<string>>(new Set())
let refreshTimer: ReturnType<typeof setInterval> | null = null

// 加载数据
async function fetchData() {
  if (!projectId.value) {
    ElMessage.warning(t('signal_live.missingProjectId'))
    return
  }
  loading.value = true
  try {
    const res: any = await StopCauseDiagnosisAPI.getAllStationChanges({
      projectId: projectId.value,
      changesPerStation: 30,
    })
    if (res.isSuccess) {
      stations.value = res.data || []
      lastRefreshTime.value = new Date().toLocaleTimeString('zh-CN', { hour12: false })
    }
    else {
      ElMessage.error(t('signal_live.loadFailed'))
    }
  }
  catch (err: any) {
    ElMessage.error(err?.message || t('signal_live.requestError'))
  }
  finally {
    loading.value = false
  }
}

// 自动刷新
function startAutoRefresh() {
  stopAutoRefresh()
  if (autoRefreshEnabled.value) {
    refreshTimer = setInterval(fetchData, autoRefreshSeconds.value * 1000)
  }
}
function stopAutoRefresh() {
  if (refreshTimer) {
    clearInterval(refreshTimer)
    refreshTimer = null
  }
}

function toggleAutoRefresh() {
  autoRefreshEnabled.value = !autoRefreshEnabled.value
  if (autoRefreshEnabled.value) {
    startAutoRefresh()
  }
  else {
    stopAutoRefresh()
  }
}

function toggleStation(code: string) {
  if (expandedStations.value.has(code)) {
    expandedStations.value.delete(code)
  }
  else {
    expandedStations.value.add(code)
  }
}

// 辅助函数
function statusColor(status: string): string {
  switch (status) {
    case 'Running': case 'Normal': return '#67C23A'
    case 'Stopped': case 'Fault': return '#F56C6C'
    case 'Silent': return '#909399'
    default: return '#E6A23C'
  }
}

function statusLabel(status: string): string {
  switch (status) {
    case 'Running': return t('signal_live.statusRunning')
    case 'Normal': return t('common.normal')
    case 'Stopped': return t('signal_live.statusStopped')
    case 'Fault': return t('signal_live.statusFault')
    case 'Silent': return t('signal_live.statusSilent')
    default: return status || t('signal_live.statusUnknown')
  }
}

function formatValue(val: any): string {
  if (val === null || val === undefined || val === '') return t('signal_live.emptyValue')
  const s = String(val)
  if (s === 'True' || s === 'true' || s === '1') return 'True ✅'
  if (s === 'False' || s === 'false' || s === '0') return 'False ❌'
  return s
}

function valueClass(val: any): string {
  const s = String(val)
  if (s === 'True' || s === 'true' || s === '1') return 'val-true'
  if (s === 'False' || s === 'false' || s === '0') return 'val-false'
  return 'val-other'
}

function formatTime(t: string | null): string {
  if (!t) return '-'
  try {
    const d = new Date(t)
    return d.toLocaleTimeString('zh-CN', { hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit' })
      + `.${String(d.getMilliseconds()).padStart(3, '0')}`
  }
  catch {
    return t
  }
}

function timeAgo(t: string | null): string {
  if (!t) return ''
  try {
    const diff = Date.now() - new Date(t).getTime()
    if (diff < 0) return t('signal_live.justNow')
    if (diff < 60000) return t('signal_live.secondsAgo', { n: Math.floor(diff / 1000) })
    if (diff < 3600000) return t('signal_live.minutesAgo', { n: Math.floor(diff / 60000) })
    return t('signal_live.hoursAgo', { n: Math.floor(diff / 3600000) })
  }
  catch {
    return ''
  }
}

const totalChanges = computed(() => stations.value.reduce((acc, s) => acc + (s.recentChanges?.length || 0), 0))
const activeStations = computed(() => stations.value.filter(s => s.recentChanges?.length > 0).length)

onMounted(() => {
  loadProjects().then(() => {
    if (selectedProjectId.value)
      fetchData()
  })
  startAutoRefresh()
})
onBeforeUnmount(() => {
  stopAutoRefresh()
})
</script>

<template>
  <div class="signal-live-page">
    <!-- 顶部 -->
    <div class="page-header">
      <div class="header-left">
        <h2 class="page-title">
          📡 {{ $t('signal_live.title') }}
        </h2>
        <span class="header-meta">
          {{ $t('signal_live.headerMeta', { stations: stations.length, active: activeStations, changes: totalChanges }) }}
        </span>
      </div>
      <div class="header-actions">
        <!-- 项目选择器 -->
        <el-select
          v-model="selectedProjectId"
          :placeholder="$t('signal_live.selectProject')"
          style="width: 220px"
          filterable
          @change="onProjectChange"
        >
          <el-option
            v-for="p in projectList"
            :key="p.id"
            :label="p.projectName"
            :value="String(p.id)"
          />
        </el-select>
        <span v-if="lastRefreshTime" class="refresh-time">
          {{ $t('signal_live.updatedAt', { time: lastRefreshTime }) }}
        </span>
        <el-button-group>
          <el-button :type="autoRefreshEnabled ? 'success' : 'info'" @click="toggleAutoRefresh">
            {{ autoRefreshEnabled ? $t('signal_live.autoRefresh', { seconds: autoRefreshSeconds }) : $t('signal_live.paused') }}
          </el-button>
          <el-button type="primary" :loading="loading" :disabled="!selectedProjectId" @click="fetchData">
            🔄 {{ $t('signal_live.refresh') }}
          </el-button>
        </el-button-group>
      </div>
    </div>

    <!-- 工位卡片网格 -->
    <div class="station-grid">
      <div
        v-for="station in stations"
        :key="station.stationCode"
        class="station-card"
        :class="{
          'station-card--active': station.recentChanges?.length > 0,
          'station-card--silent': station.isSilent,
          'station-card--expanded': expandedStations.has(station.stationCode),
        }"
      >
        <!-- 卡片头 -->
        <div class="card-header" @click="toggleStation(station.stationCode)">
          <div class="card-title-row">
            <div class="station-status-dot" :style="{ background: statusColor(station.status) }" />
            <span class="station-code">{{ station.stationCode }}</span>
            <span class="station-status-text" :style="{ color: statusColor(station.status) }">
              {{ statusLabel(station.status) }}
            </span>
            <el-tag
              :type="station.enabled ? (station.recentChanges?.length ? 'warning' : 'success') : 'info'"
              size="small"
              effect="plain"
            >
              {{ station.enabled ? (station.recentChanges?.length ? $t('signal_live.changesCount', { count: station.recentChanges.length }) : $t('signal_live.noChanges')) : $t('signal_live.disabled') }}
            </el-tag>
          </div>
          <div class="card-meta">
            <span v-if="station.lastSampleTime" class="meta-item" :title="station.lastSampleTime">
              🕐 {{ timeAgo(station.lastSampleTime) }}
            </span>
            <span class="meta-item">📊 {{ $t('signal_live.signalCounts', { core: station.coreSignalCount, context: station.contextSignalCount }) }}</span>
            <span class="expand-icon">{{ expandedStations.has(station.stationCode) ? '▼' : '▶' }}</span>
          </div>
        </div>

        <!-- 信号变化迷你时间线 (始终显示最近5条) -->
        <div v-if="station.recentChanges?.length" class="mini-timeline">
          <div
            v-for="(change, idx) in station.recentChanges.slice(0, 5)"
            :key="change.changeId || idx"
            class="mini-change"
          >
            <span class="change-time">{{ formatTime(change.changedAt) }}</span>
            <span class="change-var" :title="change.signalKey">{{ change.variableName || change.signalKey }}</span>
            <span class="change-arrow">
              <span :class="valueClass(change.oldValue)">{{ formatValue(change.oldValue) }}</span>
              →
              <span :class="valueClass(change.newValue)">{{ formatValue(change.newValue) }}</span>
            </span>
          </div>
        </div>
        <div v-else class="no-changes">
          {{ $t('signal_live.noSignalChanges') }}
        </div>

        <!-- 展开详情：完整变化列表 -->
        <transition name="expand">
          <div v-if="expandedStations.has(station.stationCode) && station.recentChanges?.length > 5" class="full-timeline">
            <div class="timeline-divider">
              ── {{ $t('signal_live.allChanges', { count: station.recentChanges.length }) }} ──
            </div>
            <div
              v-for="(change, idx) in station.recentChanges"
              :key="`full-${change.changeId || idx}`"
              class="full-change-row"
            >
              <span class="change-time-full">{{ formatTime(change.changedAt) }}</span>
              <span class="change-var-full" :title="change.signalKey">
                {{ change.variableName || change.signalKey }}
              </span>
              <span v-if="change.signalType" class="change-type">{{ change.signalType }}</span>
              <span class="change-values">
                <span :class="valueClass(change.oldValue)">{{ formatValue(change.oldValue) }}</span>
                <span class="arrow">→</span>
                <span :class="valueClass(change.newValue)">{{ formatValue(change.newValue) }}</span>
              </span>
              <span v-if="change.groupName" class="change-group">{{ change.groupName }}</span>
            </div>
          </div>
        </transition>
      </div>
    </div>

    <!-- 空状态 -->
    <div v-if="!loading && !selectedProjectId" class="empty-state">
      <p>⬆️ {{ $t('signal_live.selectProjectFirst') }}</p>
    </div>
    <div v-else-if="!loading && stations.length === 0 && selectedProjectId" class="empty-state">
      <p>📡 {{ $t('signal_live.noStationData') }}</p>
      <p>{{ $t('signal_live.noStationDataHint') }}</p>
    </div>
  </div>
</template>

<style scoped>
.signal-live-page {
  padding: 20px;
  max-width: 1600px;
  margin: 0 auto;
  background: #f5f7fa;
  min-height: 100vh;
}

.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 20px;
  flex-wrap: wrap;
  gap: 12px;
}

.header-left {
  display: flex;
  align-items: baseline;
  gap: 16px;
}

.page-title {
  margin: 0;
  font-size: 22px;
}

.header-meta {
  color: #909399;
  font-size: 14px;
}

.header-actions {
  display: flex;
  align-items: center;
  gap: 12px;
}

.refresh-time {
  color: #909399;
  font-size: 13px;
}

/* ── 工位卡片网格 ── */
.station-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(420px, 1fr));
  gap: 16px;
}

.station-card {
  background: #fff;
  border-radius: 10px;
  border: 2px solid #e4e7ed;
  overflow: hidden;
  transition: all 0.3s ease;
}

.station-card--active {
  border-color: #e6a23c;
}

.station-card--silent {
  opacity: 0.65;
}

.station-card--expanded {
  border-color: #409eff;
}

.station-card:hover {
  box-shadow: 0 4px 16px rgba(0, 0, 0, 0.08);
}

/* ── 卡片头部 ── */
.card-header {
  padding: 14px 16px 10px;
  cursor: pointer;
  user-select: none;
}

.card-title-row {
  display: flex;
  align-items: center;
  gap: 10px;
}

.station-status-dot {
  width: 12px;
  height: 12px;
  border-radius: 50%;
  flex-shrink: 0;
}

.station-code {
  font-size: 17px;
  font-weight: 700;
  color: #303133;
}

.card-meta {
  display: flex;
  align-items: center;
  gap: 14px;
  margin-top: 6px;
  font-size: 12px;
  color: #909399;
}

.expand-icon {
  margin-left: auto;
  font-size: 11px;
  color: #c0c4cc;
}

/* ── 迷你时间线 ── */
.mini-timeline {
  padding: 0 16px 12px;
}

.mini-change {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 0;
  font-size: 13px;
  border-bottom: 1px solid #f2f3f5;
}

.mini-change:last-child {
  border-bottom: none;
}

.change-time {
  color: #909399;
  font-size: 12px;
  font-family: 'Cascadia Code', 'Consolas', monospace;
  min-width: 90px;
  flex-shrink: 0;
}

.change-var {
  color: #606266;
  font-weight: 500;
  max-width: 160px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  flex-shrink: 0;
}

.change-arrow {
  display: flex;
  align-items: center;
  gap: 4px;
  font-size: 12px;
  margin-left: auto;
}

.no-changes {
  padding: 12px 16px;
  color: #c0c4cc;
  font-size: 13px;
  text-align: center;
}

/* ── 完整时间线 ── */
.full-timeline {
  border-top: 1px solid #ebeef5;
  padding: 8px 16px 12px;
  max-height: 400px;
  overflow-y: auto;
  background: #fafbfc;
}

.timeline-divider {
  text-align: center;
  font-size: 12px;
  color: #c0c4cc;
  padding: 4px 0 8px;
}

.full-change-row {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 5px 0;
  font-size: 13px;
  border-bottom: 1px solid #f2f3f5;
  flex-wrap: wrap;
}

.full-change-row:last-child {
  border-bottom: none;
}

.change-time-full {
  color: #909399;
  font-size: 12px;
  font-family: 'Cascadia Code', 'Consolas', monospace;
  min-width: 90px;
  flex-shrink: 0;
}

.change-var-full {
  color: #303133;
  font-weight: 600;
  max-width: 180px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.change-type {
  font-size: 11px;
  color: #909399;
  background: #f0f2f5;
  padding: 1px 6px;
  border-radius: 3px;
}

.change-values {
  display: flex;
  align-items: center;
  gap: 4px;
  font-size: 12px;
  margin-left: auto;
}

.arrow {
  color: #c0c4cc;
}

.change-group {
  font-size: 11px;
  color: #a8abb2;
}

/* ── 值颜色 ── */
.val-true {
  color: #67c23a;
  font-weight: 600;
}

.val-false {
  color: #f56c6c;
  font-weight: 600;
}

.val-other {
  color: #409eff;
}

/* ── 展开动画 ── */
.expand-enter-active,
.expand-leave-active {
  transition: all 0.3s ease;
  overflow: hidden;
}
.expand-enter-from,
.expand-leave-to {
  max-height: 0;
  opacity: 0;
  padding-top: 0;
  padding-bottom: 0;
}

/* ── 空状态 ── */
.empty-state {
  text-align: center;
  padding: 80px 20px;
  color: #909399;
}
</style>
