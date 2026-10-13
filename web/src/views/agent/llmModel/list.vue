<route lang="yaml">
meta:
  title: 模型管理
  icon: i-carbon:machine-learning-model
</route>

<script setup lang="ts">
import { ElMessage, ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import apiLlmModel from '@/api/modules/agent/llmModel'
import FormMode from './components/FormMode/index.vue'
import messages from './components/lang'
import PurposeBinding from './components/PurposeBinding/index.vue'
import purposeMessages from './components/PurposeBinding/lang'
import QuickTest from './components/QuickTest/index.vue'

defineOptions({
  name: 'LlmModelList',
})

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}
for (const locale in purposeMessages) {
  mergeLocaleMessage(locale, purposeMessages[locale])
}

const { pagination, getParams, onSizeChange, onCurrentChange, onSortChange } = usePagination()

const formMode = ref<'dialog' | 'drawer'>('dialog')

const formModeProps = ref({
  visible: false,
  id: '',
})
const purposeBindingVisible = ref(false)

// 快速测试
const quickTestProps = ref({
  visible: false,
  model: null as any,
})

function onQuickTest(row: any) {
  quickTestProps.value.model = {
    id: row.id,
    modelName: row.modelName,
    modelCode: row.modelCode,
    provider: row.provider,
    modelType: row.modelType,
  }
  quickTestProps.value.visible = true
}

// 搜索
const searchDefault = {
  modelName: '',
  modelType: '',
  provider: '',
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
    ...(search.value.modelName && { modelName: search.value.modelName }),
    ...(search.value.modelType && { modelType: search.value.modelType }),
    ...(search.value.provider && { provider: search.value.provider }),
  }
  apiLlmModel.list(params).then((res: any) => {
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
      ElMessage.warning(t('llmModel_form.selectOne'))
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
  ElMessageBox.confirm(t('llmModel_form.deleteConfirm', { name: row.modelName }), t('llmModel_form.confirmTitle')).then(() => {
    apiLlmModel.delete(row.id).then(() => {
      getDataList()
      ElMessage.success(t('common.deleteSuccess'))
    })
  }).catch(() => {})
}

function onBatchDelete() {
  if (batch.value.selectionDataList.length === 0) {
    ElMessage.warning(t('llmModel_form.selectAtLeastOne'))
    return
  }
  const ids = batch.value.selectionDataList.map((item: any) => item.id)
  ElMessageBox.confirm(t('llmModel_form.deleteBatchConfirm', { count: ids.length }), t('llmModel_form.confirmTitle')).then(() => {
    apiLlmModel.batchDelete(ids).then(() => {
      getDataList()
      ElMessage.success(t('common.deleteSuccess'))
    })
  }).catch(() => {})
}

// 模型类型选项
const modelTypeOptions = [
  { label: 'Chat', value: 'Chat' },
  { label: 'VL', value: 'VL' },
  { label: 'Embedding', value: 'Embedding' },
  { label: 'Rerank', value: 'Rerank' },
  { label: 'STT', value: 'STT' },
  { label: 'TTS', value: 'TTS' },
  { label: 'Image', value: 'Image' },
]

// 提供商选项
const providerOptions = [
  { label: 'OpenAI', value: 'OpenAI' },
  { label: 'Azure', value: 'Azure' },
  { label: 'DeepSeek', value: 'DeepSeek' },
  { label: 'Ollama', value: 'Ollama' },
]
</script>

<template>
  <div class="absolute-container">
    <FaPageMain class="flex-1 overflow-auto" :main-class="{ 'flex-1 flex flex-col overflow-auto': true }">
      <FaSearchBar :show-toggle="false">
        <template #default>
          <ElForm :model="search" size="default" label-width="80px" class="search-form">
            <ElRow :gutter="20">
              <ElCol :span="6">
                <ElFormItem :label="$t('llmModel_form.modelName')">
                  <ElInput v-model="search.modelName" :placeholder="$t('llmModel_form.modelNamePh')" clearable @keydown.enter="currentChange()" @clear="currentChange()" />
                </ElFormItem>
              </ElCol>
              <ElCol :span="6">
                <ElFormItem :label="$t('llmModel_form.modelType')">
                  <ElSelect v-model="search.modelType" :placeholder="$t('common.pleaseSelect')" clearable @change="currentChange()">
                    <ElOption v-for="item in modelTypeOptions" :key="item.value" :label="item.label" :value="item.value" />
                  </ElSelect>
                </ElFormItem>
              </ElCol>
              <ElCol :span="6">
                <ElFormItem :label="$t('llmModel_form.provider')">
                  <ElSelect v-model="search.provider" :placeholder="$t('common.pleaseSelect')" clearable @change="currentChange()">
                    <ElOption v-for="item in providerOptions" :key="item.value" :label="item.label" :value="item.value" />
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
        <ElButton size="default" type="primary" plain @click="purposeBindingVisible = true">
          <template #icon>
            <FaIcon name="i-ep:connection" />
          </template>
          {{ $t('modelPurpose.title') }}
        </ElButton>
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
        <ElTableColumn prop="modelName" :label="$t('llmModel_form.modelName')" min-width="150" show-overflow-tooltip />
        <ElTableColumn prop="modelCode" :label="$t('llmModel_form.modelCode')" min-width="150" show-overflow-tooltip />
        <ElTableColumn prop="modelType" :label="$t('llmModel_form.modelType')" width="100" align="center">
          <template #default="{ row }">
            <ElTag :type="row.modelType === 'Chat' ? 'primary' : 'info'" size="small">
              {{ row.modelType }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn prop="provider" :label="$t('llmModel_form.provider')" width="100" align="center" />
        <ElTableColumn prop="contextWindow" :label="$t('llmModel_form.contextWindow')" width="110" align="right" />
        <ElTableColumn prop="maxTokens" :label="$t('llmModel_form.maxOutput')" width="100" align="right" />
        <ElTableColumn prop="isEnabled" :label="$t('llmModel_form.status')" width="80" align="center">
          <template #default="{ row }">
            <ElTag :type="row.isEnabled ? 'success' : 'danger'" size="small">
              {{ row.isEnabled ? $t('common.enbale') : $t('common.disable') }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn :label="$t('common.operate')" width="210" align="center" fixed="right">
          <template #default="{ row }">
            <ElSpace>
              <ElButton v-if="row.modelType === 'Chat' || row.modelType === 'VL'" type="success" size="small" plain @click="onQuickTest(row)">
                {{ $t('llmModel_form.test') }}
              </ElButton>
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
    <PurposeBinding v-model="purposeBindingVisible" />
    <QuickTest v-model="quickTestProps.visible" :model="quickTestProps.model" />
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
