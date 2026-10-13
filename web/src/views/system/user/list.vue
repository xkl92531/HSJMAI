<route lang="yaml">
meta:
  enabled: false
</route>

<script setup lang="ts">
import { ElMessage, ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import apiManager from '@/api/modules/user'
import eventBus from '@/utils/eventBus'
import FormMode from './components/FormMode/index.vue'
import Purview from './components/FormMode/purview.vue'
import messages from './components/lang'

defineOptions({
  name: 'UserList',
})
const { t, mergeLocaleMessage } = useI18n()
const { pagination, getParams, onSizeChange, onCurrentChange, onSortChange } = usePagination()
// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}
const router = useRouter()
const routerName = router.currentRoute.value.name.toString()
// 表格是否自适应高度
const tableAutoHeight = ref(false)

/**
 * 详情展示模式
 * router 路由跳转
 * dialog 对话框
 * drawer 抽屉
 */
const formMode = ref<'router' | 'dialog' | 'drawer'>('dialog')

// 详情
const formModeProps = ref({
  visible: false,
  id: '',
})

// 搜索
const searchDefault = {
  account: '',
  name: '',
  mobile: '',
  sex: '',
}
const search = ref({ ...searchDefault })
function searchReset() {
  Object.assign(search.value, searchDefault)
}

// 批量操作
const batch = ref({
  enable: true,
  selectionDataList: [],
})

// 列表
const loading = ref(false)
const dataList = ref([])

onMounted(() => {
  getDataList()
  if (formMode.value === 'router') {
    eventBus.on('get-data-list', () => {
      getDataList()
    })
  }
})

onBeforeUnmount(() => {
  if (formMode.value === 'router') {
    eventBus.off('get-data-list')
  }
})

function getDataList() {
  loading.value = true
  const params = {
    ...getParams(),
    ...(search.value.account && { account: search.value.account }),
    ...(search.value.name && { name: search.value.name }),
    ...(search.value.mobile && { mobile: search.value.mobile }),
    ...(search.value.sex !== '' && { sex: search.value.sex }),
  }
  apiManager.list(params).then((res: any) => {
    loading.value = false
    dataList.value = res.data
    dataList.value.forEach((item: any) => {
      item.statusLoading = false
    })
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
  formModeProps.value.id = ''
  formModeProps.value.visible = true
}

function onEdit(row: any) {
  if (!row) {
    if (batch.value.selectionDataList.length <= 0) {
      ElMessage.warning(t('common.onlySelectOne'))
      return
    }
    if (batch.value.selectionDataList.length > 1) {
      ElMessage.warning(t('common.onlySelectOne'))
      return
    }
    formModeProps.value.id = batch.value.selectionDataList[0].id
  }
  else {
    formModeProps.value.id = row.id
  }
  formModeProps.value.visible = true
}
function onDeleteSelect() {
  const ids = []
  if (batch.value.selectionDataList.length <= 0) {
    ElMessage.warning(t('common.onlySelectOne'))
    return
  }
  batch.value.selectionDataList.forEach((item: any) => {
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
    apiManager.batchDelete(ids).then(() => {
      ElMessage.success(t('common.deleteSuccess'))
      getDataList()
    })
  })
}
function onChangeStatus(row: any) {
  return new Promise<boolean>((resolve) => {
    ElMessageBox.confirm(t('user_form.statusConfirm', { action: row.status == 1 ? t('user_form.disable') : t('user_form.enable'), account: row.account }), t('user_form.statusConfirmTitle')).then(() => {
      row.statusLoading = true
      apiManager.updateStatus({
        ID: row.id,
        status: row.status == 1 ? 2 : 1,
      }).then(() => {
        row.statusLoading = false
        ElMessage.success({
          message: t('user_form.statusSuccess', { action: row.status == 2 ? t('user_form.enable') : t('user_form.disable') }),
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

function onResetPassword(row: any) {
  ElMessageBox.confirm(t('user_form.resetPasswordConfirm', { account: row.account }), t('user_form.statusConfirmTitle')).then(() => {
    apiManager.passwordReset({ ID: row.id, Password: '123456' }).then(() => {
      ElMessage.success({
        message: t('user_form.resetSuccess'),
        center: true,
      })
    })
  }).catch(() => {})
}

function onDel(row: any) {
  ElMessageBox.confirm(t('user_form.deleteAdminConfirm', { account: row.account }), t('user_form.statusConfirmTitle')).then(() => {
    apiManager.delete(row.id).then(() => {
      getDataList()
      ElMessage.success({
        message: t('common.deleteSuccess'),
        center: true,
      })
    })
  }).catch(() => {})
}

function handleMoreOperating(command: string, row: any) {
  switch (command) {
    case 'resetPassword':
      onResetPassword(row)
      break
    case 'delete':
      onDel(row)
      break
  }
}

const selectable = (row: any) => row.adminType !== 999
const roleFormModeProps = ref({
  visible: false,
  id: '',
})
function onRoleSelect() {
  roleFormModeProps.value.visible = true
}
</script>

<template>
  <div :class="{ 'absolute-container': tableAutoHeight }">
    <FaPageMain :class="{ 'flex-1 overflow-auto': tableAutoHeight }" :main-class="{ 'flex-1 flex flex-col overflow-auto': tableAutoHeight }">
      <FaSearchBar :show-toggle="false">
        <template #default="{ fold, toggle }">
          <ElForm :model="search" size="default" label-width="60px" class="search-form">
            <ElRow :gutter="20">
              <ElCol :span="6">
                <ElFormItem :label="t('user_form.account')">
                  <ElInput v-model="search.account" :placeholder="t('common.pleaseInput') + t('user_form.account')" clearable @keydown.enter="currentChange()" @clear="currentChange()" />
                </ElFormItem>
              </ElCol>
              <ElCol :span="6">
                <ElFormItem :label="t('user_form.name')">
                  <ElInput v-model="search.name" :placeholder="t('common.pleaseInput') + t('user_form.name')" clearable @keydown.enter="currentChange()" @clear="currentChange()" />
                </ElFormItem>
              </ElCol>
              <ElCol :span="6">
                <ElButton type="primary" @click="currentChange()">
                  <template #icon>
                    <FaIcon name="i-ep:search" />
                  </template>
                  {{ $t("common.search") }}
                </ElButton>
                <ElButton @click="searchReset(); currentChange()">
                  {{ $t("common.reset") }}
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
        <ElButton v-auth="`${routerName}.edit`" size="default" type="primary" plain danger :disabled="!batch.selectionDataList.length" @click="onEdit(null)">
          <template #icon>
            <FaIcon name="i-ep:edit" />
          </template> {{ $t('common.edit') }}
        </ElButton>
        <ElButton v-auth="`${routerName}.delete`" size="default" type="danger" danger :disabled="!batch.selectionDataList.length" @click="onDeleteSelect">
          <template #icon>
            <FaIcon name="i-ep:delete" />
          </template>{{ $t('common.delete') }}
        </ElButton>
        <ElButton
          v-auth="`${routerName}.role`" size="default" :disabled="!batch.selectionDataList.length"
          @click="onRoleSelect"
        >
          <template #icon>
            <FaIcon name="mdi:account-lock-outline" />
          </template>{{ t('user_form.roleAllocation') }}
        </ElButton>
      </ElSpace>
      <ElTable v-loading="loading" class="my-4" :data="dataList" stripe highlight-current-row border height="100%" @sort-change="sortChange" @selection-change="batch.selectionDataList = $event">
        <ElTableColumn v-if="batch.enable" type="selection" :selectable="selectable" align="center" fixed />
        <ElTableColumn prop="name" :label="t('user_form.name')" width="100" align="center" />
        <ElTableColumn prop="account" sortable :label="t('user_form.account')" />
        <ElTableColumn prop="sex" :label="t('user_form.sex')" width="100" align="center">
          <template #default="scope">
            <ElTag v-if="scope.row.sex === 2" type="success" size="small">
              {{ $t("common.man") }}
            </ElTag>
            <ElTag v-else-if="scope.row.sex === 1" type="warning" size="small">
              {{ $t("common.girl") }}
            </ElTag>
            <ElTag v-else type="info" size="small">
              {{ $t("common.secrecy") }}
            </ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn prop="mobilePhone" :label="t('user_form.mobilePhone')" width="150" align="center" />
        <ElTableColumn prop="eMail" :label="t('user_form.eMail')" width="180" align="left" show-overflow-tooltip />
        <ElTableColumn prop="positionName" :label="t('user_form.positionName')" width="120" align="center" />
        <ElTableColumn :label="t('user_form.status')" width="100" align="center">
          <template #default="scope">
            <ElSwitch v-model="scope.row.status" :loading="scope.row.statusLoading" inline-prompt :active-text="t('common.normal') " :active-value="1" :inactive-value="2" :inactive-text="t('common.stopUsing')" :before-change="() => onChangeStatus(scope.row)" />
          </template>
        </ElTableColumn>
        <ElTableColumn :label="t('common.operate')" width="200" align="center" fixed="right">
          <template #default="scope">
            <ElSpace>
              <ElButton type="primary" size="small" plain @click="onEdit(scope.row)">
                {{ t('common.edit') }}
              </ElButton>
              <ElDropdown v-auth="[`${routerName}.resetPassword`, `${routerName}.delete`]" @command="handleMoreOperating($event, scope.row)">
                <ElButton size="small">
                  {{ t('common.moreOperations') }}
                  <FaIcon name="i-ep:arrow-down" class="el-icon--right" />
                </ElButton>
                <template #dropdown>
                  <ElDropdownMenu>
                    <span v-auth="`${routerName}.resetPassword`">
                      <ElDropdownItem command="resetPassword">
                        {{ t('user_form.resetPassword') }}
                      </ElDropdownItem>
                    </span>
                    <span v-auth="`${routerName}.delete`">
                      <ElDropdownItem command="delete" divided>
                        {{ t('common.delete') }}
                      </ElDropdownItem>
                    </span>
                  </ElDropdownMenu>
                </template>
              </ElDropdown>
            </ElSpace>
          </template>
        </ElTableColumn>
      </ElTable>
      <ElPagination v-if="pagination.total > 0" :current-page="pagination.page" :total="pagination.total" :page-size="pagination.size" :page-sizes="pagination.sizes" :layout="pagination.layout" :hide-on-single-page="false" class="pagination" background @current-change="currentChange" @size-change="sizeChange" />
    </FaPageMain>
    <FormMode v-if="formMode === 'dialog' || formMode === 'drawer'" :id="formModeProps.id" v-model="formModeProps.visible" :mode="formMode" @success="getDataList" />
    <Purview
      v-if="formMode === 'dialog' || formMode === 'drawer'" :id="roleFormModeProps.id" v-model="roleFormModeProps.visible" :mode="formMode" :data="batch.selectionDataList"
      @success="getDataList"
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
