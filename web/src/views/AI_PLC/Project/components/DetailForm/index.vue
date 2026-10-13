<script setup lang="ts">
import type { FormInstance, FormRules } from 'element-plus'
import type { DetailFormProps } from '../../types'
import api from '@/api/modules/AI_PLC/project'
import { ElMessage } from 'element-plus'
import { useI18n } from 'vue-i18n'
import messages from '../lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const props = withDefaults(defineProps<DetailFormProps>(), {
  id: '',
  projectName: '',
  brand: '',
  model: '',
  version: '',
  sourcePath: '',
  sourceHash: '',
  status: 0,
  templateVersion: '',
  fileCount: 0,
  successCount: 0,
  failCount: 0,
  lastParseRunID: 0,
  remark: '',
  factoryID: 0,
  productionLineID: 0,
  workcellID: 0,
  productionLineCode: '',
})

const loading = ref(false)
const formRef = ref<FormInstance>()
const form = ref({
  id: props.id,
  projectName: '',
  brand: '',
  model: '',
  version: '',
  sourcePath: '',
  sourceHash: '',
  status: 0,
  templateVersion: '',
  fileCount: 0,
  successCount: 0,
  failCount: 0,
  lastParseRunID: 0,
  remark: '',
  factoryID: 0,
  productionLineID: 0,
  workcellID: 0,
  productionLineCode: '',
  iotUrl: '',
  plcIp: '',
  plcPort: null as number | null,
})

const formRules = ref<FormRules>({
  projectName: [
    { required: true, message: t('aiPlcProject_form.projectNamePlaceholder'), trigger: 'blur' },
  ],
  status: [
    { required: true, message: t('aiPlcProject_form.statusPlaceholder'), trigger: 'change' },
  ],
  fileCount: [
    { required: true, message: t('aiPlcProject_form.fileCountPlaceholder'), trigger: 'blur' },
  ],
  successCount: [
    { required: true, message: t('aiPlcProject_form.successCountPlaceholder'), trigger: 'blur' },
  ],
  failCount: [
    { required: true, message: t('aiPlcProject_form.failCountPlaceholder'), trigger: 'blur' },
  ],
})

onMounted(() => {
  if (form.value.id !== '') {
    getInfo()
  }
})

function getInfo() {
  loading.value = true
  api.detail(form.value.id).then((res: any) => {
    loading.value = false
    form.value = res
  })
}

defineExpose({
  submit() {
    return new Promise<void>((resolve) => {
      formRef.value && formRef.value.validate((valid) => {
        if (valid) {
          const promise = form.value.id === '' ? api.create(form.value) : api.edit(form.value)
          promise.then(() => {
            ElMessage.success({
              message: form.value.id === '' ? t('aiPlcProject_form.createSuccess') : t('aiPlcProject_form.editSuccess'),
              center: true,
            })
            resolve()
          })
        }
      })
    })
  },
})
</script>

