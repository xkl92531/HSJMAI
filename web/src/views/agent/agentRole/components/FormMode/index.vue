<script setup lang="ts">
import type { Props } from '../DetailForm/index.vue'
import DetailForm from '../DetailForm/index.vue'
import { useI18n } from 'vue-i18n'
import messages from '../lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const l in messages) {
  mergeLocaleMessage(l, messages[l])
}

const props = defineProps<{
  mode: 'dialog' | 'drawer'
} & Props>()

const emits = defineEmits<{
  success: []
}>()

const visible = defineModel<boolean>({
  default: false,
})

const formRef = useTemplateRef('formRef')

const title = computed(() => props.id === '' ? t('agentRole_form.createTitle') : t('agentRole_form.editTitle'))

function onSubmit() {
  formRef.value?.submit().then(() => {
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
    <ElDialog v-if="props.mode === 'dialog'" v-model="visible" :title="title" width="900px" top="4vh" :close-on-click-modal="false" append-to-body destroy-on-close>
      <DetailForm ref="formRef" v-bind="$props" />
      <template #footer>
        <ElButton size="large" @click="onCancel">
          {{ $t('common.close') }}
        </ElButton>
        <ElButton type="primary" size="large" @click="onSubmit">
          {{ $t('common.save') }}
        </ElButton>
      </template>
    </ElDialog>
    <ElDrawer v-else-if="props.mode === 'drawer'" v-model="visible" :title="title" size="900px" :close-on-click-modal="false" destroy-on-close>
      <DetailForm ref="formRef" v-bind="$props" />
      <template #footer>
        <ElButton size="large" @click="onCancel">
          {{ $t('common.close') }}
        </ElButton>
        <ElButton type="primary" size="large" @click="onSubmit">
          {{ $t('common.save') }}
        </ElButton>
      </template>
    </ElDrawer>
  </div>
</template>
