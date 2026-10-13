<route lang="yaml">
meta:
  title: 工具配置
  icon: i-carbon:tool-kit
</route>

<script setup lang="ts">
import { ElMessage, ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import apiToolDefinition from '@/api/modules/agent/toolDefinition'
import FormMode from './components/FormMode/index.vue'
import messages from './components/lang'

defineOptions({
  name: 'ToolDefinitionList',
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
  toolName: '',
  toolType: '',
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
    ...(search.value.toolName && { toolName: search.value.toolName }),
    ...(search.value.toolType && { toolType: search.value.toolType }),
    ...(search.value.isEnabled !== '' && { isEnabled: search.value.isEnabled }),
  }
  apiToolDefinition.list(params).then((res: any) => {
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
      ElMessage.warning(t('tool_form.selectOne'))
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
  ElMessageBox.confirm(t('tool_form.deleteConfirm', { name: row.toolName }), t('tool_form.confirmTitle')).then(() => {
    apiToolDefinition.delete(row.id).then(() => {
      getDataList()
      ElMessage.success(t('common.deleteSuccess'))
    })
  }).catch(() => {})
}

function onBatchDelete() {
  if (batch.value.selectionDataList.length === 0) {
    ElMessage.warning(t('tool_form.selectAtLeastOne'))
    return
  }
  const ids = batch.value.selectionDataList.map((item: any) => item.id)
  ElMessageBox.confirm(t('tool_form.deleteBatchConfirm', { n: ids.length }), t('tool_form.confirmTitle')).then(() => {
    apiToolDefinition.batchDelete(ids).then(() => {
      getDataList()
      ElMessage.success(t('common.deleteSuccess'))
    })
  }).catch(() => {})
}

// 工具类型选项
const toolTypeOptions = computed(() => [
  { label: t('tool_form.toolTypeLocal'), value: 'Local' },
  { label: t('tool_form.toolTypeHttp'), value: 'Http' },
  { label: t('tool_form.toolTypeMcp'), value: 'Mcp' },
])

function getToolTypeColor(type: string) {
  const colors: Record<string, string> = {
    Local: 'success',
    Http: 'primary',
    Mcp: 'warning',
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
                <ElFormItem :label="$t('tool_form.toolName')">
                  <ElInput v-model="search.toolName" :placeholder="$t('common.pleaseInput') + $t('tool_form.toolName')" clearable @keydown.enter="currentChange()" @clear="currentChange()" />
                </ElFormItem>
              </ElCol>
              <ElCol :span="6">
                <ElFormItem :label="$t('tool_form.toolType')">
                  <ElSelect v-model="search.toolType" :placeholder="$t('common.pleaseSelect')" clearable @change="currentChange()">
                    <ElOption v-for="item in toolTypeOptions" :key="item.value" :label="item.label" :value="item.value" />
                  </ElSelect>
                </ElFormItem>
              </ElCol>
              <ElCol :span="6">
                <ElFormItem :label="$t('tool_form.status')">
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
        <ElTableColumn prop="toolName" :label="$t('tool_form.toolName')" min-width="150" show-overflow-tooltip />
        <ElTableColumn prop="toolCode" :label="$t('tool_form.toolCode')" min-width="150" show-overflow-tooltip />
        <ElTableColumn prop="toolType" :label="$t('tool_form.toolType')" width="120" align="center">
          <template #default="{ row }">
            <ElTag :type="getToolTypeColor(row.toolType)" size="small">
              {{ row.toolType }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn prop="description" :label="$t('tool_form.description')" min-width="200" show-overflow-tooltip />
        <ElTableColumn prop="requiresConfirmation" :label="$t('tool_form.confirmRequired')" width="80" align="center">
          <template #default="{ row }">
            <ElTag :type="row.requiresConfirmation ? 'warning' : 'info'" size="small">
              {{ row.requiresConfirmation ? $t('common.yes') : $t('common.no') }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn prop="isEnabled" :label="$t('tool_form.status')" width="80" align="center">
          <template #default="{ row }">
            <ElTag :type="row.isEnabled ? 'success' : 'danger'" size="small">
              {{ row.isEnabled ? $t('common.enbale') : $t('common.disable') }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn :label="$t('common.operate')" width="150" align="center" fixed="right">
          <template #default="{ row }">
            <ElSpace>
              <ElButton type="primary" size="small" plain @click="onEdit(row)">
                {{ $t('common.edit') }}
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
