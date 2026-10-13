<route lang="yaml">
meta:
  title: 智能体配置
  icon: i-carbon:bot
</route>

<script setup lang="ts">
import { ElMessage, ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import apiAgentRole from '@/api/modules/agent/agentRole'
import FormMode from './components/FormMode/index.vue'
import ToolAuth from './components/ToolAuth/index.vue'
import SkillAuth from './components/SkillAuth/index.vue'
import AgentRelation from './components/AgentRelation/index.vue'
import messages from './components/lang'

defineOptions({
  name: 'AgentRoleList',
})

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const l in messages) {
  mergeLocaleMessage(l, messages[l])
}

const { pagination, getParams, onSizeChange, onCurrentChange, onSortChange } = usePagination()

const formMode = ref<'dialog' | 'drawer'>('drawer')

const formModeProps = ref({
  visible: false,
  id: '',
})

// 工具授权弹窗
const toolAuthProps = ref({
  visible: false,
  roleId: '',
  roleName: '',
})

// 技能授权弹窗
const skillAuthProps = ref({
  visible: false,
  roleId: '',
  roleName: '',
})

// 下属智能体弹窗
const agentRelationProps = ref({
  visible: false,
  roleId: '',
  roleName: '',
})

// 搜索
const searchDefault = {
  roleName: '',
  agentType: '',
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
    ...(search.value.roleName && { roleName: search.value.roleName }),
    ...(search.value.agentType && { agentType: search.value.agentType }),
    ...(search.value.isEnabled !== '' && { isEnabled: search.value.isEnabled }),
  }
  apiAgentRole.list(params).then((res: any) => {
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
      ElMessage.warning(t('agentRole_form.selectOneRecord'))
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
  ElMessageBox.confirm(t('agentRole_form.deleteConfirm', { name: row.roleName }), t('agentRole_form.confirmTitle')).then(() => {
    apiAgentRole.delete(row.id).then(() => {
      getDataList()
      ElMessage.success(t('common.deleteSuccess'))
    })
  }).catch(() => {})
}

function onBatchDelete() {
  if (batch.value.selectionDataList.length === 0) {
    ElMessage.warning(t('agentRole_form.selectAtLeastOne'))
    return
  }
  const ids = batch.value.selectionDataList.map((item: any) => item.id)
  ElMessageBox.confirm(t('agentRole_form.deleteBatchConfirm', { count: ids.length }), t('agentRole_form.confirmTitle')).then(() => {
    apiAgentRole.batchDelete(ids).then(() => {
      getDataList()
      ElMessage.success(t('common.deleteSuccess'))
    })
  }).catch(() => {})
}

function onToolAuth(row: any) {
  toolAuthProps.value.roleId = row.id
  toolAuthProps.value.roleName = row.roleName
  toolAuthProps.value.visible = true
}

function onSkillAuth(row: any) {
  skillAuthProps.value.roleId = row.id
  skillAuthProps.value.roleName = row.roleName
  skillAuthProps.value.visible = true
}

function onAgentRelation(row: any) {
  agentRelationProps.value.roleId = row.id
  agentRelationProps.value.roleName = row.roleName
  agentRelationProps.value.visible = true
}

// 智能体类型选项
const agentTypeOptions = [
  { label: t('agentRole_form.agentTypeStandard'), value: 'Standard' },
  { label: t('agentRole_form.agentTypeWorkflow'), value: 'Workflow' },
  { label: t('agentRole_form.agentTypeComposite'), value: 'Composite' },
  { label: t('agentRole_form.agentTypeCode'), value: 'Code' },
]

function getAgentTypeLabel(type: string) {
  const option = agentTypeOptions.find(item => item.value === type)
  return option ? option.label : type
}

function getAgentTypeColor(type: string): 'primary' | 'success' | 'warning' | 'info' | 'danger' {
  const colors: Record<string, 'primary' | 'success' | 'warning' | 'info' | 'danger'> = {
    Standard: 'primary',
    Workflow: 'success',
    Composite: 'warning',
    Code: 'danger',
  }
  return colors[type] || 'info'
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
                <ElFormItem :label="$t('agentRole_form.roleName')">
                  <ElInput v-model="search.roleName" :placeholder="$t('agentRole_form.roleNamePlaceholder')" clearable @keydown.enter="currentChange()" @clear="currentChange()" />
                </ElFormItem>
              </ElCol>
              <ElCol :span="6">
                <ElFormItem :label="$t('agentRole_form.agentType')">
                  <ElSelect v-model="search.agentType" :placeholder="$t('common.pleaseSelect')" clearable @change="currentChange()">
                    <ElOption v-for="item in agentTypeOptions" :key="item.value" :label="item.label" :value="item.value" />
                  </ElSelect>
                </ElFormItem>
              </ElCol>
              <ElCol :span="6">
                <ElFormItem :label="$t('agentRole_form.status')">
                  <ElSelect v-model="search.isEnabled" :placeholder="$t('common.pleaseSelect')" clearable @change="currentChange()">
                    <ElOption :label="$t('common.enbale')" :value="true" />
                    <ElOption :label="$t('common.disable')" :value="false" />
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
        <ElTableColumn prop="roleName" :label="$t('agentRole_form.roleName')" min-width="150" show-overflow-tooltip />
        <ElTableColumn prop="roleCode" :label="$t('agentRole_form.roleCode')" min-width="120" show-overflow-tooltip />
        <ElTableColumn prop="agentType" :label="$t('agentRole_form.agentType')" width="150" align="center">
          <template #default="{ row }">
            <ElTag :type="getAgentTypeColor(row.agentType)" size="small">
              {{ getAgentTypeLabel(row.agentType) }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn prop="description" :label="$t('agentRole_form.description')" min-width="200" show-overflow-tooltip />
        <ElTableColumn prop="isPublic" :label="$t('agentRole_form.isPublic')" width="80" align="center">
          <template #default="{ row }">
            <ElTag :type="row.isPublic ? 'success' : 'info'" size="small">
              {{ row.isPublic ? $t('common.yes') : $t('common.no') }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn prop="isEnabled" :label="$t('agentRole_form.status')" width="80" align="center">
          <template #default="{ row }">
            <ElTag :type="row.isEnabled ? 'success' : 'danger'" size="small">
              {{ row.isEnabled ? $t('common.enbale') : $t('common.disable') }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn :label="$t('common.operate')" width="300" align="center" fixed="right">
          <template #default="{ row }">
            <ElSpace>
              <ElButton type="primary" size="small" plain @click="onEdit(row)">
                {{ $t('common.edit') }}
              </ElButton>
              <ElButton type="success" size="small" plain @click="onToolAuth(row)">
                {{ $t('agentRole_form.toolAuth') }}
              </ElButton>
              <ElButton type="warning" size="small" plain @click="onSkillAuth(row)">
                {{ $t('agentRole_form.skillAuth') }}
              </ElButton>
              <ElButton type="info" size="small" plain @click="onAgentRelation(row)">
                {{ $t('agentRole_form.subordinateConfig') }}
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
    <ToolAuth v-model="toolAuthProps.visible" :role-id="toolAuthProps.roleId" :role-name="toolAuthProps.roleName" />
    <SkillAuth v-model="skillAuthProps.visible" :role-id="skillAuthProps.roleId" :role-name="skillAuthProps.roleName" />
    <AgentRelation v-model="agentRelationProps.visible" :role-id="agentRelationProps.roleId" :role-name="agentRelationProps.roleName" />
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
