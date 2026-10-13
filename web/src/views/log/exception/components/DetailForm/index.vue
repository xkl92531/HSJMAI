<script setup lang="ts">
import type { FormInstance } from 'element-plus'
import type { DetailFormProps } from '../../types'
import api from '@/api/modules/log/exception'
import { ElCol, ElRow } from 'element-plus'
import { useI18n } from 'vue-i18n'
import messages from '../lang'

const props = withDefaults(
  defineProps<DetailFormProps>(),
  {
    id: '',
    name: '',
    className: '',
    methodName: '',
    exceptionName: '',
    exceptionMsg: '',
    exceptionSource: '',
    stackTrace: '',
    paramsObj: '',
    exceptionTime: undefined,
    account: '',
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
  className: '',
  methodName: '',
  exceptionName: '',
  exceptionMsg: '',
  exceptionSource: '',
  stackTrace: '',
  paramsObj: '',
  exceptionTime: undefined,
  account: '',
  createrName: '',
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

})
</script>

<template>
  <div v-loading="loading">
    <ElForm ref="formRef" :model="form" label-width="120px" label-suffix="：">
      <ElRow :gutter="24">
        <ElCol :span="8">
          <ElFormItem :label="t('exception_form.name')" prop="name">
            {{ form.name }}
          </ElFormItem>
        </ElCol>
        <ElCol :span="8">
          <ElFormItem :label="t('exception_form.className')" prop="className">
            {{ form.className }}
          </ElFormItem>
        </ElCol>
        <ElCol :span="8">
          <ElFormItem :label="t('exception_form.methodName')" prop="methodName">
            {{ form.methodName }}
          </ElFormItem>
        </ElCol>

        <ElCol :span="8">
          <ElFormItem :label="t('exception_form.exceptionTime')" prop="exceptionTime">
            {{ form.exceptionTime }}
          </ElFormItem>
        </ElCol>
        <ElCol :span="8">
          <ElFormItem :label="t('exception_form.account')" prop="account">
            {{ form.account }}
          </ElFormItem>
        </ElCol>
        <ElCol :span="8">
          <ElFormItem :label="t('exception_form.createName')" prop="account">
            {{ form.createrName }}
          </ElFormItem>
        </ElCol>
        <ElCol :span="12">
          <ElFormItem :label="t('exception_form.exceptionName')" prop="exceptionName">
            {{ form.exceptionName }}
          </ElFormItem>
        </ElCol>
        <ElCol :span="24">
          <ElFormItem :label="t('exception_form.exceptionMsg')" prop="exceptionMsg">
            <ElInput v-model="form.exceptionMsg" type="textarea" :rows="5" readonly />
          </ElFormItem>
        </ElCol>
        <ElCol :span="24">
          <ElFormItem :label="t('exception_form.exceptionSource')" prop="exceptionSource">
            <ElInput v-model="form.exceptionSource" type="textarea" :rows="5" readonly />
          </ElFormItem>
        </ElCol>
        <ElCol :span="24">
          <ElFormItem :label="t('exception_form.stackTrace')" prop="stackTrace">
            <ElInput v-model="form.stackTrace" type="textarea" :rows="5" readonly />
          </ElFormItem>
        </ElCol>
        <ElCol :span="24">
          <ElFormItem :label="t('exception_form.paramsObj')" prop="paramsObj">
            <ElInput v-model="form.paramsObj" type="textarea" :rows="5" readonly />
          </ElFormItem>
        </ElCol>
      </ElRow>
    </ElForm>
  </div>
</template>

<style lang="scss" scoped>
// scss
</style>
