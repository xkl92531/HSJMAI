<route lang="yaml">
meta:
  title: 工作流管理
  icon: i-carbon:flow
</route>

<script setup lang="ts">
import { ElMessage, ElMessageBox } from 'element-plus'
import { useRouter } from 'vue-router'
import apiWorkflow from '@/api/modules/agent/workflow'
import { useI18n } from 'vue-i18n'
import messages from './components/lang'

const { t, mergeLocaleMessage } = useI18n()
// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

defineOptions({
  name: 'WorkflowList',
})

const router = useRouter()
const { pagination, getParams, onSizeChange, onCurrentChange, onSortChange } = usePagination()

// 搜索
const searchDefault = {
  status: '' as number | string,
  triggerType: '',
}
const search = ref({ ...searchDefault })

function searchReset() {
  Object.assign(search.value, searchDefault)
}

// 批量操作
const batch = ref({
  enable: true,
  selectionDataList: [] as any[],
})

const loading = ref(false)
const dataList = ref<any[]>([])

onMounted(() => {
  getDataList()
})

function getDataList() {
  loading.value = true
  const params = {
    ...getParams(),
    ...(search.value.status !== '' && { status: search.value.status }),
    ...(search.value.triggerType && { triggerType: search.value.triggerType }),
  }
  apiWorkflow.list(params).then((res: any) => {
    loading.value = false
    dataList.value = res.data || []
    pagination.value.total = res.total || 0
  }).catch(() => {
    loading.value = false
  })
}

function sizeChange(size: number) {
  onSizeChange(size).then(() => getDataList())
}

function currentChange(page = 1) {
  onCurrentChange(page).then(() => getDataList())
}

function sortChange({ prop, order }: { prop: string, order: string }) {
  onSortChange(prop, order).then(() => getDataList())
}

function onViewDetail(row: any) {
  router.push(`/agent/workflow/detail?id=${row.id}`)
}

function onDelete(row: any) {
  ElMessageBox.confirm(t('agentWorkflow.deleteWorkflowConfirm', { name: row.name || row.id }), t('agentWorkflow.confirmInfoTitle')).then(() => {
    apiWorkflow.delete(row.id).then(() => {
      getDataList()
      ElMessage.success(t('common.deleteSuccess'))
    })
  }).catch(() => {})
}

function onBatchDelete() {
  if (batch.value.selectionDataList.length === 0) {
    ElMessage.warning(t('agentWorkflow.selectAtLeastOne'))
    return
  }
  const ids = batch.value.selectionDataList.map((item: any) => item.id)
  ElMessageBox.confirm(t('agentWorkflow.batchDeleteConfirm', { n: ids.length }), t('agentWorkflow.confirmInfoTitle')).then(() => {
    apiWorkflow.batchDelete(ids).then(() => {
      getDataList()
      ElMessage.success(t('common.deleteSuccess'))
    })
  }).catch(() => {})
}

// 状态选项
const statusOptions = computed(() => [
  { label: t('agentWorkflow.statusPending'), value: 0 },
  { label: t('agentWorkflow.statusPlanning'), value: 1 },
  { label: t('agentWorkflow.statusExecuting'), value: 2 },
  { label: t('agentWorkflow.statusCompleted'), value: 3 },
  { label: t('agentWorkflow.statusFailed'), value: 4 },
  { label: t('agentWorkflow.statusCancelled'), value: 5 },
])

// 触发类型选项
const triggerTypeOptions = [
  { label: 'User', value: 'User' },
  { label: 'System', value: 'System' },
  { label: 'Schedule', value: 'Schedule' },
  { label: 'SubWorkflow', value: 'SubWorkflow' },
]

function getStatusLabel(status: number | null | undefined) {
  const option = statusOptions.value.find(item => item.value === status)
  return option ? option.label : t('agentWorkflow.statusUnknown')
}

function getStatusColor(status: number | null | undefined) {
  const colors: Record<number, string> = {
    0: 'info',
    1: 'warning',
    2: 'primary',
    3: 'success',
    4: 'danger',
    5: 'info',
  }
  return colors[status ?? -1] || 'info'
}

function formatDateTime(dateStr: string | null | undefined) {
  if (!dateStr) return '-'
  return new Date(dateStr).toLocaleString('zh-CN')
}

function formatDuration(seconds: number | null | undefined) {
  if (!seconds) return '-'
  if (seconds < 60) return t('agentWorkflow.durationSeconds', { n: seconds })
  if (seconds < 3600) return t('agentWorkflow.durationMinutes', { m: Math.floor(seconds / 60), s: seconds % 60 })
  return t('agentWorkflow.durationHours', { h: Math.floor(seconds / 3600), m: Math.floor((seconds % 3600) / 60) })
}
</script>

