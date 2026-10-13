<route lang="yaml">
meta:
  title: 列表页
</route>

<script setup lang="ts">
import { ElCol, ElMessage, ElMessageBox, ElRow, ElSelect } from 'element-plus'
import { useI18n } from 'vue-i18n'
import api from '@/api/modules/log/dataLog'
import eventBus from '@/utils/eventBus'
import FormMode from './components/FormMode/index.vue'
import messages from './components/lang'

defineOptions({
  name: 'DataLogList',
})

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const router = useRouter()
const { pagination, getParams, onSizeChange, onCurrentChange, onSortChange } = usePagination()
const tabbar = useTabbar()
const routerName = router.currentRoute.value.name.toString()
const data = ref({
  loading: false,
  // 表格是否自适应高度
  tableAutoHeight: true,
  /**
   * 详情展示模式
   * router 路由跳转
   * dialog 对话框
   * drawer 抽屉
   */
  formMode: 'dialog', // 'router' as 'router' | 'dialog' | 'drawer',
  // 详情
  formModeProps: {
    visible: false,
    id: '',
  },
  // 搜索
  search: {
    keyword: '',
    diffType: '',
    tableName: '',
    logContent: '',
  },
  // 批量操作
  batch: {
    enable: true,
    selectionDataList: [],
  },
  // 列表数据
  dataList: [],
})

onMounted(() => {
  getDataList()
  if (data.value.formMode === 'router') {
    eventBus.on('get-data-list', () => {
      getDataList()
    })
  }
})

