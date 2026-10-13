<route lang="yaml">
meta:
  title: 列表页
</route>

<script setup lang="ts">
import { ElMessage, ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import api from '@/api/modules/Basic/DataDictionary'
import useSettingsStore from '@/store/modules/settings'
import eventBus from '@/utils/eventBus'
import FormMode from './components/FormMode/index.vue'
import messages from './components/lang'

defineOptions({
  name: 'DataDictionaryList',
})
const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const router = useRouter()
const routerName = ref(router.currentRoute.value.name.toString())
const { pagination, getParams, onSizeChange, onCurrentChange, onSortChange } = usePagination()

const dictionaryRef = ref()
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
    parentCode: '',
  },
  // 搜索
  search: {
    code: '',
    name: '',
    status: 0,
    parentCode: '',
    keyword: '',
    key: '',
    isTree: true,
  },
  // 批量操作
  batch: {
    enable: true,
    selectionDataList: [],
  },
  // 列表数据
  dataList: [],
  treeList: [],
})

onMounted(() => {
  // getDataList()
  // if (data.value.formMode === 'router') {
  //   eventBus.on('get-data-list', () => {
  //     getDataList()
  //   })
  // }
  getTreeList()
})

onBeforeUnmount(() => {
  if (data.value.formMode === 'router') {
    eventBus.off('get-data-list')
  }
})

function getTreeList() {
  data.value.search.parentCode = ''
  data.value.dataList = []
  api.details({ parentCode: '0', treeKeyword: data.value.search.key }).then((res: any) => {
    data.value.loading = false
    data.value.treeList = res
  })
}

