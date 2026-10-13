<template>
  <div class="graph-step-tag-panel">
    <!-- 顶部：统计条 + 操作 -->
    <div v-if="summary" class="summary-bar">
      <div class="stat-item">
        <span class="stat-label">{{ $t('aiPlcProject_form.graphStepDb') }}</span>
        <span class="stat-value">{{ summary.dbCount }}</span>
      </div>
      <div class="stat-item">
        <span class="stat-label">{{ $t('aiPlcProject_form.totalSymbols') }}</span>
        <span class="stat-value">{{ summary.totalSymbols }}</span>
      </div>
      <div class="stat-item ok">
        <span class="stat-label">{{ $t('aiPlcProject_form.verified') }}</span>
        <span class="stat-value">{{ summary.verifiedCount }}</span>
      </div>
      <div class="stat-item info">
        <span class="stat-label">{{ $t('aiPlcProject_form.registered') }}</span>
        <span class="stat-value">{{ summary.registeredCount }}</span>
      </div>
      <div class="stat-item warn" v-if="summary.pendingCount > 0">
        <span class="stat-label">{{ $t('aiPlcProject_form.pending') }}</span>
        <span class="stat-value">{{ summary.pendingCount }}</span>
      </div>
      <div class="stat-item err" v-if="summary.regFailedCount > 0">
        <span class="stat-label">{{ $t('aiPlcProject_form.regFailed') }}</span>
        <span class="stat-value">{{ summary.regFailedCount }}</span>
      </div>
      <div class="stat-item err" v-if="summary.verifyFailedCount > 0">
        <span class="stat-label">{{ $t('aiPlcProject_form.verifyFailed') }}</span>
        <span class="stat-value">{{ summary.verifyFailedCount }}</span>
      </div>
      <div class="actions">
        <el-button size="small" :loading="loading" @click="loadSummary">{{ $t('aiPlcProject_form.refresh') }}</el-button>
        <el-button size="small" type="primary" :loading="retrying" @click="retryRegistration">
          {{ $t('aiPlcProject_form.retryRegister') }}
        </el-button>
      </div>
    </div>

    <!-- 按 DB 分组卡片 -->
    <div v-if="summary && summary.databases.length > 0" class="db-list">
      <el-card
        v-for="db in summary.databases"
        :key="db.dbName"
        class="db-card"
        shadow="never"
      >
        <div class="db-header" @click="toggleDb(db.dbName)">
          <el-icon class="expand-icon" :class="{ expanded: expandedDb === db.dbName }">
            <CaretRight />
          </el-icon>
          <span class="db-name">{{ db.dbName }}</span>
          <el-tag size="small" type="info" effect="plain">{{ db.fbName }}</el-tag>
          <el-tag v-if="db.stationCode" size="small" type="success" effect="plain">{{ db.stationCode }}{{ db.stationName ? ' ' + db.stationName : '' }}</el-tag>
          <span class="db-meta">{{ $t('aiPlcProject_form.stepsSymbols', { steps: db.stepCount, symbols: db.symbolCount }) }}</span>
          <el-tag
            v-if="db.regFailedCount > 0 || db.verifyFailedCount > 0"
            size="small"
            type="danger"
          >
            {{ $t('aiPlcProject_form.failedCount', { count: db.regFailedCount + db.verifyFailedCount }) }}
          </el-tag>
          <el-tag v-else-if="db.verifiedCount === db.symbolCount" size="small" type="success">
            {{ $t('aiPlcProject_form.allVerified') }}
          </el-tag>
          <el-tag v-else size="small" type="warning">{{ $t('aiPlcProject_form.verifiedRatio', { verified: db.verifiedCount, total: db.symbolCount }) }}</el-tag>
        </div>
        <div v-if="db.failureSample" class="failure-sample" :title="db.failureSample">
          {{ $t('aiPlcProject_form.failureReason') }}: {{ db.failureSample }}
        </div>

        <!-- 展开明细 -->
        <el-table
          v-if="expandedDb === db.dbName"
          :data="rowsCache[db.dbName] || []"
          size="small"
          border
          v-loading="rowsLoading === db.dbName"
          max-height="420"
        >
          <el-table-column prop="symbol" :label="$t('aiPlcProject_form.symbol')" min-width="320" show-overflow-tooltip />
          <el-table-column prop="dbAddress" :label="$t('aiPlcProject_form.dbAddress')" min-width="280" show-overflow-tooltip />
          <el-table-column :label="$t('aiPlcProject_form.category')" width="100">
            <template #default="{ row }">
              <el-tag size="small" :type="row.tagCategory === 'EXEC_BIT' ? 'primary' : 'info'">
                {{ row.tagCategory === 'EXEC_BIT' ? $t('aiPlcProject_form.execBit') : $t('aiPlcProject_form.stepNoCheck') }}
              </el-tag>
            </template>
          </el-table-column>
          <el-table-column prop="stepNumber" :label="$t('aiPlcProject_form.stepNumber')" width="80" align="center" />
          <el-table-column prop="bitIndex" :label="$t('aiPlcProject_form.arrayIndex')" width="80" align="center" />
          <el-table-column :label="$t('aiPlcProject_form.status')" width="110">
            <template #default="{ row }">
              <el-tag size="small" :type="statusType(row.regStatus)">
                {{ statusText(row.regStatus) }}
              </el-tag>
            </template>
          </el-table-column>
          <el-table-column prop="verifyDetail" :label="$t('common.detail')" min-width="200" show-overflow-tooltip />
        </el-table>
      </el-card>
    </div>

    <!-- 空态 -->
    <el-empty
      v-if="summary && summary.totalSymbols === 0"
      :description="$t('aiPlcProject_form.emptyGraphHint')"
    />
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import { CaretRight } from '@element-plus/icons-vue'
import graphStepTagApi from '@/api/modules/PLC/graphStepTag'
import { useI18n } from 'vue-i18n'
import messages from './lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const props = defineProps<{ projectId: number | string }>()

