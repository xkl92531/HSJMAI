<script setup lang="ts">
import type { FormInstance, FormRules } from 'element-plus'
import { ElMessage } from 'element-plus'
import { useI18n } from 'vue-i18n'
import type { DetailFormProps } from '../../types'
import messages from '../lang'
import api from '@/api/modules/log/login'

const props = withDefaults(
  defineProps<DetailFormProps>(),
  {
    id: '',
    userID: 0,
    account: '',
    loginIP: '',
    loginType: '',
    oSVersion: '',
    loginDate: undefined,
    location: '',
    address: '',
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
  userID: 0,
  account: '',
  loginIP: '',
  loginType: '',
  oSVersion: '',
  loginDate: undefined,
  location: '',
  address: '',
})
const formRules = ref<FormRules>({
  userID: [
    {
      required: true,
      message: t('login_formRules.userID'),
      trigger: 'blur',
    },
  ],
  account: [
    {
      required: true,
      message: t('login_formRules.account'),
      trigger: 'blur',
    },
  ],
  loginIP: [
    {
      required: true,
      message: t('login_formRules.loginIP'),
      trigger: 'blur',
    },
  ],
  loginType: [
    {
      required: true,
      message: t('login_formRules.loginType'),
      trigger: 'blur',
    },
  ],
  oSVersion: [
    {
      required: true,
      message: t('login_formRules.oSVersion'),
      trigger: 'blur',
    },
  ],
  loginDate: [
    {
      required: true,
      message: t('login_formRules.loginDate'),
      trigger: 'blur',
    },
  ],
  location: [
    {
      required: true,
      message: t('login_formRules.location'),
      trigger: 'blur',
    },
  ],
  address: [
    {
      required: true,
      message: t('login_formRules.address'),
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
      <ElFormItem :label="t('login_form.userID')" prop="userID">
        <ElSelect
          v-model="form.userID"
          clearable
          :placeholder="t('login_form.userID')"
          style="width: 240px"
        >
          <el-option
            v-for="(item, i) in ['a', 'b']"
            :key="item + i"
            :label="item"
            :value="item"
          />
        </ElSelect>
      </ElFormItem>
      <ElFormItem :label="t('login_form.account')" prop="account">
        <ElInput v-model="form.account" :placeholder="t('login_form.account')" />
      </ElFormItem>
      <ElFormItem :label="t('login_form.loginIP')" prop="loginIP">
        <ElInput v-model="form.loginIP" :placeholder="t('login_form.loginIP')" />
      </ElFormItem>
      <ElFormItem :label="t('login_form.loginType')" prop="loginType">
        <ElInput v-model="form.loginType" :placeholder="t('login_form.loginType')" />
      </ElFormItem>
      <ElFormItem :label="t('login_form.oSVersion')" prop="oSVersion">
        <ElInput v-model="form.oSVersion" :placeholder="t('login_form.oSVersion')" />
      </ElFormItem>
      <ElFormItem :label="t('login_form.loginDate')" prop="loginDate">
        <ElDatePicker
          v-model="form.loginDate" format="YYYY/MM/DD"
          value-format="YYYY-MM-DD" :placeholder="t('login_form.loginDate')" type="date"
        />
      </ElFormItem>
      <ElFormItem :label="t('login_form.location')" prop="location">
        <ElInput v-model="form.location" :placeholder="t('login_form.location')" />
      </ElFormItem>
      <ElFormItem :label="t('login_form.address')" prop="address">
        <ElInput v-model="form.address" :placeholder="t('login_form.address')" />
      </ElFormItem>
    </ElForm>
  </div>
</template>

<style lang="scss" scoped>
// scss
</style>
