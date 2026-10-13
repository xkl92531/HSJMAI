<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import DetailForm from '../DetailForm/purview.vue'

const props = defineProps({
  ...DetailForm.props,
  modelValue: {
    type: Boolean,
    default: false,
  },
  mode: {
    type: String,
    default: 'dialog',
    validator: (val: string) => ['dialog', 'drawer'].includes(val),
  },

})

const emits = defineEmits<{
  success: []
}>()
const { t } = useI18n()
const visible = defineModel<boolean>({
  default: false,
})

const formRef = ref()

const title = computed(() => t('user_form.roleAllocation'))

function onSubmit() {
  // submit() 为组件内部方法
  formRef.value.submit().then(() => {
    emits('success')
    onCancel()
  })
}

function onCancel() {
  visible.value = false
}
</script>

<template>
  <div>
    <ElDialog v-if="props.mode === 'dialog'" v-model="visible" :title="title" width="50%" :close-on-click-modal="false" append-to-body destroy-on-close>
      <DetailForm ref="formRef" v-bind="props" />
      <template #footer>
        <ElButton size="large" @click="onCancel">
          {{ t('common.cancel') }}
        </ElButton>
        <ElButton type="primary" size="large" @click="onSubmit">
          {{ t('common.save') }}
        </ElButton>
      </template>
    </ElDialog>
    <ElDrawer v-else-if="props.mode === 'drawer'" v-model="visible" :title="title" size="600px" :close-on-click-modal="false" destroy-on-close>
      <DetailForm ref="formRef" v-bind="props" />
      <template #footer>
        <ElButton size="large" @click="onCancel">
          {{ t('common.cancel') }}
        </ElButton>
        <ElButton type="primary" size="large" @click="onSubmit">
          {{ t('common.save') }}
        </ElButton>
      </template>
    </ElDrawer>
  </div>
</template>
