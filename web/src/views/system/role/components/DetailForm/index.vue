<script setup lang="ts">
import type { FormInstance, FormRules } from 'element-plus'
import type { DetailFormProps } from '../../types'
import api from '@/api/modules/system/role'
import { ElMessage } from 'element-plus'
import { useI18n } from 'vue-i18n'
import messages from '../../lang'

const props = withDefaults(defineProps<DetailFormProps>(), {
  id: '',
  name: '',
  groupID: '0',
  sort: 0,
  remarks: '',
  status: 1,
})

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
  groupID: 0,
  sort: 0,
  remarks: '',
  status: 1,
})
const formRules = ref<FormRules>({
  name: [
    {
      required: true,
      message: t('role_formRules.name'),
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
        formRef.value
        && formRef.value.validate((valid) => {
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
        formRef.value
        && formRef.value.validate((valid) => {
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
    <ElForm
      ref="formRef"
      :model="form"
      :rules="formRules"
      label-width="120px"
      label-suffix="："
    >
      <ElFormItem :label="t('role_form.name')" prop="name">
        <ElInput
          v-model="form.name"
          :placeholder="$t('common.pleaseInput') + t('role_form.name')"
          :maxlength="50"
          show-word-limit
        />
      </ElFormItem>
      <ElFormItem :label="t('role_form.remarks')" prop="remarks">
        <ElInput
          v-model="form.remarks"
          type="textarea"
          :placeholder="$t('common.pleaseInput') + t('role_form.remarks')"
          :show-word-limit="true"
          :maxlength="500"
        />
      </ElFormItem>
      <ElFormItem :label="t('role_form.status')" prop="status">
        <ElSwitch v-model="form.status" :active-value="1" :inactive-value="2" />
      </ElFormItem>
    </ElForm>
  </div>
</template>

<style lang="scss" scoped>
// scss
</style>