<template>
  <div v-loading="loading">
    <ElForm
      ref="formRef"
      :model="form"
      :rules="formRules"
      label-width="120px"
      label-suffix="："
    >
      <ElFormItem :label="$t('aiPlcProject_form.projectName')" prop="projectName">
        <ElInput v-model="form.projectName" :placeholder="$t('aiPlcProject_form.projectNamePlaceholder')" maxlength="200" show-word-limit />
      </ElFormItem>
      <ElFormItem :label="$t('aiPlcProject_form.brand')" prop="brand">
        <ElInput v-model="form.brand" :placeholder="$t('aiPlcProject_form.brandPlaceholder')" maxlength="50" show-word-limit />
      </ElFormItem>
      <ElFormItem :label="$t('aiPlcProject_form.modelSeries')" prop="model">
        <ElInput v-model="form.model" :placeholder="$t('aiPlcProject_form.modelSeriesPlaceholder')" maxlength="50" show-word-limit />
      </ElFormItem>
      <ElFormItem :label="$t('aiPlcProject_form.projectVersion')" prop="version">
        <ElInput v-model="form.version" :placeholder="$t('aiPlcProject_form.projectVersionPlaceholder')" maxlength="50" show-word-limit />
      </ElFormItem>
      <ElFormItem :label="$t('aiPlcProject_form.sourcePath')" prop="sourcePath">
        <ElInput v-model="form.sourcePath" :placeholder="$t('aiPlcProject_form.sourcePathPlaceholder')" maxlength="500" show-word-limit />
      </ElFormItem>
      <ElFormItem :label="$t('aiPlcProject_form.status')" prop="status">
        <ElSelect v-model="form.status" :placeholder="$t('aiPlcProject_form.statusPlaceholder')">
          <ElOption :value="0" :label="$t('aiPlcProject_form.statusPendingParse')" />
          <ElOption :value="1" :label="$t('aiPlcProject_form.statusParsing')" />
          <ElOption :value="2" :label="$t('aiPlcProject_form.statusReady')" />
          <ElOption :value="3" :label="$t('aiPlcProject_form.statusFailed')" />
        </ElSelect>
      </ElFormItem>
      <ElFormItem :label="$t('aiPlcProject_form.templateVersion')" prop="templateVersion">
        <ElInput v-model="form.templateVersion" :placeholder="$t('aiPlcProject_form.templateVersionPlaceholder')" maxlength="50" show-word-limit />
      </ElFormItem>
      <ElRow :gutter="20">
        <ElCol :span="8">
           <ElFormItem :label="$t('aiPlcProject_form.fileCount')" prop="fileCount">
              <ElInputNumber v-model="form.fileCount" :min="0" />
           </ElFormItem>
        </ElCol>
        <ElCol :span="8">
           <ElFormItem :label="$t('aiPlcProject_form.successCount')" prop="successCount">
              <ElInputNumber v-model="form.successCount" :min="0" />
           </ElFormItem>
        </ElCol>
        <ElCol :span="8">
           <ElFormItem :label="$t('aiPlcProject_form.failCount')" prop="failCount">
              <ElInputNumber v-model="form.failCount" :min="0" />
           </ElFormItem>
        </ElCol>
      </ElRow>
      <ElFormItem :label="$t('aiPlcProject_form.remark')" prop="remark">
        <ElInput
          v-model="form.remark"
          type="textarea"
          :placeholder="$t('aiPlcProject_form.remarkPlaceholder')"
          maxlength="500"
          show-word-limit
        />
      </ElFormItem>
         <ElFormItem :label="$t('aiPlcProject_form.productionLineCode')" prop="productionLineCode">
        <ElInput v-model="form.productionLineCode" :placeholder="$t('aiPlcProject_form.productionLineCodePlaceholder')" maxlength="50" show-word-limit />
      </ElFormItem>
      <ElFormItem :label="$t('aiPlcProject_form.iotUrl')" prop="iotUrl">
        <ElInput v-model="form.iotUrl" :placeholder="$t('aiPlcProject_form.iotUrlPlaceholder')" maxlength="500" />
      </ElFormItem>
      <ElRow :gutter="20">
        <ElCol :span="16">
          <ElFormItem :label="$t('aiPlcProject_form.plcIp')" prop="plcIp">
            <ElInput v-model="form.plcIp" :placeholder="$t('aiPlcProject_form.plcIpPlaceholder')" maxlength="50" />
          </ElFormItem>
        </ElCol>
        <ElCol :span="8">
          <ElFormItem :label="$t('aiPlcProject_form.plcPort')" prop="plcPort">
            <ElInputNumber v-model="form.plcPort" :min="0" :max="65535" :step="1" step-strictly :placeholder="$t('aiPlcProject_form.plcPortPlaceholder')" style="width: 100%" />
          </ElFormItem>
        </ElCol>
      </ElRow>
    </ElForm>
  </div>
</template>

<style lang="scss" scoped>
// scss
</style>
