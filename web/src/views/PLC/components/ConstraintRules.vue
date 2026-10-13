<script setup lang="ts">
import { ref, watch, computed } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import ConstraintAPI from '@/api/modules/PLC/constraint'
import { useI18n } from 'vue-i18n'
import messages from './constraintRules.lang'

const props = defineProps<{
  projectId: string | number
}>()

const { t, mergeLocaleMessage } = useI18n()

// 遍历语言包并合并到当前实例
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const loading = ref(false)
const rules = ref<any[]>([])
const pagination = ref({
  page: 1,
  limit: 10,
  total: 0,
})

// 筛选
const filterForm = ref({
  ruleType: '',
  equipmentId: '',
})

// 约束类型选项
const ruleTypeOptions = computed(() => [
  { label: t('constraintRules.typeAll'), value: '' },
  { label: t('constraintRules.typeMutex'), value: 'Mutex' },
  { label: t('constraintRules.typeExactlyOne'), value: 'ExactlyOne' },
  { label: t('constraintRules.typeAllRequired'), value: 'AllRequired' },
  { label: t('constraintRules.typePairing'), value: 'Pairing' },
  { label: t('constraintRules.typeSafety'), value: 'Safety' },
  { label: t('constraintRules.typeSequence'), value: 'Sequence' },
  { label: t('constraintRules.typeCustom'), value: 'Custom' },
])

// 优先级颜色映射
const priorityType = (priority: string) => {
  switch (priority) {
    case 'Critical': return 'danger'
    case 'Warning': return 'warning'
    case 'Info': return 'info'
    default: return 'info'
  }
}

// 约束类型颜色映射
const ruleTypeTagType = (type: string) => {
  switch (type) {
    case 'Mutex': return 'danger'
    case 'Safety': return 'danger'
    case 'ExactlyOne': return 'warning'
    case 'Pairing': return 'primary'
    case 'AllRequired': return 'success'
    case 'Sequence': return ''
    case 'Custom': return 'info'
    default: return 'info'
  }
}

// 信号角色标签
const roleLabel = (role: string) => {
  switch (role) {
    case 'Source': return t('constraintRules.roleSource')
    case 'Target': return t('constraintRules.roleTarget')
    case 'Member': return t('constraintRules.roleMember')
    default: return role
  }
}

const roleType = (role: string) => {
  switch (role) {
    case 'Source': return 'primary'
    case 'Target': return 'success'
    case 'Member': return 'info'
    default: return 'info'
  }
}

// 统计信息
const summary = ref<any>(null)

const fetchRules = async () => {
  if (!props.projectId) return
  loading.value = true
  try {
    const res = await ConstraintAPI.list({
      projectId: props.projectId,
      page: pagination.value.page,
      limit: pagination.value.limit,
      ruleType: filterForm.value.ruleType || undefined,
      equipmentId: filterForm.value.equipmentId || undefined,
    }) as any
    if (res.data) {
      rules.value = res.data
      pagination.value.total = res.total || 0
    }
  }
  catch (error: any) {
    ElMessage.error(error.message || t('constraintRules.fetchFailed'))
  }
  finally {
    loading.value = false
  }
}

const fetchSummary = async () => {
  if (!props.projectId) return
  try {
    summary.value = await ConstraintAPI.summary(props.projectId) as any
  }
  catch {}
}

const handlePageChange = (val: number) => {
  pagination.value.page = val
  fetchRules()
}

const handleSearch = () => {
  pagination.value.page = 1
  fetchRules()
}

const handleReset = () => {
  filterForm.value = { ruleType: '', equipmentId: '' }
  pagination.value.page = 1
  fetchRules()
}

const handleDelete = (row: any) => {
  ElMessageBox.confirm(
    t('constraintRules.deleteConfirm', { name: row.ruleName }),
    t('common.confirmDelete'),
    { confirmButtonText: t('constraintRules.ok'), cancelButtonText: t('constraintRules.cancel'), type: 'warning' },
  ).then(async () => {
    try {
      const res = await ConstraintAPI.delete(row.id) as any
      if (res.isSuccess) {
        ElMessage.success(t('common.deleteSuccess'))
        fetchRules()
        fetchSummary()
      }
      else {
        ElMessage.error(res.errorMessage || t('constraintRules.deleteFailed'))
      }
    }
    catch (error: any) {
      ElMessage.error(error.message || t('constraintRules.deleteFailed'))
    }
  }).catch(() => {})
}

// 公开刷新方法，供父组件调用
const refresh = () => {
  fetchRules()
  fetchSummary()
}

defineExpose({ refresh })

watch(() => props.projectId, (val) => {
  if (val) {
    fetchRules()
    fetchSummary()
  }
}, { immediate: true })
</script>

