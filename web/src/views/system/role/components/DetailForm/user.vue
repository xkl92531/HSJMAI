<script setup lang="ts">
import api from '@/api/modules/system/role'
import user from '@/api/modules/user'
import messages from '@/views/system/user/components/lang'
import roleMessages from '../../lang'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'

const props = defineProps({
  id: {
    type: [Number, String],
    default: '',
  },
})
const { pagination, getParams, onSizeChange, onCurrentChange, onSortChange } = usePagination()
const { t, mergeLocaleMessage } = useI18n()
const router = useRouter()
const routerName = router.currentRoute.value.name.toString()
// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}
for (const locale in roleMessages) {
  mergeLocaleMessage(locale, roleMessages[locale])
}
// 搜索
const searchDefault = {
  name: '',
  roleId: props.id,
}
const search = ref({ ...searchDefault })

// 批量操作
const batch = ref({
  enable: true,
  selectionDataList: [],
})

// 列表
const loading = ref(false)
const dataList = ref([])

function getDataList() {
  loading.value = true
  const params = {
    ...getParams(),
    ...(search.value.roleId && { roleId: search.value.roleId }),
    ...(search.value.name && { name: search.value.name }),
  }
  api.getUserListPageByRole(params).then((res: any) => {
    loading.value = false
    dataList.value = res.data
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

onMounted(() => {
  getDataList()
})

const visible = ref(false)
const value = ref<string[]>([])
const userOptions = ref([] as { id: string | number, name: string }[])
const searchLoading = ref(false)
function remoteMethod(query: string) {
  if (query) {
    searchLoading.value = true
    user.details({ name: query }).then((res: any) => {
      userOptions.value = res
    }).finally(() => {
      searchLoading.value = false
    })
  }
  else {
    userOptions.value = []
  }
}

function onCreate() {
  value.value = []
  visible.value = true
}

function onSubmit() {
  if (value.value == null || value.value.length === 0) {
    ElMessage.error(t('role_form.userNotEmpty'))
    return
  }
  api.saveUser({ roleIds: [props.id], userIds: value.value }).then(() => {
    visible.value = false
    ElMessage.success(t('common.saveSuccess'))
    getDataList()
  })
}

function onDelete(row: any) {
  ElMessageBox.confirm(t('role_form.confirmDelUserWithName', { name: row.name }), t('role_form.statusConfirmTitle')).then(() => {
    api.deleteUser({ roleIds: [props.id], userIds: [row.id] }).then(() => {
      visible.value = false
      ElMessage.success(t('common.deleteSuccess'))
      getDataList()
    })
  })
}

function onSelectDelete() {
  if (batch.value.selectionDataList == null || batch.value.selectionDataList.length <= 0) {
    ElMessage.error(t('common.mustSelectOne'))
    return
  }
  ElMessageBox.confirm(t('role_form.confirmDelUserText'), t('role_form.statusConfirmTitle')).then(() => {
    let userIds = batch.value.selectionDataList.map((it: any) => it.id)
    api.deleteUser({ roleIds: [props.id], userIds }).then(() => {
      visible.value = false
      ElMessage.success(t('common.deleteSuccess'))
      getDataList()
    })
  })
}
</script>

<template>
  <FaPageMain>
    <FaSearchBar :show-toggle="false">
      <template #default="{ fold, toggle }">
        <ElForm :model="search" size="default" label-width="60px" class="search-form">
          <ElRow :gutter="20">
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
            </ElCol>
          </ElRow>
        </ElForm>
      </template>
    </FaSearchBar>
    <ElDivider border-style="dashed" />
    <ElSpace wrap>
      <ElButton v-auth="`${routerName}.addUser`" type="primary" size="default" @click="onCreate">
        <template #icon>
          <FaIcon name="i-ep:plus" />
        </template>
        {{ $t('common.create') }}
      </ElButton>
      <ElButton v-auth="`${routerName}.delUser`" size="default" type="danger" danger @click="onSelectDelete">
        <template #icon>
          <FaIcon name="i-ep:delete" />
        </template>{{ $t('common.delete') }}
      </ElButton>
    </ElSpace>
    <ElTable v-loading="loading" class="my-4" :data="dataList" stripe highlight-current-row border height="100%" @sort-change="sortChange" @selection-change="batch.selectionDataList = $event">
      <ElTableColumn v-if="batch.enable" type="selection" align="center" fixed />
      <ElTableColumn prop="name" :label="t('user_form.name')" width="100" align="center" />
      <ElTableColumn prop="account" :label="t('user_form.account')" />
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
      <ElTableColumn prop="positionName" :label="t('user_form.positionName')" width="120" align="center" />
      <ElTableColumn :label="t('user_form.status')" width="100" align="center">
        <template #default="scope">
          <ElTag v-if="scope.row.status === 1" type="success" size="small">
            {{ $t("common.normal") }}
          </ElTag>
          <ElTag v-if="scope.row.status === 2" type="danger" size="small">
            {{ $t("common.stopUsing") }}
          </ElTag>
        </template>
      </ElTableColumn>
      <ElTableColumn :label="t('common.operate')" width="100" align="center">
        <template #default="scope">
          <ElSpace>
            <ElButton v-auth="`${routerName}.delUser`" type="danger" size="small" plain @click="onDelete(scope.row)">
              {{ t('common.delete') }}
            </ElButton>
          </ElSpace>
        </template>
      </ElTableColumn>
    </ElTable>
    <ElPagination v-if="pagination.total > 0" :current-page="pagination.page" :total="pagination.total" :page-size="pagination.size" :page-sizes="pagination.sizes" :layout="pagination.layout" :hide-on-single-page="false" class="pagination" background @current-change="currentChange" @size-change="sizeChange" />
    <ElDialog v-model="visible" :title="t('role_form.addUser')" width="30%" :close-on-click-modal="false" append-to-body destroy-on-close>
      <el-select
        v-model="value"
        multiple
        filterable
        remote
        reserve-keyword
        :placeholder="t('role_form.searchText')"
        remote-show-suffix
        :remote-method="remoteMethod"
        :loading="searchLoading"
      >
        <el-option
          v-for="item in userOptions"
          :key="item.id"
          :label="item.name"
          :value="item.id"
        />
      </el-select>
      <template #footer>
        <ElButton size="large" @click="visible = false">
          {{ t('common.close') }}
        </ElButton>
        <ElButton type="primary" size="large" @click="onSubmit">
          {{ t("common.save") }}
        </ElButton>
      </template>
    </ElDialog>
  </FaPageMain>
</template>