function onDeleteSelect() {
  const ids = []
  data.value.batch.selectionDataList.forEach((item: any) => {
    ids.push(item.id)
  })
  if (ids.length <= 0) {
    ElMessage.warning(t('common.mustSelectOne'))
    return
  }
  data.value.search.isTree = false
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

function onEditSelect() {
  if (data.value.batch.selectionDataList.length > 1) {
    ElMessage.warning(t('common.onlySelectOne'))
    return
  }
  data.value.search.isTree = false
  data.value.formModeProps.id = data.value.batch.selectionDataList[0].id
  data.value.formModeProps.visible = true
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

function onCreate() {
  data.value.search.isTree = true
  data.value.formModeProps.id = ''
  data.value.formModeProps.visible = true
  data.value.formModeProps.parentCode = ''
}

function onEdit(row: any, isTree = false) {
  data.value.formModeProps.id = row.id
  data.value.formModeProps.parentCode = row.parentCode
  data.value.search.isTree = isTree
  data.value.formModeProps.visible = true
}

function onDel(row: any, isTree = false) {
  data.value.search.isTree = isTree
  ElMessageBox.confirm(`${t('common.confirmDel')}`, t('common.tip')).then(() => {
    api.delete(row.id).then(() => {
      getDataList()
      ElMessage.success({
        message: t('common.deleteSuccess'),
        center: true,
      })
    })
  }).catch(() => {})
}

function onTreeDel(row: any) {
  data.value.search.isTree = true
  ElMessageBox.confirm(`${t('common.confirmDel')}`, t('common.tip')).then(() => {
    api.delete(row.id).then(() => {
      getTreeList()
      ElMessage.success({
        message: t('common.deleteSuccess'),
        center: true,
      })
    })
  }).catch(() => {})
}

function onNodeClick(node: any) {
  data.value.search.parentCode = node.code
  getDataList()
}

function onAddSub(row?: any) {
  let code = ''
  if (row) {
    code = row.code
  }
  else {
    if (data.value.search.parentCode === '0'
      || data.value.search.parentCode.length <= 0
      || data.value.search.parentCode === undefined) {
      ElMessage.warning(t('common.mustSelectOne'))
      return
    }
    code = data.value.search.parentCode
  }
  data.value.formModeProps.id = ''
  data.value.formModeProps.parentCode = code
  data.value.search.isTree = false
  data.value.formModeProps.visible = true
}

function search() {
  if (!data.value.search.isTree) {
    getDataList()
  }
  else {
    getTreeList()
  }
}

function dictionaryFilter(value: string, data: any) {
  if (!value) {
    return true
  }
  return data.name.includes(value) || data.code.includes(value)
}
watch(() => data.value.search.key, (val) => {
  dictionaryRef.value?.filter(val)
})

function onChangeStatus(row: any) {
  return new Promise<boolean>((resolve) => {
    ElMessageBox.confirm(t('data_form.statusConfirm', { action: row.status == 1 ? t('data_form.disable') : t('data_form.enable'), name: row.name }), t('data_form.statusConfirmTitle')).then(() => {
      row.statusLoading = true
      api.updateStatus({
        ID: row.id,
        status: row.status == 1 ? 2 : 1,
      }).then(() => {
        row.statusLoading = false
        ElMessage.success({
          message: t('data_form.statusSuccess', { action: row.status == 2 ? t('data_form.enable') : t('data_form.disable') }),
          center: true,
        })
        return resolve(true)
      }).catch(() => {
        row.statusLoading = false
        return resolve(false)
      })
    }).catch(() => {
      return resolve(false)
    })
  })
}
</script>

<template>
  <div class="absolute size-full flex flex-col">
    <div class="flex-1 overflow-auto">
      <FaLayoutContainer hide-left-side-toggle class="static" left-side-class="h-full flex flex-col gap-4" default-class="flex flex-col flex-1 gap-4">
        <template #leftSide>
          <ElButtonGroup class="inline-flex">
            <ElButton type="primary" class="w-full" @click="onCreate()">
              {{ $t('data_form.addDictionary') }}
            </ElButton>
            <ElButton @click="getTreeList">
              <template #icon>
                <FaIcon name="i-ep:refresh" />
              </template>
            </ElButton>
          </ElButtonGroup>
          <ElInput v-model="data.search.key" :placeholder="t('data_form.searchText')" clearable />
          <ElScrollbar class="tree">
            <ElTree ref="dictionaryRef" :data="data.treeList" :props="{ label: 'name' }" :filter-node-method="dictionaryFilter as any" default-expand-all @node-click="onNodeClick">
              <template #default="{ node, data }">
                <div class="custom-tree-node">
                  <div class="label" :title="node.label">
                    {{ node.label }}
                  </div>
                  <div class="code">
                    {{ data.code }}
                  </div>
                  <div class="actions">
                    <ElButtonGroup>
                      <ElButton type="primary" plain size="default" @click.stop="onAddSub(data)">
                        <template #icon>
                          <FaIcon name="i-ep:plus" />
                        </template>
                      </ElButton>
                      <ElButton type="info" plain size="default" @click.stop="onEdit(data, true)">
                        <template #icon>
                          <FaIcon name="i-ep:edit" />
                        </template>
                      </ElButton>
                      <ElButton type="danger" plain size="default" @click.stop="onTreeDel(data)">
                        <template #icon>
                          <FaIcon name="i-ep:delete" />
                        </template>
                      </ElButton>
                    </ElButtonGroup>
                  </div>
                </div>
              </template>
            </ElTree>
          </ElScrollbar>
        </template>
        <div v-if="data.search.parentCode !== ''" class="contents">
          <ElSpace wrap>
            <FaSearchBar :show-toggle="false">
              <ElForm :model="data.search" size="default" label-width="100px" inline-message inline class="search-form">
                <ElFormItem :label="$t('common.keyword')">
                  <ElInput
                    v-model="data.search.keyword" :placeholder="`${t('common.pleaseInput') + t('data_form.name')}、${t('data_form.code')}${t('data_form.fuzzyQuery')}`"
                    clearable style="width: 400px;" @keydown.enter="currentChange()" @clear="currentChange()"
                  />
                </ElFormItem>
                <ElFormItem>
                  <ElButton type="primary" @click="currentChange()">
                    <template #icon>
                      <FaIcon name="i-ep:search" />
                    </template>
                    {{ $t('common.search') }}
                  </ElButton>
                </ElFormItem>
              </ElForm>
            </FaSearchBar>
          </ElSpace>
          <ElSpace wrap>
            <ElButton v-auth="`${routerName}.add`" type="primary" size="default" @click="onAddSub()">
              <template #icon>
                <FaIcon name="i-ep:plus" />
              </template>
              {{ $t('common.create') }}
            </ElButton>
            <ElButton v-auth="`${routerName}.edit`" size="default" type="primary" plain danger :disabled="!data.batch.selectionDataList.length" @click="onEditSelect">
              <template #icon>
                <FaIcon name="i-ep:edit" />
              </template> {{ $t('common.edit') }}
            </ElButton>
            <ElButton v-auth="`${routerName}.delete`" size="default" type="danger" danger :disabled="!data.batch.selectionDataList.length" @click="onDeleteSelect">
              <template #icon>
                <FaIcon name="i-ep:delete" />
              </template>{{ $t('common.delete') }}
            </ElButton>
          </ElSpace>
          <ElTable v-loading="data.loading" class="my-4" :data="data.dataList" stripe highlight-current-row border height="100%" @sort-change="sortChange" @selection-change="data.batch.selectionDataList = $event">
            <ElTableColumn v-if="data.batch.enable" type="selection" align="center" fixed />
            <ElTableColumn prop="code" :label="t('data_form.code')">
              <template #default="scope">
                {{ scope.row.code }}
              </template>
            </ElTableColumn>
            <ElTableColumn prop="name" :label="t('data_form.name')">
              <template #default="scope">
                {{ scope.row.name }}
              </template>
            </ElTableColumn>
            <ElTableColumn prop="shorterForm" :label="t('data_form.shorterForm')">
              <template #default="scope">
                {{ scope.row.shorterForm }}
              </template>
            </ElTableColumn>
            <ElTableColumn prop="sort" :label="t('data_form.sort')" width="80" align="center">
              <template #default="scope">
                {{ scope.row.sort }}
              </template>
            </ElTableColumn>
            <ElTableColumn prop="status" :label="t('data_form.status')" width="80" align="center">
              <template #default="scope">
                <ElSwitch v-model="scope.row.status" :loading="scope.row.statusLoading" inline-prompt :active-text="t('common.normal') " :active-value="1" :inactive-value="2" :inactive-text="t('common.stopUsing')" :before-change="() => onChangeStatus(scope.row)" />
                <!-- <ElTag v-if="scope.row.status === 1">
                  {{ t('common.normal') }}
                </ElTag>
                <ElTag v-if="scope.row.status === 2" type="danger">
                  {{ t('common.stopUsing') }}
                </ElTag> -->
              </template>
            </ElTableColumn>
            <ElTableColumn v-auth="[`${routerName}.edit`, `${routerName}.delete`]" :label="$t('common.operate')" width="160" align="center" fixed="right">
              <template #default="scope">
                <ElButton v-auth="`${routerName}.edit`" type="primary" size="small" plain @click="onEdit(scope.row, false)">
                  {{ $t('common.edit') }}
                </ElButton>
                <ElButton v-auth="`${routerName}.delete`" type="danger" size="small" plain @click="onDel(scope.row, false)">
                  {{ $t('common.delete') }}
                </ElButton>
              </template>
            </ElTableColumn>
          </ElTable>
          <ElPagination v-if="pagination.total > 0" :current-page="pagination.page" :total="pagination.total" :page-size="pagination.size" :page-sizes="pagination.sizes" :layout="pagination.layout" :hide-on-single-page="false" class="pagination" background @size-change="sizeChange" @current-change="currentChange" />
        </div>
        <div v-else class="flex-center flex-1 text-3xl text-primary/50">
          {{ $t('data_form.text') }}
        </div>
      </FaLayoutContainer>
    </div>
    <FormMode
      v-if="data.formMode === 'dialog' || data.formMode === 'drawer'" :id="data.formModeProps.id"
      v-model="data.formModeProps.visible" :mode="data.formMode" :parent-code="data.formModeProps.parentCode"
      @success="search"
    />
  </div>
</template>

<style scoped>
:deep(.tree) {
  flex: 1;
  overflow-y: auto;

  .el-tree {
    .el-tree-node__content {
      height: 60px;
    }

    .is-current > .el-tree-node__content {
      background-color: var(--el-color-primary-light-9);
    }

    .custom-tree-node {
      position: relative;
      display: flex;
      flex: 1;
      flex-direction: column;
      justify-content: center;
      width: 0;
      height: 100%;

      .label {
        width: calc(100% - 10px);
        overflow: hidden;
        text-overflow: ellipsis;
        color: var(--el-text-color-primary);
        white-space: nowrap;
      }

      .code {
        width: calc(100% - 10px);
        overflow: hidden;
        text-overflow: ellipsis;
        color: var(--el-text-color-placeholder);
        white-space: nowrap;
      }

      &:hover {
        .actions {
          display: block;
        }
      }

      .actions {
        position: absolute;
        top: 50%;
        right: 10px;
        display: none;
        transform: translateY(-50%);

        .el-button {
          padding: 5px 8px;
        }
      }
    }
  }
}
</style>
