<route lang="yaml">
meta:
  title: 列表页
</route>

<script setup lang="ts">
import { ElCol, ElRow } from 'element-plus'
import { onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import api from '@/api/modules/log/exception'
import FormMode from './components/FormMode/index.vue'
import messages from './components/lang'
import vxeTableConfig from './components/tableConfig'

// vxe-table

defineOptions({
  name: 'ExceptionList',
})

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const router = useRouter()
const { pagination, getParams, onSizeChange, onCurrentChange } = usePagination()
const routerName = ref(router.currentRoute.value.name.toString())
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
    name: '',
    className: '',
    methodName: '',
    exceptionName: '',
    exceptionMsg: '',
    exceptionSource: '',
    stackTrace: '',
    paramsObj: '',
    account: '',
  },
  // 批量操作
  batch: {
    enable: true,
    selectionDataList: [],
  },
  // 列表数据
  dataList: [],
})

const xGrid = ref()
const gridOptions = reactive(vxeTableConfig.config(t))
const gridEvent = vxeTableConfig.gridEvents(t, data)
function changeFilterEvent(event, option, $panel) {
  $panel.changeOption(event, !!option.data, option)
}

onMounted(() => {
  getDataList()
  vxeTableConfig.initCustom(gridOptions, xGrid)
})

function getDataList() {
  data.value.loading = true
  gridOptions.loading = true
  const params = Object.assign(getParams(), data.value.search)
  api.list(params).then((res: any) => {
    data.value.loading = false
    gridOptions.loading = false
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

function onEdit(row: any) {
  data.value.formModeProps.id = row.id
  data.value.formModeProps.visible = true
}

/**
 * 重置
 */
function reset() {
  data.value.search = {
    name: '',
    className: '',
    methodName: '',
    exceptionName: '',
    exceptionMsg: '',
    exceptionSource: '',
    stackTrace: '',
    paramsObj: '',
    account: '',
  }
  const $table = xGrid.value
  if ($table) {
    $table.clearFilter()
    $table.clearSort()
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
            <ElRow :gutter="24">
              <ElCol v-if="!fold" :span="8">
                <ElFormItem :label="t('exception_form.className')" prop="className">
                  <ElInput v-model="data.search.className" :placeholder="t('common.pleaseInput') + t('exception_form.className')" clearable @change="getDataList" />
                </ElFormItem>
              </ElCol>
              <ElCol v-if="!fold" :span="8">
                <ElFormItem :label="t('exception_form.methodName')" prop="methodName">
                  <ElInput v-model="data.search.methodName" :placeholder="t('common.pleaseInput') + t('exception_form.methodName')" clearable @change="getDataList" />
                </ElFormItem>
              </ElCol>
              <ElCol v-if="!fold" :span="8">
                <ElFormItem :label="t('exception_form.exceptionName')" prop="exceptionName">
                  <ElInput v-model="data.search.exceptionName" :placeholder="t('common.pleaseInput') + t('exception_form.exceptionName')" clearable @change="getDataList" />
                </ElFormItem>
              </ElCol>
              <ElCol v-if="!fold" :span="8">
                <ElFormItem :label="t('exception_form.exceptionMsg')" prop="exceptionMsg">
                  <ElInput v-model="data.search.exceptionMsg" :placeholder="t('common.pleaseInput') + t('exception_form.exceptionMsg')" clearable @change="getDataList" />
                </ElFormItem>
              </ElCol>
              <ElCol v-if="!fold" :span="8">
                <ElFormItem :label="t('exception_form.exceptionSource')" prop="exceptionSource">
                  <ElInput v-model="data.search.exceptionSource" :placeholder="t('common.pleaseInput') + t('exception_form.exceptionSource')" clearable @change="getDataList" />
                </ElFormItem>
              </ElCol>
              <ElCol v-if="!fold" :span="8">
                <ElFormItem :label="t('exception_form.account')" prop="account">
                  <ElInput v-model="data.search.account" :placeholder="t('common.pleaseInput') + t('exception_form.account')" clearable @change="getDataList" />
                </ElFormItem>
              </ElCol>
              <ElCol :span="8">
                <ElFormItem :label="t('exception_form.paramsObj')" prop="paramsObj">
                  <ElInput v-model="data.search.paramsObj" :placeholder="t('common.pleaseInput') + t('exception_form.paramsObj')" clearable @change="getDataList" />
                </ElFormItem>
              </ElCol>
              <ElCol :span="4">
                <ElButton type="primary" @click="currentChange()">
                  <template #icon>
                    <FaIcon name="i-ep:search" />
                  </template>
                  {{ t('common.search') }}
                </ElButton>
                <ElButton type="info" @click="reset()">
                  <template #icon>
                    <FaIcon name="ep:refresh-left" />
                  </template>
                  {{ t('common.reset') }}
                </ElButton>
                <ElButton link @click="toggle">
                  <template #icon>
                    <FaIcon :name="fold ? 'i-ep:caret-bottom' : 'i-ep:caret-top' " />
                  </template>
                  {{ fold ? $t('common.expand') : $t('common.collapse') }}
                </ElButton>
              </ElCol>
            </ElRow>
          </ElForm>
        </template>
      </FaSearchBar>
      <ElDivider border-style="dashed" />
      <div class="vxe-table-div">
        <vxe-grid ref="xGrid" :data="data.dataList" v-bind="gridOptions" v-on="gridEvent">
          <template #empty>
            <span>
              <p>{{ $t('common.noData') }}</p>
            </span>
          </template>
          <template #name_filter="{ $panel, column }">
            <vxe-input v-for="(option, index) in column.filters" :key="index" v-model="option.data" type="text" :placeholder="$t('common.pleaseInput')" clearable @input="changeFilterEvent($event, option, $panel)" />
          </template>
          <template #opt_content="{ row }">
            <ElButton v-auth="`${routerName}.view`" type="primary" size="small" plain @click="onEdit(row)">
              <template #icon>
                <FaIcon name="i-ep:view" />
              </template>
              {{ $t('common.detail') }}
            </ElButton>
          </template>
          <template #toolbar_buttons>
            <div />
          </template>
        </vxe-grid>
      </div>
      <ElPagination v-if="pagination.total > 0" :current-page="pagination.page" style="margin-top: 10px;" :total="pagination.total" :page-size="pagination.size" :page-sizes="pagination.sizes" :layout="pagination.layout" :hide-on-single-page="false" class="pagination" background @size-change="sizeChange" @current-change="currentChange" />
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
