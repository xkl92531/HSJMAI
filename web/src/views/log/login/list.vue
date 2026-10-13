<route lang="yaml">
meta:
  title: 列表页
</route>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import api from '@/api/modules/log/login'
import eventBus from '@/utils/eventBus'
import FormMode from './components/FormMode/index.vue'
import messages from './components/lang'
import vxeTableConfig from './components/tableConfig'

// vxe-table

defineOptions({
  name: 'LoginLog',
})

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const { pagination, getParams, onSizeChange, onCurrentChange, onSortChange } = usePagination()

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
  if (data.value.formMode === 'router') {
    eventBus.on('get-data-list', () => {
      getDataList()
    })
  }
})

function getDataList() {
  gridOptions.loading = true
  const params = Object.assign(getParams(), data.value.search)
  api.list(params).then((res: any) => {
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

/**
 * 重置
 */
function reset() {
  data.value.search = {
    keyword: '',
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
            <ElRow :gutter="16">
              <ElCol :span="10">
                <ElFormItem :label="t('common.keyword')">
                  <ElInput v-model="data.search.keyword" :placeholder="`${t('common.pleaseInput') + t('login_form.empName')}、${t('login_form.account')}`" clearable />
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
          <template #toolbar_buttons />
        </vxe-grid>
      </div>
      <ElPagination :current-page="pagination.page" style="margin-top: 10px;" :total="pagination.total" :page-size="pagination.size" :page-sizes="pagination.sizes" :layout="pagination.layout" :hide-on-single-page="false" class="pagination" background @size-change="sizeChange" @current-change="currentChange" />
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
