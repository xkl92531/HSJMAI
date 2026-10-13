<script setup lang="ts">
import { ElLoading } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { loadingHide, loadingShow } from '@/ui/components/FaLoading'
import DetailForm from '../DetailForm/setpurview.vue'
import messages from '../../lang'

const props = defineProps ({
  ...DetailForm.props,
  mode: {
    type: String,
  },
  title: {
    type: String,
    default: '',
  },
})
const emits = defineEmits<{
  success: []
}>()
const { t, mergeLocaleMessage } = useI18n()
// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}
const visible = defineModel<boolean>({
  default: false,
})

const formRef = ref()

// 标题缺省时回退为「配置权限」
const titleText = computed(() => props.title || t('role_form.configurePermissions'))

// const title = ref('配置权限')
let loading
function openLoading() {
  loadingShow({
    type: 'circle-fade',
    size: 30,
    text: t('role_form.saving'),
  })
}

function closeLoading() {
  loadingHide()
}

function onSubmit() {
  // submit() 为组件内部方法

  formRef.value.submit().then((res: boolean) => {
    emits('success')
    // onCancel()
    loading.close()
  })
}

function onCancel() {
  visible.value = false
}
</script>

<template>
  <div>
    <ElDialog
      v-if="props.mode === 'dialog'"
      v-model="visible"
      :title="titleText"
      width="70%"
      :close-on-click-modal="false"
      append-to-body
      destroy-on-close
      class="el-submit-dialog"
    >
      <DetailForm ref="formRef" v-bind="props" @openloading="openLoading" @close-loading="closeLoading" />
      <template #footer>
        <ElButton size="large" @click="onCancel">
          {{ t("common.close") }}
        </ElButton>
        <ElButton type="primary" size="large" @click="onSubmit">
          {{ t("common.save") }}
        </ElButton>
      </template>
    </ElDialog>
    <ElDrawer
      v-else-if="props.mode === 'drawer'"
      v-model="visible"
      :title="titleText"
      size="75%"
      :close-on-click-modal="false"
      destroy-on-close
    >
      <DetailForm ref="formRef" v-bind="props" @open-loading="openLoading" @close-loading="closeLoading" />
      <template #footer>
        <ElButton size="large" @click="onCancel">
          {{ t("common.close") }}
        </ElButton>
        <ElButton type="primary" size="large" @click="onSubmit">
          {{ t("common.save") }}
        </ElButton>
      </template>
    </ElDrawer>
  </div>
</template>
