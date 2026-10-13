<script setup lang="ts">
import type { DetailFormProps } from '../../types'
import { useI18n } from 'vue-i18n'
import DetailForm from '../DetailForm/user.vue'

const props = defineProps<{
  mode: 'dialog' | 'drawer'
} & DetailFormProps>()
const emits = defineEmits<{
  success: []
}>()
const { t } = useI18n()
const visible = defineModel<boolean>({
  default: false,
})

const formRef = ref()

const title = computed(() => t('role_form.viewUser'))

function onCancel() {
  visible.value = false
}
</script>

<template>
  <div>
    <ElDialog v-if="props.mode === 'dialog'" v-model="visible" :title="title" top="4vh" width="60%" :close-on-click-modal="false" append-to-body destroy-on-close>
      <DetailForm ref="formRef" v-bind="props" />
      <template #footer>
        <ElButton size="large" @click="onCancel">
          {{ t('common.close') }}
        </ElButton>
      </template>
    </ElDialog>
    <ElDrawer v-else-if="props.mode === 'drawer'" v-model="visible" :title="title" size="60%" :close-on-click-modal="false" destroy-on-close>
      <DetailForm ref="formRef" v-bind="props" />
      <template #footer>
        <ElButton size="large" @click="onCancel">
          {{ t('common.close') }}
        </ElButton>
      </template>
    </ElDrawer>
  </div>
</template>
