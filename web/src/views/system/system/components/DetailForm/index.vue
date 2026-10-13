<script setup lang="ts">
import type { FormInstance, FormRules } from 'element-plus'
import type { DetailFormProps } from '../../types'
import api from '@/api/modules/system/system'
import { ElCol, ElMessage, ElRow } from 'element-plus'
import { useI18n } from 'vue-i18n'
import messages from '../lang'

const props = withDefaults(
  defineProps<DetailFormProps>(),
  {
    id: '',
    name: '',
    remarks: '',
    status: 0,
    sort: 0,
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
  name: '',
  remarks: '',
  status: 1,
  sort: 1,
})
const formRules = ref<FormRules>({
  name: [
    {
      required: true,
      message: t('system_formRules.name'),
      trigger: 'blur',
    },
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
      <ElRow>
        <ElCol :span="24">
          <ElFormItem :label=" t('system_form.name')" prop="name">
            <ElInput v-model="form.name" :placeholder="$t('common.pleaseInput') + t('system_form.name')" :maxlength="200" show-word-limit />
          </ElFormItem>
        </ElCol>
        <ElCol :span="12">
          <ElFormItem :label="t('system_form.sort')" prop="sort">
            <ElInputNumber v-model="form.sort" :precision="0" :min="0" :max="9999999999" />
          </ElFormItem>
        </ElCol>
        <ElCol :span="12">
          <ElFormItem :label="t('system_form.status')" prop="status">
            <ElRadioGroup v-model="form.status">
              <ElRadioButton :value="1">
                {{ t('common.normal') }}
              </ElRadioButton>
              <ElRadioButton :value="2">
                {{ t('common.stopUsing') }}
              </ElRadioButton>
            </ElRadioGroup>
          </ElFormItem>
        </ElCol>
        <ElCol :span="24">
          <ElFormItem :label="t('system_form.remarks')" prop="remarks">
            <ElInput v-model="form.remarks" :placeholder="$t('common.pleaseInput') + t('system_form.remarks')" type="textarea" :maxlength="500" show-word-limit :rows="4" />
          </ElFormItem>
        </ElCol>
      </ElRow>
    </ElForm>
  </div>
</template>

<style lang="scss" scoped>
// scss
</style>
