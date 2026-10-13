<route lang="yaml">
meta:
  title: PLC项目管理
</route>

<script setup lang="ts">
import { ElMessage, ElMessageBox } from 'element-plus'
import api from '@/api/modules/AI_PLC/project'
import FormMode from './components/FormMode/index.vue'
import BlockDetailDialog from './components/BlockDetailDialog/index.vue'
import { useI18n } from 'vue-i18n'
import messages from './list.lang'

defineOptions({
  name: 'ProjectList',
})

const router = useRouter()
const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}
const { pagination, getParams, onSizeChange, onCurrentChange, onSortChange } = usePagination()

const data = ref({
  loading: false,
  tableAutoHeight: true,
  formMode: 'dialog',
  formModeProps: {
    visible: false,
    id: '',
  },
  search: {
    ProjectName: '',
  },
  batch: {
    enable: true,
    selectionDataList: [],
  },
  dataList: [],
  detailDialogProps: {
    visible: false,
    projectId: '' as string | number,
    projectName: '',
  },
})

onMounted(() => {
  getDataList()
})

function getDataList() {
  data.value.loading = true
  const params = Object.assign(getParams(), data.value.search)
  api.list(params).then((res: any) => {
    data.value.loading = false
    data.value.dataList = res.data
    pagination.value.total = res.total
  })
}

function onDeleteSelect() {
  const ids: any[] = []
  data.value.batch.selectionDataList.forEach((item: any) => {
    ids.push(item.id)
  })
  if (ids.length <= 0) {
    ElMessage.warning(t('aiPlcProject_list.selectToDelete'))
    return
  }
  ElMessageBox.confirm(t('aiPlcProject_list.confirmDeleteSelected'), t('common.tip'), {
    confirmButtonText: t('common.confirm'),
    cancelButtonText: t('common.cancel'),
    type: 'warning',
  }).then(() => {
    api.batchDelete(ids).then(() => {
      ElMessage.success(t('common.deleteSuccess'))
      getDataList()
    })
  })
}

function onEditSelect() {
  if (data.value.batch.selectionDataList.length > 1) {
    ElMessage.warning(t('aiPlcProject_list.selectOneToEdit'))
    return
  }
  const row = data.value.batch.selectionDataList[0]
  onEdit(row)
}

function onCreate() {
  data.value.formModeProps.id = ''
  data.value.formModeProps.visible = true
}

function onEdit(row: any) {
  data.value.formModeProps.id = row.id
  data.value.formModeProps.visible = true
}