const loading = ref(false)
const retrying = ref(false)
const summary = ref<any>(null)
const expandedDb = ref('')
const rowsLoading = ref('')
const rowsCache = ref<Record<string, any[]>>({})

const statusType = (s: string) =>
  s === 'Verified' ? 'success'
  : s === 'Registered' ? 'primary'
  : s === 'Pending' ? 'info'
  : 'danger'

const statusText = (s: string) =>
  s === 'Verified' ? t('aiPlcProject_form.verified')
  : s === 'Registered' ? t('aiPlcProject_form.registered')
  : s === 'Pending' ? t('aiPlcProject_form.pending')
  : s === 'RegFailed' ? t('aiPlcProject_form.regFailed')
  : s === 'VerifyFailed' ? t('aiPlcProject_form.verifyFailed')
  : s

const loadSummary = async () => {
  loading.value = true
  try {
    const res: any = await graphStepTagApi.summary(props.projectId)
    if (res?.isSuccess) summary.value = res.backResult
    else ElMessage.error(res?.errorMessage || t('aiPlcProject_form.queryFailed'))
  } catch (e: any) {
    ElMessage.error(e?.message || t('aiPlcProject_form.queryError'))
  } finally {
    loading.value = false
  }
}

const toggleDb = async (dbName: string) => {
  if (expandedDb.value === dbName) {
    expandedDb.value = ''
    return
  }
  expandedDb.value = dbName
  if (!rowsCache.value[dbName]) {
    rowsLoading.value = dbName
    try {
      const res: any = await graphStepTagApi.rowsByDb(props.projectId, dbName)
      if (res?.isSuccess) rowsCache.value[dbName] = res.backResult || []
      else ElMessage.error(res?.errorMessage || t('aiPlcProject_form.rowsQueryFailed'))
    } finally {
      rowsLoading.value = ''
    }
  }
}

const retryRegistration = async () => {
  retrying.value = true
  try {
    const res: any = await graphStepTagApi.retry(props.projectId)
    if (res?.isSuccess) {
      ElMessage.success(res.backResult || t('aiPlcProject_form.enqueued'))
      setTimeout(loadSummary, 4000)
    } else {
      ElMessage.error(res?.errorMessage || t('aiPlcProject_form.enqueueFailed'))
    }
  } catch (e: any) {
    ElMessage.error(e?.message || t('aiPlcProject_form.enqueueError'))
  } finally {
    retrying.value = false
  }
}

onMounted(loadSummary)
defineExpose({ loadSummary })
</script>

<style scoped lang="scss">
.graph-step-tag-panel {
  .summary-bar {
    display: flex;
    align-items: center;
    gap: 24px;
    padding: 12px 16px;
    background: var(--el-fill-color-light);
    border-radius: 6px;
    margin-bottom: 12px;
    flex-wrap: wrap;

    .stat-item {
      display: flex;
      flex-direction: column;
      gap: 2px;
      .stat-label { font-size: 12px; color: var(--el-text-color-secondary); }
      .stat-value { font-size: 20px; font-weight: 600; }
      &.ok .stat-value { color: var(--el-color-success); }
      &.info .stat-value { color: var(--el-color-primary); }
      &.warn .stat-value { color: var(--el-color-warning); }
      &.err .stat-value { color: var(--el-color-danger); }
    }
    .actions { margin-left: auto; display: flex; gap: 8px; }
  }

  .db-list { display: flex; flex-direction: column; gap: 8px; }
  .db-card :deep(.el-card__body) { padding: 10px 14px; }

  .db-header {
    display: flex;
    align-items: center;
    gap: 10px;
    cursor: pointer;
    user-select: none;

    .expand-icon { transition: transform 0.2s; &.expanded { transform: rotate(90deg); } }
    .db-name { font-weight: 600; font-family: monospace; }
    .db-meta { color: var(--el-text-color-secondary); font-size: 12px; }
  }

  .failure-sample {
    margin-top: 6px;
    font-size: 12px;
    color: var(--el-color-danger);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .el-table { margin-top: 10px; }
}
</style>
