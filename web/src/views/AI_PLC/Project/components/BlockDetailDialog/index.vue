<script setup lang="ts">
import { ElMessage } from 'element-plus'
import blockApi from '@/api/modules/AI_PLC/block'
import GraphStepTagPanel from '../GraphStepTagPanel.vue'
import { useI18n } from 'vue-i18n'
import messages from '../lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const props = defineProps<{
  projectId: number | string
  projectName?: string
}>()

const visible = defineModel<boolean>({
  default: false,
})

const { pagination, getParams, onSizeChange, onCurrentChange } = usePagination()

const data = ref({
  loading: false,
  search: {
    BlockName: '',
    Status: null as number | null,
   },
  dataList: [] as any[],
})

const statusOptions = [
  { value: 0, label: t('aiPlcProject_form.statusPendingCheck') },
  { value: 1, label: t('aiPlcProject_form.statusChecked') },
  { value: 2, label: t('aiPlcProject_form.statusCheckFailed') },
]

watch(visible, (val) => {
  if (val && props.projectId) {
    getDataList()
  }
})

function getDataList() {
  data.value.loading = true
  const params = {
    ...getParams(),
    ProjectID: props.projectId,
    BlockName: data.value.search.BlockName || undefined,
    Status: data.value.search.Status ?? undefined,
  }
  blockApi.list(params).then((res: any) => {
    data.value.loading = false
    data.value.dataList = res.data || []
    pagination.value.total = res.total || 0
  }).catch(() => {
    data.value.loading = false
    ElMessage.error(t('aiPlcProject_form.getDataFailed'))
  })
}

function onSearch() {
  pagination.value.page = 1
  getDataList()
}

function sizeChange(size: number) {
  onSizeChange(size).then(() => getDataList())
}

function currentChange(page: number) {
  onCurrentChange(page).then(() => getDataList())
}

function onClose() {
  visible.value = false
  data.value.dataList = []
  onReset()
}

function onReset() {
  data.value.search.BlockName = ''
  data.value.search.Status = null
  // pagination.value.page = 1
  // getDataList()
}
</script>

<template>
  <ElDialog
    v-model="visible"
    :title="$t('aiPlcProject_form.dialogTitle', { name: projectName || '' })"
    width="90%"
    :close-on-click-modal="false"
    append-to-body
    destroy-on-close
    @closed="onClose"
  >
    <div v-loading="data.loading">
      <!-- GRAPH 步位注册台账 -->
      <ElCollapse class="mb-4">
        <ElCollapseItem name="graphStepTag">
          <template #title>
            <span style="font-weight: 600">{{ $t('aiPlcProject_form.graphStepReg') }}</span>
            <ElTag size="small" type="info" effect="plain" style="margin-left: 8px">
              {{ $t('aiPlcProject_form.execBitsLedger') }}
            </ElTag>
          </template>
          <GraphStepTagPanel :project-id="projectId" />
        </ElCollapseItem>
      </ElCollapse>

      <!-- 搜索栏 -->
      <ElForm inline class="mb-4">
        <ElFormItem :label="$t('aiPlcProject_form.fileName')">
          <ElInput
            v-model="data.search.BlockName"
            :placeholder="$t('aiPlcProject_form.fileNamePlaceholder')"
            clearable
            @keydown.enter="onSearch"
            @clear="onSearch"
          />
        </ElFormItem>
        <ElFormItem :label="$t('aiPlcProject_form.status')">
          <ElSelect
            v-model="data.search.Status"
            :placeholder="$t('aiPlcProject_form.statusPlaceholder')"
            clearable
            style="width: 150px"
          >
            <ElOption
              v-for="item in statusOptions"
              :key="item.value"
              :label="item.label"
              :value="item.value"
            />
          </ElSelect>
        </ElFormItem>
        <ElFormItem>
          <ElButton type="primary" @click="onSearch">
            <FaIcon name="i-ep:search" /> {{ $t('aiPlcProject_form.search') }}
          </ElButton>
          <ElButton @click="onReset">
            <FaIcon name="i-ep:refresh" /> {{ $t('common.reset') }}
          </ElButton>
        </ElFormItem>
      </ElForm>

      <!-- 数据表格 -->
      <ElTable
        :data="data.dataList"
        stripe
        border
        max-height="400"
      >
        <ElTableColumn prop="blockName" :label="$t('aiPlcProject_form.blockName')"  width="260" show-overflow-tooltip />
        <ElTableColumn prop="blockType" :label="$t('aiPlcProject_form.blockType')" width="100" align="center" />
        <ElTableColumn prop="blockNumber" :label="$t('aiPlcProject_form.blockNumber')" width="100" align="center" />
        <ElTableColumn prop="language" :label="$t('aiPlcProject_form.language')" width="100" align="center" />
        <ElTableColumn prop="version" :label="$t('aiPlcProject_form.version')" width="100" align="center" />
        <ElTableColumn prop="status" :label="$t('aiPlcProject_form.status')" width="100" align="center">
          <template #default="scope">
            <ElTag v-if="scope.row.status === 0" type="info">{{ $t('aiPlcProject_form.statusPendingCheck') }}</ElTag>
            <ElTag v-else-if="scope.row.status === 1" type="success">{{ $t('aiPlcProject_form.statusChecked') }}</ElTag>
            <ElTag v-else-if="scope.row.status === 2" type="danger">{{ $t('aiPlcProject_form.statusCheckFailed') }}</ElTag>
            <span v-else>{{ scope.row.status }}</span>
          </template>
        </ElTableColumn>
        <ElTableColumn prop="rawFilePath" :label="$t('aiPlcProject_form.filePath')" min-width="200" show-overflow-tooltip />
        <ElTableColumn prop="createTime" :label="$t('common.createTime')" width="180" align="center" />
      </ElTable>

      <!-- 分页 -->
      <ElPagination
        v-if="pagination.total > 0"
        class="mt-4"
        :current-page="pagination.page"
        :total="pagination.total"
        :page-size="pagination.size"
        :page-sizes="pagination.sizes"
        :layout="pagination.layout"
        background
        @size-change="sizeChange"
        @current-change="currentChange"
      />
    </div>
    <template #footer>
      <ElButton size="large" @click="onClose">
        {{ $t('common.close') }}
      </ElButton>
    </template>
  </ElDialog>
</template>
