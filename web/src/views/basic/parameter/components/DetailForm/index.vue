<script setup lang="ts">
import type { FormInstance, FormRules } from 'element-plus'
import type { DetailFormProps } from '../../types'
import api from '@/api/modules/Basic/SystemParam'
import { ElMessage } from 'element-plus'
import { useI18n } from 'vue-i18n'
import messages from '../lang'

const props = withDefaults(
  defineProps<DetailFormProps>(),
  {
    id: '',
    paramCode: '',
    paramName: '',
    paramValue: '',
    remarks: '',
    status: 1,
  },
)

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const editDisabled = ref(false)
const loading = ref(false)
const formRef = ref<FormInstance>()
const form = ref({
  id: props.id,
  paramCode: '',
  paramName: '',
  paramValue: '',
  remarks: '',
  status: 1,
})
const formRules = ref<FormRules>({
  paramCode: [
    {
      required: true,
      message: t('systemParam_formRules.paramCode'),
      trigger: 'blur',
    },
  ],
  paramName: [
    {
      required: true,
      message: t('systemParam_formRules.paramName'),
      trigger: 'blur',
    },
  ],
  paramValue: [
    {
      required: true,
      message: t('systemParam_formRules.paramValue'),
      trigger: 'blur',
    },
  ],
})

onMounted(() => {
  if (form.value.id !== '') {
    getInfo()
    editDisabled.value = true
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
      if (form.value.id === '') {
        formRef.value && formRef.value.validate((valid) => {
          if (valid) {
            api.create(form.value).then(() => {
              ElMessage.success({
                message: t('common.createSuccess'),
                center: true,
              })
              resolve()
            })
          }
        })
      }
      else {
        formRef.value && formRef.value.validate((valid) => {
          if (valid) {
            api.edit(form.value).then(() => {
              ElMessage.success({
                message: t('common.updateSuccess'),
                center: true,
              })
              resolve()
            })
          }
        })
      }
    })
  },
})
</script>

<template>
  <div v-loading="loading">
    <ElForm ref="formRef" :model="form" :rules="formRules" label-width="120px" label-suffix="：">
      <ElFormItem :label="t('systemParam_form.paramCode')" prop="paramCode">
        <ElInput v-model="form.paramCode" :disabled="editDisabled" :placeholder="t('systemParam_form.paramCode')" :maxlength="90" show-word-limit />
      </ElFormItem>
      <ElFormItem :label="t('systemParam_form.paramName')" prop="paramName">
        <ElInput v-model="form.paramName" :placeholder="t('systemParam_form.paramName')" :maxlength="90" show-word-limit />
      </ElFormItem>
      <ElFormItem :label="t('systemParam_form.paramValue')" prop="paramValue">
        <ElInput v-model="form.paramValue" type="textarea" :rows="4" :placeholder="t('systemParam_form.paramValue')" show-word-limit />
      </ElFormItem>
      <ElFormItem :label="t('systemParam_form.remarks')" prop="remarks">
        <ElInput v-model="form.remarks" type="textarea" :maxlength="500" :rows="4" show-word-limit :placeholder="t('systemParam_form.remarks')" />
      </ElFormItem>
      <ElFormItem :label="t('systemParam_form.status')" prop="status">
        <ElRadioGroup v-model="form.status">
          <ElRadioButton :value="1">
            {{ t('common.normal') }}
          </ElRadioButton>
          <ElRadioButton :value="2">
            {{ t('common.stopUsing') }}
          </ElRadioButton>
        </ElRadioGroup>
      </ElFormItem>
    </ElForm>
  </div>
</template>

<style lang="scss" scoped>
// scss
</style>
