<script setup lang="ts">
import type { Props } from '../DetailForm/index.vue'
import { useI18n } from 'vue-i18n'
import DetailForm from '../DetailForm/index.vue'

const props = defineProps<{
  mode: 'dialog' | 'drawer'
} & Props>()
const emits = defineEmits<{
  success: []
}>()
const { t } = useI18n()
const visible = defineModel<boolean>({
  default: false,
})

const formRef = useTemplateRef('formRef')

const title = computed(() => props.id === '' ? t('user_form.addUser') : t('user_form.editUser'))

function onSubmit() {
  // submit() 为组件内部方法
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
    <ElDialog v-if="props.mode === 'dialog'" v-model="visible" :title="title" width="50% " top="4vh" :close-on-click-modal="false" append-to-body destroy-on-close>
      <DetailForm ref="formRef" v-bind="$props" />
      <template #footer>
        <ElButton size="large" @click="onCancel">
          {{ t('common.close') }}
        </ElButton>
        <ElButton type="primary" size="large" @click="onSubmit">
          {{ t('common.save') }}
        </ElButton>
      </template>
    </ElDialog>
    <ElDrawer v-else-if="props.mode === 'drawer'" v-model="visible" :title="title" size="600px" :close-on-click-modal="false" destroy-on-close>
      <DetailForm ref="formRef" v-bind="$props" />
      <div class="buttons">
        <ElButton size="large" @click="onCancel">
          {{ t('common.close') }}
        </ElButton>
        <ElButton type="primary" size="large" @click="onSubmit">
          {{ t('common.save') }}
        </ElButton>
      </div>
    </ElDrawer>
  </div>
</template>
