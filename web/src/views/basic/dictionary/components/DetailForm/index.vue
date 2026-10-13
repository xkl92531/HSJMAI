<script setup lang="ts">
import type { FormInstance, FormRules } from 'element-plus'
import type { DetailFormProps } from '../../types'
import api from '@/api/modules/Basic/DataDictionary'
import { ElMessage } from 'element-plus'
import { useI18n } from 'vue-i18n'
import messages from '../lang'

const props = withDefaults(
  defineProps<DetailFormProps>(),
  {
    id: '',
    code: '',
    name: '',
    parentCode: '',
    parentName: '',
    shorterForm: '',
    sort: 0,
    remarks: '',
    status: 1,
  },
)

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}
const loading = ref(false)
const formRef = ref<FormInstance>()
const form = ref({
  id: props.id,
  code: '',
  name: '',
  parentCode: props.parentCode,
  parentName: '',
  shorterForm: '',
  sort: 0,
  remarks: '',
  status: 1,
})
const formRules = ref<FormRules>({
  code: [
    {
      required: true,
      message: t('data_formRules.code'),
      trigger: 'blur',
    },
  ],
  name: [
    {
      required: true,
      message: t('data_formRules.name'),
      trigger: 'blur',
    },
  ],
  sort: [
    {
      required: true,
      message: t('data_formRules.sort'),
      trigger: 'blur',
    },
  ],
})

onMounted(() => {
  if (form.value.id !== '') {
    getInfo()
  }
  else {
    api.getSort((form.value.parentCode == '' || form.value.parentCode == null ? '0' : form.value.parentCode)).then((res: any) => {
      form.value.sort = res
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
      <ElFormItem :label="t('data_form.code')" prop="code">
        <ElInput v-model="form.code" :placeholder="t('common.pleaseInput') + t('data_form.code')" :maxlength="50" show-word-limit />
      </ElFormItem>
      <ElFormItem :label="t('data_form.name')" prop="name">
        <ElInput v-model="form.name" :placeholder="t('common.pleaseInput') + t('data_form.name')" :maxlength="200" show-word-limit />
      </ElFormItem>
      <ElFormItem :label="t('data_form.shorterForm')" prop="shorterForm">
        <ElInput v-model="form.shorterForm" :placeholder="t('common.pleaseInput') + t('data_form.shorterForm')" :maxlength="200" show-word-limit />
      </ElFormItem>
      <ElFormItem :label="t('data_form.sort')" prop="sort">
        <ElInputNumber v-model="form.sort" :precision="0" :min="0" :max="9999999999" />
      </ElFormItem>
      <ElFormItem :label="t('data_form.remarks')" prop="remarks">
        <ElInput
          v-model="form.remarks" type="textarea" :rows="3" :maxlength="500" show-word-limit
          :placeholder="t('common.pleaseInput') + t('data_form.remarks')"
        />
      </ElFormItem>
      <ElFormItem :label="t('data_form.status')" prop="status">
        <ElSwitch v-model="form.status" inline-prompt active-text="正常" :active-value="1" :inactive-value="2" inactive-text="停用" />
      </ElFormItem>
    </ElForm>
  </div>
</template>

<style lang="scss" scoped>
// scss
</style>
