<route lang="yaml">
meta:
  title: 技能管理
  icon: i-carbon:skill-level-advanced
</route>

<script setup lang="ts">
import { ElMessage, ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import apiSkillDefinition from '@/api/modules/agent/skillDefinition'
import FormMode from './components/FormMode/index.vue'
import messages from './components/lang'

defineOptions({
  name: 'SkillDefinitionList',
})

const { t, mergeLocaleMessage } = useI18n()
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const { pagination, getParams, onSizeChange, onCurrentChange, onSortChange } = usePagination()

const formMode = ref<'dialog' | 'drawer'>('drawer')

const formModeProps = ref({
  visible: false,
  id: '',
})

// 搜索
const searchDefault = {
  skillName: '',
  category: '',
  approvalStatus: '',
  isEnabled: '',
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
    ...(search.value.skillName && { skillName: search.value.skillName }),
    ...(search.value.category && { category: search.value.category }),
    ...(search.value.approvalStatus && { approvalStatus: search.value.approvalStatus }),
    ...(search.value.isEnabled !== '' && { isEnabled: search.value.isEnabled }),
  }
  apiSkillDefinition.list(params).then((res: any) => {
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

function onCreate() {
  formModeProps.value.id = ''
  formModeProps.value.visible = true
}

function onEdit(row: any) {
  if (!row) {
    if (batch.value.selectionDataList.length !== 1) {
      ElMessage.warning(t('skill_form.selectOne'))
      return
    }
    formModeProps.value.id = batch.value.selectionDataList[0].id
  }
  else {
    formModeProps.value.id = row.id
  }
  formModeProps.value.visible = true
}

function onDelete(row: any) {
  ElMessageBox.confirm(t('skill_form.deleteConfirm', { name: row.skillName }), t('skill_form.confirmTitle')).then(() => {
    apiSkillDefinition.delete(row.id).then(() => {
      getDataList()
      ElMessage.success(t('common.deleteSuccess'))
    })
  }).catch(() => {})
}

function onBatchDelete() {
  if (batch.value.selectionDataList.length === 0) {
    ElMessage.warning(t('skill_form.selectAtLeastOne'))
    return
  }
  const ids = batch.value.selectionDataList.map((item: any) => item.id)
  ElMessageBox.confirm(t('skill_form.deleteBatchConfirm', { n: ids.length }), t('skill_form.confirmTitle')).then(() => {
    apiSkillDefinition.batchDelete(ids).then(() => {
      getDataList()
      ElMessage.success(t('common.deleteSuccess'))
    })
  }).catch(() => {})
}

// 审批操作
async function onApprove(row: any) {
  try {
    await apiSkillDefinition.approve(row.id, 'Approved')
    ElMessage.success(t('skill_form.approveSuccess'))
    getDataList()
  }
  catch {
    ElMessage.error(t('skill_form.approveFailed'))
  }
}

async function onReject(row: any) {
  try {
    await apiSkillDefinition.approve(row.id, 'Rejected')
    ElMessage.warning(t('skill_form.skillRejected'))
    getDataList()
  }
  catch {
    ElMessage.error(t('skill_form.approveFailed'))
  }
}

// 分类选项
const categoryOptions = computed(() => [
  { label: t('skill_form.categoryPerception'), value: 'Perception' },
  { label: t('skill_form.categoryAnalysis'), value: 'Analysis' },
  { label: t('skill_form.categoryDecision'), value: 'Decision' },
  { label: t('skill_form.categoryExecution'), value: 'Execution' },
  { label: t('skill_form.categoryCollaboration'), value: 'Collaboration' },
])

// 审批状态选项
const approvalStatusOptions = computed(() => [
  { label: t('skill_form.statusPending'), value: 'Pending' },
  { label: t('skill_form.statusApproved'), value: 'Approved' },
  { label: t('skill_form.statusRejected'), value: 'Rejected' },
])

function getCategoryColor(category: string): 'primary' | 'success' | 'warning' | 'info' | 'danger' {
  const colors: Record<string, 'primary' | 'success' | 'warning' | 'info' | 'danger'> = {
    Perception: 'primary',
    Analysis: 'success',
    Decision: 'warning',
    Execution: 'danger',
    Collaboration: 'info',
  }
  return colors[category] || 'info'
}

function getApprovalStatusColor(status: string): 'primary' | 'success' | 'warning' | 'info' | 'danger' {
  const colors: Record<string, 'primary' | 'success' | 'warning' | 'info' | 'danger'> = {
    Pending: 'warning',
    Approved: 'success',
    Rejected: 'danger',
  }
  return colors[status] || 'info'
}
</script>

<template>
  <div class="absolute-container">
    <FaPageMain class="flex-1 overflow-auto" :main-class="{ 'flex-1 flex flex-col overflow-auto': true }">
      <FaSearchBar :show-toggle="false">
        <template #default>
          <ElForm :model="search" size="default" label-width="80px" class="search-form">
            <ElRow :gutter="20">
              <ElCol :span="5">
                <ElFormItem :label="$t('skill_form.skillName')">
                  <ElInput v-model="search.skillName" :placeholder="$t('common.pleaseInput') + $t('skill_form.skillName')" clearable @keydown.enter="currentChange()" @clear="currentChange()" />
                </ElFormItem>
              </ElCol>
              <ElCol :span="5">
                <ElFormItem :label="$t('skill_form.category')">
                  <ElSelect v-model="search.category" :placeholder="$t('common.pleaseSelect')" clearable @change="currentChange()">
                    <ElOption v-for="item in categoryOptions" :key="item.value" :label="item.label" :value="item.value" />
                  </ElSelect>
                </ElFormItem>
              </ElCol>
              <ElCol :span="5">
                <ElFormItem :label="$t('skill_form.approvalStatus')">
                  <ElSelect v-model="search.approvalStatus" :placeholder="$t('common.pleaseSelect')" clearable @change="currentChange()">
                    <ElOption v-for="item in approvalStatusOptions" :key="item.value" :label="item.label" :value="item.value" />
                  </ElSelect>
                </ElFormItem>
              </ElCol>
              <ElCol :span="4">
                <ElFormItem :label="$t('skill_form.status')">
                  <ElSelect v-model="search.isEnabled" :placeholder="$t('common.pleaseSelect')" clearable @change="currentChange()">
                    <ElOption :label="$t('common.enbale')" :value="true" />
                    <ElOption :label="$t('common.disable')" :value="false" />
                  </ElSelect>
                </ElFormItem>
              </ElCol>
              <ElCol :span="5">
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
        <ElButton type="primary" size="default" @click="onCreate">
          <template #icon>
            <FaIcon name="i-ep:plus" />
          </template>
          {{ $t('common.create') }}
        </ElButton>
        <ElButton size="default" type="primary" plain :disabled="batch.selectionDataList.length !== 1" @click="onEdit(null)">
          <template #icon>
            <FaIcon name="i-ep:edit" />
          </template>
          {{ $t('common.edit') }}
        </ElButton>
        <ElButton size="default" type="danger" :disabled="!batch.selectionDataList.length" @click="onBatchDelete">
          <template #icon>
            <FaIcon name="i-ep:delete" />
          </template>
          {{ $t('common.deleteBatch') }}
        </ElButton>
      </ElSpace>
      <ElTable v-loading="loading" class="my-4" :data="dataList" stripe highlight-current-row border height="100%" @sort-change="sortChange" @selection-change="batch.selectionDataList = $event">
        <ElTableColumn v-if="batch.enable" type="selection" align="center" fixed width="50" />
        <ElTableColumn prop="skillName" :label="$t('skill_form.skillName')" min-width="150" show-overflow-tooltip />
        <ElTableColumn prop="skillCode" :label="$t('skill_form.skillCode')" min-width="120" show-overflow-tooltip />
        <ElTableColumn prop="category" :label="$t('skill_form.category')" width="120" align="center">
          <template #default="{ row }">
            <ElTag v-if="row.category" :type="getCategoryColor(row.category)" size="small">
              {{ row.category }}
            </ElTag>
            <span v-else class="text-gray-400">-</span>
          </template>
        </ElTableColumn>
        <ElTableColumn prop="description" :label="$t('skill_form.description')" min-width="200" show-overflow-tooltip />
        <ElTableColumn prop="isAgentSkill" label="Agent Skill" width="100" align="center">
          <template #default="{ row }">
            <ElTag :type="row.isAgentSkill ? 'success' : 'info'" size="small">
              {{ row.isAgentSkill ? $t('common.yes') : $t('common.no') }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn prop="approvalStatus" :label="$t('skill_form.approvalStatus')" width="100" align="center">
          <template #default="{ row }">
            <ElTag :type="getApprovalStatusColor(row.approvalStatus)" size="small">
              {{ row.approvalStatus || 'Approved' }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn prop="isEnabled" :label="$t('skill_form.status')" width="80" align="center">
          <template #default="{ row }">
            <ElTag :type="row.isEnabled ? 'success' : 'danger'" size="small">
              {{ row.isEnabled ? $t('common.enbale') : $t('common.disable') }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn :label="$t('common.operate')" width="250" align="center" fixed="right">
          <template #default="{ row }">
            <ElSpace>
              <ElButton type="primary" size="small" plain @click="onEdit(row)">
                {{ $t('common.edit') }}
              </ElButton>
              <ElButton v-if="row.approvalStatus === 'Pending'" type="success" size="small" plain @click="onApprove(row)">
                {{ $t('skill_form.approve') }}
              </ElButton>
              <ElButton v-if="row.approvalStatus === 'Pending'" type="warning" size="small" plain @click="onReject(row)">
                {{ $t('skill_form.reject') }}
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
    <FormMode :id="formModeProps.id" v-model="formModeProps.visible" :mode="formMode" @success="getDataList" />
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
  grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
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
