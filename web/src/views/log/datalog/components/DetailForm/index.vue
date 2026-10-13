<script setup lang="ts">
import type { DetailFormProps } from '../../types'
import apiDetail from '@/api/modules/log/dataDetail'
import api from '@/api/modules/log/dataLog'
import { ElCol, ElRow, ElTable, ElTabPane, ElTabs, ElTag } from 'element-plus'
import { useI18n } from 'vue-i18n'
import messages from '../lang'

const props = withDefaults(
  defineProps<DetailFormProps>(),
  {
    id: '',
    diffType: '',
    tableName: '',
    tableDesc: '',
    logContent: '',
  },
)

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const loading = ref(false)
const form = ref({
  id: props.id,
  diffType: '',
  tableName: '',
  tableDesc: '',
  logContent: '',
  createrName: '',
  createTime: '',
})

const tableData = ref()

onMounted(() => {
  if (form.value.id !== '') {
    getInfo()
    apiDetail.details({ DataLogID: form.value.id }).then((res: any) => {
      tableData.value = res
    })
  }
})

function getInfo() {
  loading.value = true
  api.detail(form.value.id).then((res: any) => {
    loading.value = false
    form.value = res
  })
}
</script>

<template>
  <div v-loading="loading">
    <ElForm ref="formRef" :model="form" label-width="120px" label-suffix="：">
      <ElRow>
        <ElCol :span="12">
          <ElFormItem :label="t('dataLog_form.tableName')">
            {{ form.tableName }}
          </ElFormItem>
        </ElCol>
        <ElCol :span="12">
          <ElFormItem :label="t('dataLog_form.tableDesc')">
            {{ form.tableDesc }}
          </ElFormItem>
        </ElCol>

        <ElCol :span="12">
          <ElFormItem :label="t('dataLog_form.operationBy')">
            {{ form.createrName }}
          </ElFormItem>
        </ElCol>
        <ElCol :span="12">
          <ElFormItem :label="t('dataLog_form.operationTime')">
            {{ form.createTime }}
          </ElFormItem>
        </ElCol>
        <ElCol :span="12">
          <ElFormItem :label="t('dataLog_form.diffType')">
            <template v-if=" form.diffType === 'DELETE'">
              <ElTag type="danger">
                {{ form.diffType }}
              </ElTag>
            </template>
            <template v-else-if=" form.diffType === 'UPDATE'">
              <ElTag type="success">
                {{ form.diffType }}
              </ElTag>
            </template>
            <template v-else>
              <ElTag>
                {{ form.diffType }}
              </ElTag>
            </template>
          </ElFormItem>
        </ElCol>
        <ElCol :span="24">
          <ElFormItem :label="t('dataLog_form.logContent')" prop="logContent">
            <ElInput v-model="form.logContent" type="textarea" :rows="5" readonly :placeholder="t('dataLog_form.logContent')" />
          </ElFormItem>
        </ElCol>
        <ElCol :span="24">
          <ElTabs type="border-card">
            <ElTabPane :label="t('dataLog_form.changerecord')">
              <ElTable :data="tableData" style="width: 100%;" height="300" stripe show-overflow-tooltip border>
                <el-table-column prop="columnDesc" :label="t('dataLog_form.fieldDescription')" width="150" />
                <el-table-column prop="columnField" :label="t('dataLog_form.fieldName')" width="150" />
                <el-table-column prop="oldValue" :label="t('dataLog_form.oldValue')" />
                <el-table-column prop="newValue" :label="t('dataLog_form.newValue')" />
                <el-table-column prop="createTime" :label="t('dataLog_form.time')" />
              </ElTable>
            </ElTabPane>
          </ElTabs>
        </ElCol>
      </ElRow>
    </ElForm>
  </div>
</template>

<style lang="scss" scoped>
// scss
</style>