<template>
  <div class="absolute-container">
    <FaPageMain class="flex-1 overflow-auto" :main-class="{ 'flex-1 flex flex-col overflow-auto': true }">
      <FaSearchBar :show-toggle="false">
        <template #default>
          <ElForm :model="search" size="default" label-width="80px" class="search-form">
            <ElRow :gutter="20">
              <ElCol :span="6">
                <ElFormItem :label="$t('agentWorkflow.statusLabel')">
                  <ElSelect v-model="search.status" :placeholder="$t('agentWorkflow.selectStatusPlaceholder')" clearable @change="currentChange()">
                    <ElOption v-for="item in statusOptions" :key="item.value" :label="item.label" :value="item.value" />
                  </ElSelect>
                </ElFormItem>
              </ElCol>
              <ElCol :span="6">
                <ElFormItem :label="$t('agentWorkflow.triggerTypeLabel')">
                  <ElSelect v-model="search.triggerType" :placeholder="$t('common.pleaseSelect')" clearable @change="currentChange()">
                    <ElOption v-for="item in triggerTypeOptions" :key="item.value" :label="item.label" :value="item.value" />
                  </ElSelect>
                </ElFormItem>
              </ElCol>
              <ElCol :span="6">
                <ElButton type="primary" @click="currentChange()">
                  <template #icon>
                    <FaIcon name="i-ep:search" />
                  </template>
                  {{ $t('common.search') }}
                </ElButton>
                <ElButton @click="searchReset(); currentChange()">
                  {{ $t('common.reset') }}
                </ElButton>
              </ElCol>
            </ElRow>
          </ElForm>
        </template>
      </FaSearchBar>
      <ElDivider border-style="dashed" />
      <ElSpace wrap>
        <ElButton size="default" type="danger" :disabled="!batch.selectionDataList.length" @click="onBatchDelete">
          <template #icon>
            <FaIcon name="i-ep:delete" />
          </template>
          {{ $t('common.deleteBatch') }}
        </ElButton>
        <ElButton size="default" @click="getDataList">
          <template #icon>
            <FaIcon name="i-ep:refresh" />
          </template>
          {{ $t('agentWorkflow.refresh') }}
        </ElButton>
      </ElSpace>
      <ElTable v-loading="loading" class="my-4" :data="dataList" stripe highlight-current-row border height="100%" @sort-change="sortChange" @selection-change="batch.selectionDataList = $event">
        <ElTableColumn v-if="batch.enable" type="selection" align="center" fixed width="50" />
        <ElTableColumn prop="id" label="ID" width="180" show-overflow-tooltip />
        <ElTableColumn prop="name" :label="$t('agentWorkflow.nameCol')" min-width="150" show-overflow-tooltip>
          <template #default="{ row }">
            {{ row.name || $t('agentWorkflow.unnamed') }}
          </template>
        </ElTableColumn>
        <ElTableColumn prop="description" :label="$t('agentWorkflow.descriptionLabel')" min-width="250" show-overflow-tooltip />
        <ElTableColumn prop="status" :label="$t('agentWorkflow.statusLabel')" width="100" align="center">
          <template #default="{ row }">
            <ElTag :type="getStatusColor(row.status)" size="small">
              {{ getStatusLabel(row.status) }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn prop="triggerType" :label="$t('agentWorkflow.triggerTypeLabel')" width="100" align="center">
          <template #default="{ row }">
            <ElTag type="info" size="small">
              {{ row.triggerType || '-' }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn prop="thinkingMode" :label="$t('agentWorkflow.thinkingModeLabel')" width="120" align="center">
          <template #default="{ row }">
            {{ row.thinkingMode || '-' }}
          </template>
        </ElTableColumn>
        <ElTableColumn prop="actualDuration" :label="$t('agentWorkflow.durationLabel')" width="100" align="center">
          <template #default="{ row }">
            {{ formatDuration(row.actualDuration) }}
          </template>
        </ElTableColumn>
        <ElTableColumn prop="createTime" :label="$t('common.createTime')" width="170" align="center">
          <template #default="{ row }">
            {{ formatDateTime(row.createTime) }}
          </template>
        </ElTableColumn>
        <ElTableColumn :label="$t('common.operate')" width="150" align="center" fixed="right">
          <template #default="{ row }">
            <ElSpace>
              <ElButton type="primary" size="small" plain @click="onViewDetail(row)">
                {{ $t('common.detail') }}
              </ElButton>
              <ElButton type="danger" size="small" plain @click="onDelete(row)">
                {{ $t('common.delete') }}
              </ElButton>
            </ElSpace>
          </template>
        </ElTableColumn>
      </ElTable>
      <ElPagination v-if="pagination.total > 0" :current-page="pagination.page" :total="pagination.total" :page-size="pagination.size" :page-sizes="pagination.sizes" :layout="pagination.layout" :hide-on-single-page="false" class="pagination" background @current-change="currentChange" @size-change="sizeChange" />
    </FaPageMain>
  </div>
</template>

<style scoped>
.absolute-container {
  position: absolute;
  display: flex;
  flex-direction: column;
  width: 100%;
  height: 100%;
}

.search-form {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(330px, 1fr));
  margin-bottom: -18px;

  :deep(.el-form-item) {
    grid-column: auto / span 1;

    &:last-child {
      grid-column-end: -1;

      .el-form-item__content {
        justify-content: flex-end;
      }
    }
  }
}

.el-divider {
  width: calc(100% + 40px);
  margin-inline: -20px;
}
</style>
