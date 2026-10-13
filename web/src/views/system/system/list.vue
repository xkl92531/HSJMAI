<route lang="yaml">
meta:
  title: 列表页
</route>

<script setup lang="ts">
import api from '@/api/modules/system/system'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import FormMode from './components/FormMode/index.vue'
import messages from './components/lang'

defineOptions({
  name: 'SystemList',
})

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}
const router = useRouter()
const routerName = router.currentRoute.value.name.toString()
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
    name: '',
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

function onEditSelect() {
  if (data.value.batch.selectionDataList.length > 1) {
    ElMessage.warning(t('common.onlySelectOne'))
    return
  }
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
  data.value.formModeProps.id = ''
  data.value.formModeProps.visible = true
}

function onEdit(row: any) {
  data.value.formModeProps.id = row.id
  data.value.formModeProps.visible = true
}

function onDel(row: any) {
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

function onChangeStatus(row: any) {
  return new Promise<boolean>((resolve) => {
    ElMessageBox.confirm(t('system_form.statusConfirm', { action: row.status == 1 ? t('system_form.disable') : t('system_form.enable'), name: row.name }), t('system_form.statusConfirmTitle')).then(() => {
      row.statusLoading = true
      api.updateStatus({
        ID: row.id,
        status: row.status == 1 ? 2 : 1,
      }).then(() => {
        row.statusLoading = false
        ElMessage.success({
          message: t('system_form.statusSuccess', { action: row.status == 2 ? t('system_form.enable') : t('system_form.disable') }),
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
  <div :class="{ 'absolute-container': data.tableAutoHeight }">
    <FaPageMain :class="{ 'flex-1 overflow-auto': data.tableAutoHeight }" :main-class="{ 'flex-1 flex flex-col overflow-auto': data.tableAutoHeight }">
      <FaSearchBar :show-toggle="false">
        <ElForm :model="data.search" size="default" label-width="100px" class="search-form">
          <ElRow :gutter="16">
            <ElCol :span="8">
              <ElFormItem :label="$t('common.keyword')">
                <ElInput
                  v-model="data.search.name"
                  :placeholder=" $t('common.pleaseInput') + $t('system_form.name')"
                  clearable
                  style="width: 100%;"
                  @keydown.enter="currentChange()"
                  @clear="currentChange()"
                />
              </ElFormItem>
            </ElCol>
            <ElCol :span="5">
              <ElButton type="primary" @click="currentChange()">
                <template #icon>
                  <FaIcon name="i-ep:search" />
                </template>
                {{ $t("common.search") }}
              </ElButton>
            </ElCol>
          </ElRow>
        </ElForm>
      </FaSearchBar>
      <ElDivider border-style="dashed" />
      <ElSpace wrap>
        <ElButton v-auth="`${routerName}.add`" type="primary" size="default" @click="onCreate">
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
        <ElTableColumn prop="name" :label="t('system_form.name')">
          <template #default="scope">
            {{ scope.row.name }}
          </template>
        </ElTableColumn>
        <ElTableColumn prop="remarks" :label="t('system_form.remarks')">
          <template #default="scope">
            {{ scope.row.remarks }}
          </template>
        </ElTableColumn>
        <ElTableColumn prop="status" :label="t('system_form.status')" width="80">
          <template #default="scope">
            <ElSwitch v-model="scope.row.status" :loading="scope.row.statusLoading" inline-prompt :active-text="t('common.normal') " :active-value="1" :inactive-value="2" :inactive-text="t('common.stopUsing')" :before-change="() => onChangeStatus(scope.row)" />
          </template>
        </ElTableColumn>
        <ElTableColumn prop="sort" align="center" :label="t('system_form.sort')" width="80">
          <template #default="scope">
            {{ scope.row.sort }}
          </template>
        </ElTableColumn>
        <ElTableColumn v-auth="['system.edit', 'system.delete']" :label="$t('common.operate')" width="150" align="center" fixed="right">
          <template #default="scope">
            <ElButton v-auth="`${routerName}.edit`" type="primary" size="small" plain @click="onEdit(scope.row)">
              {{ $t('common.edit') }}
            </ElButton>
            <ElButton v-auth="`${routerName}.delete`" type="danger" size="small" plain @click="onDel(scope.row)">
              {{ $t('common.delete') }}
            </ElButton>
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
