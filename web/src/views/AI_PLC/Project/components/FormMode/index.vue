<script setup lang="ts">
import type { DetailFormProps } from '../../types'
import DetailForm from '../DetailForm/index.vue'
import { useI18n } from 'vue-i18n'
import messages from '../lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const props = defineProps<{
  mode: 'dialog' | 'drawer'
} & DetailFormProps>()

const emits = defineEmits<{
  success: []
}>()

const visible = defineModel<boolean>({
  default: false,
})

const formRef = ref()

const title = computed(() => props.id === '' ? t('aiPlcProject_form.createTitle') : t('aiPlcProject_form.editTitle'))

function onSubmit() {
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
    <ElDialog v-if="props.mode === 'dialog'" v-model="visible" :title="title" width="800px" :close-on-click-modal="false" append-to-body destroy-on-close>
      <DetailForm ref="formRef" v-bind="props" />
      <template #footer>
        <ElButton size="large" @click="onCancel">
          {{ $t('common.cancel') }}
        </ElButton>
        <ElButton type="primary" size="large" @click="onSubmit">
          {{ $t('common.save') }}
        </ElButton>
      </template>
    </ElDialog>
    <ElDrawer v-else-if="props.mode === 'drawer'" v-model="visible" :title="title" size="600px" :close-on-click-modal="false" destroy-on-close>
      <DetailForm ref="formRef" v-bind="props" />
      <template #footer>
        <ElButton size="large" @click="onCancel">
          {{ $t('common.cancel') }}
        </ElButton>
        <ElButton type="primary" size="large" @click="onSubmit">
          {{ $t('common.save') }}
        </ElButton>
      </template>
    </ElDrawer>
  </div>
</template>