function onDel(row: any) {
  ElMessageBox.confirm(t('aiPlcProject_list.confirmDeleteThis'), t('common.tip'))
    .then(() => {
      api.delete(row.id).then(() => {
        getDataList()
        ElMessage.success(t('common.deleteSuccess'))
      })
    })
    .catch(() => {})
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

function onParseNew() {
  // window.open('/AI_PLC/Project/parse', '_blank')
  router.push({
    path: '/parse',
    query: {
      //id: agent.id,
      // name: agent.agentName,
    },
  })
}

function onDetail(row: any) {
  data.value.detailDialogProps.projectId = row.id
  data.value.detailDialogProps.projectName = row.projectName || ''
  data.value.detailDialogProps.visible = true
}
</script>

<template>
  <div :class="{ 'absolute-container': data.tableAutoHeight }">
    <FaPageMain :class="{ 'flex-1 overflow-auto': data.tableAutoHeight }" :main-class="{ 'flex-1 flex flex-col overflow-auto': data.tableAutoHeight }">
      <FaSearchBar :show-toggle="false">
        <template #default>
          <ElForm :model="data.search" size="default" label-width="100px" inline-message class="search-form">
            <ElRow :gutter="16">
              <ElCol :span="8">
                <ElFormItem :label="$t('aiPlcProject_list.projectName')">
                  <ElInput v-model="data.search.ProjectName" :placeholder="$t('aiPlcProject_list.projectNamePlaceholder')" clearable @keydown.enter="currentChange()" @clear="currentChange()" />
                </ElFormItem>
              </ElCol>
              <ElCol :span="5">
                <ElButton type="primary" @click="currentChange()">
                  <FaIcon name="i-ep:search" /> {{ $t('aiPlcProject_list.search') }}
                </ElButton>
              </ElCol>
            </ElRow>
          </ElForm>
        </template>
      </FaSearchBar>
      <ElDivider border-style="dashed" />
      <ElSpace wrap>
        <ElButton type="primary" @click="onCreate">
          <FaIcon name="i-ep:plus" /> {{ $t('common.add') }}
        </ElButton>
        <ElButton type="success" @click="onParseNew">
          <FaIcon name="i-ep:upload-filled" /> {{ $t('aiPlcProject_list.parse') }}
        </ElButton>
        <ElButton type="primary" plain :disabled="!data.batch.selectionDataList.length" @click="onEditSelect">
          <FaIcon name="i-ep:edit" /> {{ $t('common.edit') }}
        </ElButton>
        <ElButton type="danger" :disabled="!data.batch.selectionDataList.length" @click="onDeleteSelect">
          <FaIcon name="i-ep:delete" /> {{ $t('common.delete') }}
        </ElButton>
      </ElSpace>

      <ElTable
        v-loading="data.loading"
        class="my-4"
        :data="data.dataList"
        stripe
        highlight-current-row
        border
        height="100%"
        @sort-change="sortChange"
        @selection-change="data.batch.selectionDataList = $event"
      >
        <ElTableColumn type="selection" align="center" fixed />
        <ElTableColumn prop="productionLineCode" :label="$t('aiPlcProject_list.productionLineCode')" width="200" align="center" />
        <ElTableColumn prop="projectName" :label="$t('aiPlcProject_list.projectName')" width="250" align="center" show-overflow-tooltip />
        <ElTableColumn prop="brand" :label="$t('aiPlcProject_list.brand')" width="100"  align="center"/>
        <ElTableColumn prop="model" :label="$t('aiPlcProject_list.modelSeries')" width="100"  align="center"/>
        <ElTableColumn prop="version" :label="$t('aiPlcProject_list.version')" width="100"  align="center"/>
        <ElTableColumn prop="status" :label="$t('aiPlcProject_list.status')" width="100" align="center">
           <template #default="scope">
              <ElTag v-if="scope.row.status === 0" type="info">{{ $t('aiPlcProject_list.statusPendingParse') }}</ElTag>
              <ElTag v-else-if="scope.row.status === 1" type="warning">{{ $t('aiPlcProject_list.statusParsing') }}</ElTag>
              <ElTag v-else-if="scope.row.status === 2" type="success">{{ $t('aiPlcProject_list.statusReady') }}</ElTag>
              <ElTag v-else-if="scope.row.status === 3" type="danger">{{ $t('aiPlcProject_list.statusFailed') }}</ElTag>
              <span v-else>{{ scope.row.status }}</span>
           </template>
        </ElTableColumn>
        <ElTableColumn prop="fileCount" :label="$t('aiPlcProject_list.fileCount')" width="100" align="center" />
        <ElTableColumn prop="successCount" :label="$t('aiPlcProject_list.successCount')" width="100" align="center" />
        <ElTableColumn prop="failCount" :label="$t('aiPlcProject_list.failCount')" width="100" align="center" />
        <ElTableColumn prop="remark" :label="$t('aiPlcProject_list.remark')" width="100" align="center" show-overflow-tooltip />
        <ElTableColumn prop="createTime" :label="$t('common.createTime')" align="center"  fixed="right"/>
        <ElTableColumn :label="$t('common.operate')" align="center" fixed="right">
          <template #default="scope">
            <ElButton type="success" size="small" plain @click="onDetail(scope.row)">
              {{ $t('common.detail') }}
            </ElButton>
            <ElButton type="primary" size="small" plain @click="onEdit(scope.row)">
              {{ $t('common.edit') }}
            </ElButton>
            <ElButton type="danger" size="small" plain @click="onDel(scope.row)">
              {{ $t('common.delete') }}
            </ElButton>
          </template>
        </ElTableColumn>
      </ElTable>

      <ElPagination
        v-if="pagination.total > 0"
        :current-page="pagination.page"
        :total="pagination.total"
        :page-size="pagination.size"
        :page-sizes="pagination.sizes"
        :layout="pagination.layout"
        background
        @size-change="sizeChange"
        @current-change="currentChange"
      />
    </FaPageMain>

    <FormMode
      v-if="data.formMode === 'dialog' || data.formMode === 'drawer'"
      :id="data.formModeProps.id"
      v-model="data.formModeProps.visible"
      :mode="data.formMode"
      @success="getDataList"
    />

    <BlockDetailDialog
      v-model="data.detailDialogProps.visible"
      :project-id="data.detailDialogProps.projectId"
      :project-name="data.detailDialogProps.projectName"
    />
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
}
</style>