<template>
  <div>
    <!-- 统计概览 -->
    <div v-if="summary" class="summary-bar">
      <div class="summary-item">
        <span class="summary-label">{{ $t('constraintRules.totalRules') }}</span>
        <span class="summary-value">{{ summary.totalRules }}</span>
      </div>
      <el-divider direction="vertical" />
      <template v-for="(count, type) in (summary.byType || {})" :key="type">
        <div class="summary-item">
          <el-tag :type="ruleTypeTagType(type as string)" size="small">{{ type }}</el-tag>
          <span class="summary-count">{{ count }}</span>
        </div>
      </template>
    </div>

    <!-- 筛选栏 -->
    <div class="search-bar">
      <el-form :inline="true" @submit.prevent="handleSearch">
        <el-form-item :label="$t('constraintRules.ruleType')">
          <el-select v-model="filterForm.ruleType" :placeholder="$t('constraintRules.typeAll')" clearable style="width: 180px">
            <el-option
              v-for="opt in ruleTypeOptions"
              :key="opt.value"
              :label="opt.label"
              :value="opt.value"
            />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('constraintRules.equipmentId')">
          <el-input
            v-model="filterForm.equipmentId"
            :placeholder="$t('constraintRules.equipmentIdPlaceholder')"
            clearable
            style="width: 160px"
            @keyup.enter="handleSearch"
          />
        </el-form-item>
        <el-form-item>
          <el-button type="primary" @click="handleSearch">
            <i-ep-search class="mr-1" /> {{ $t('common.search') }}
          </el-button>
          <el-button @click="handleReset">
            <i-ep-refresh class="mr-1" /> {{ $t('common.reset') }}
          </el-button>
        </el-form-item>
      </el-form>
    </div>

    <!-- 规则列表 -->
    <el-table v-loading="loading" :data="rules" style="width: 100%" stripe row-key="id">
      <el-table-column prop="ruleName" :label="$t('constraintRules.ruleName')" min-width="160" show-overflow-tooltip />
      <el-table-column prop="ruleType" :label="$t('constraintRules.ruleType')" width="130" align="center">
        <template #default="{ row }">
          <el-tag :type="ruleTypeTagType(row.ruleType)" size="small">
            {{ row.ruleType }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column prop="operator" :label="$t('constraintRules.operator')" width="140" align="center">
        <template #default="{ row }">
          <el-text type="info" size="small">{{ row.operator }}</el-text>
        </template>
      </el-table-column>
      <el-table-column prop="priority" :label="$t('constraintRules.priority')" width="90" align="center">
        <template #default="{ row }">
          <el-tag :type="priorityType(row.priority)" size="small">
            {{ row.priority }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column :label="$t('constraintRules.signalList')" min-width="280">
        <template #default="{ row }">
          <div class="signal-list">
            <el-tag
              v-for="signal in (row.signals || [])"
              :key="signal.signalName"
              :type="roleType(signal.signalRole)"
              size="small"
              class="signal-tag"
              :title="`${signal.signalName} (${signal.signalAddress || '-'}) [${roleLabel(signal.signalRole)}]`"
            >
              {{ signal.signalName }}
              <span v-if="signal.signalRole !== 'Member'" class="role-badge">{{ roleLabel(signal.signalRole) }}</span>
            </el-tag>
          </div>
        </template>
      </el-table-column>
      <el-table-column prop="confidence" :label="$t('constraintRules.confidence')" width="80" align="center">
        <template #default="{ row }">
          <span v-if="row.confidence != null">{{ (row.confidence * 100).toFixed(0) }}%</span>
          <span v-else>-</span>
        </template>
      </el-table-column>
      <el-table-column prop="equipmentId" :label="$t('constraintRules.equipment')" width="120" show-overflow-tooltip />
      <el-table-column prop="description" :label="$t('constraintRules.description')" min-width="180" show-overflow-tooltip />
      <el-table-column :label="$t('common.operate')" width="80" fixed="right" align="center">
        <template #default="{ row }">
          <el-button link type="danger" size="small" @click="handleDelete(row)">
            <i-ep-delete class="mr-1" /> {{ $t('common.delete') }}
          </el-button>
        </template>
      </el-table-column>
    </el-table>

    <div class="mt-4 flex justify-end">
      <el-pagination
        v-model:current-page="pagination.page"
        v-model:page-size="pagination.limit"
        :total="pagination.total"
        layout="total, prev, pager, next"
        @current-change="handlePageChange"
      />
    </div>
  </div>
</template>

<style scoped>
.summary-bar {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 12px 16px;
  background: var(--el-fill-color-light);
  border-radius: 8px;
  margin-bottom: 16px;
  flex-wrap: wrap;
}

.summary-item {
  display: flex;
  align-items: center;
  gap: 6px;
}

.summary-label {
  color: var(--el-text-color-secondary);
  font-size: 13px;
}

.summary-value {
  font-weight: 600;
  font-size: 18px;
  color: var(--el-color-primary);
}

.summary-count {
  font-weight: 500;
  font-size: 14px;
  color: var(--el-text-color-primary);
}

.search-bar {
  margin-bottom: 16px;
  padding: 16px;
  background: var(--el-fill-color-light);
  border-radius: 8px;
}

.search-bar :deep(.el-form-item) {
  margin-bottom: 0;
}

.signal-list {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.signal-tag {
  max-width: 200px;
  overflow: hidden;
  text-overflow: ellipsis;
}

.role-badge {
  margin-left: 4px;
  font-size: 10px;
  opacity: 0.7;
}
</style>