function onDeleteSelect() {
  const ids = []
  data.value.batch.selectionDataList.forEach((item: any) => {
    ids.push(item.id)
  })
  if (ids.length <= 0) {
    ElMessage.warning(t('common.mustSelectOne'))
    return
  }
  ElMessageBox.confirm(t('common.confirmDel'), t('common.tip'), {
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

function getDataList() {
  data.value.loading = true
  const params = Object.assign(getParams(), data.value.search)

  api.list(params).then((res: any) => {
    data.value.loading = false
    data.value.dataList = res.data
    pagination.value.total = res.total
  })
}

// 每页数量切换
function sizeChange(size: number) {
  onSizeChange(size).then(() => getDataList())
}

// 当前页码切换（翻页）
function currentChange(page = 1) {
  onCurrentChange(page).then(() => getDataList())
}

// 字段排序
function sortChange({ prop, order }: { prop: string, order: string }) {
  onSortChange(prop, order).then(() => getDataList())
}
function onEdit(row: any) {
  data.value.formModeProps.id = row.id
  data.value.formModeProps.visible = true
}

/**
 * 重置
 */
function reset() {
  data.value.search = {
    keyword: '',
    diffType: '',
    tableName: '',
    logContent: '',
  }
}
</script>

<template>
  <div :class="{ 'absolute-container': data.tableAutoHeight }">
    <!-- <PageHeader title="默认模块管理" /> -->
    <FaPageMain :class="{ 'flex-1 overflow-auto': data.tableAutoHeight }" :main-class="{ 'flex-1 flex flex-col overflow-auto': data.tableAutoHeight }">
      <FaSearchBar :show-toggle="false">
        <template #default="{ fold, toggle }">
          <ElForm :model="data.search" size="default" label-width="100px" class="search-form">
            <ElRow :gutter="16">
              <ElCol :span="8">
                <ElFormItem :label="t('common.keyword')">
                  <ElInput
                    v-model="data.search.keyword"
                    :placeholder="`${t('common.pleaseInput')}${t('dataLog_form.tableDesc')}、${t('dataLog_form.operationBy')}`"
                    clearable
                    style="width: 100%;"
                    @keydown.enter="currentChange()"
                    @clear="currentChange()"
                  />
                </ElFormItem>
              </ElCol>
              <ElCol :span="8">
                <ElFormItem :label="t('dataLog_form.tableName')">
                  <ElInput
                    v-model="data.search.tableName"
                    :placeholder="`${t('common.pleaseInput')}${t('dataLog_form.tableName')}`"
                    clearable
                    style="width: 100%;"
                    @keydown.enter="currentChange()"
                    @clear="currentChange()"
                  />
                </ElFormItem>
              </ElCol>
              <ElCol :span="8">
                <ElFormItem :label="t('dataLog_form.logContent')">
                  <ElInput
                    v-model="data.search.logContent"
                    :placeholder="`${t('common.pleaseInput')}${t('dataLog_form.logContent')}`"
                    clearable
                    style="width: 100%;"
                    @keydown.enter="currentChange()"
                    @clear="currentChange()"
                  />
                </ElFormItem>
              </ElCol>
              <ElCol :span="4">
                <ElFormItem :label="t('dataLog_form.diffType')">
                  <ElSelect v-model="data.search.diffType" clearable>
                    <el-option value="INSERT" label="INSERT" />
                    <el-option value="UPDATE" label="UPDATE" />
                    <el-option value="DELETE" label="DELETE" />
                  </ElSelect>
                </ElFormItem>
              </ElCol>
              <ElCol :span="4">
                <ElButton type="primary" @click="currentChange()">
                  <template #icon>
                    <FaIcon name="i-ep:search" />
                  </template>
                  {{ $t('common.search') }}
                </ElButton>
                <ElButton type="info" @click="reset()">
                  <template #icon>
                    <FaIcon name="ep:refresh-left" />
                  </template>
                  {{ $t('common.reset') }}
                </ElButton>
              </ElCol>
            </ElRow>
          </ElForm>
        </template>
      </FaSearchBar>
      <ElDivider border-style="dashed" />
      <ElSpace wrap>
        <ElButton v-auth="'dataLog.delete'" size="default" type="danger" danger :disabled="!data.batch.selectionDataList.length" @click="onDeleteSelect">
          <template #icon>
            <FaIcon name="i-ep:delete" />
          </template>{{ $t('common.delete') }}
        </ElButton>
      </ElSpace>
      <ElTable v-loading="data.loading" class="my-4" :data="data.dataList" stripe highlight-current-row border height="100%" @sort-change="sortChange" @selection-change="data.batch.selectionDataList = $event">
        <ElTableColumn v-if="data.batch.enable" type="selection" align="center" fixed />
        <ElTableColumn prop="opt" align="center" :label="$t('common.operate')" width="100">
          <template #default="scope">
            <ElButton v-auth="`${routerName}.view`" type="primary" size="small" plain @click="onEdit(scope.row)">
              {{ $t('common.detail') }}
            </ElButton>
          </template>
        </ElTableColumn>
        <ElTableColumn prop="diffType" :label="t('dataLog_form.diffType')" width="100">
          <template #default="scope">
            <template v-if=" scope.row.diffType === 'DELETE'">
              <ElTag type="danger">
                {{ scope.row.diffType }}
              </ElTag>
            </template>
            <template v-else-if=" scope.row.diffType === 'UPDATE'">
              <ElTag type="success">
                {{ scope.row.diffType }}
              </ElTag>
            </template>
            <template v-else>
              <ElTag>
                {{ scope.row.diffType }}
              </ElTag>
            </template>
          </template>
        </ElTableColumn>
        <ElTableColumn prop="tableName" :label="t('dataLog_form.tableName')">
          <template #default="scope">
            {{ scope.row.tableName }}
          </template>
        </ElTableColumn>
        <ElTableColumn prop="tableDesc" :label="t('dataLog_form.tableDesc')">
          <template #default="scope">
            {{ scope.row.tableDesc }}
          </template>
        </ElTableColumn>
        <ElTableColumn prop="tableDesc" :label="t('dataLog_form.operationBy')" width="120">
          <template #default="scope">
            {{ scope.row.createrName }}
          </template>
        </ElTableColumn>
        <ElTableColumn prop="tableDesc" :label="t('dataLog_form.operationTime')" width="170">
          <template #default="scope">
            {{ scope.row.createTime }}
          </template>
        </ElTableColumn>
      </ElTable>
      <ElPagination v-if="pagination.total > 0" :current-page="pagination.page" :total="pagination.total" :page-size="pagination.size" :page-sizes="pagination.sizes" :layout="pagination.layout" :hide-on-single-page="false" class="pagination" background @size-change="sizeChange" @current-change="currentChange" />
    </FaPageMain>
    <FormMode v-if="data.formMode === 'dialog' || data.formMode === 'drawer'" :id="data.formModeProps.id" v-model="data.formModeProps.visible" :mode="data.formMode" @success="getDataList" />
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
