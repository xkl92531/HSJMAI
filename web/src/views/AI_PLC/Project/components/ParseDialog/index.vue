<script setup lang="ts">
import { ElMessage, type UploadFile } from 'element-plus'
import api from '@/api/modules/AI_PLC/project'
import { useI18n } from 'vue-i18n'
import messages from '../lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const props = defineProps<{
  projectId?: number | string
}>()

const emits = defineEmits<{
  success: []
}>()

const visible = defineModel<boolean>({
  default: false,
})

const loading = ref(false)
const fileList = ref<UploadFile[]>([])
const formData = ref({
  projectName: '',
  productionLineCode: '',
})

// 产线选项（可以从API获取）
const productionLineOptions = ref([
  { value: 'LINE_01', label: t('aiPlcProject_form.line01') },
  { value: 'LINE_02', label: t('aiPlcProject_form.line02') },
  { value: 'LINE_03', label: t('aiPlcProject_form.line03') },
])

function handleChange(file: UploadFile, uploadFiles: UploadFile[]) {
  fileList.value = uploadFiles
}

function handleRemove(file: UploadFile, uploadFiles: UploadFile[]) {
  fileList.value = uploadFiles
}

async function onSubmit() {
  if (!formData.value.productionLineCode) {
    ElMessage.warning(t('aiPlcProject_form.selectLine'))
    return
  }

  if (fileList.value.length === 0) {
    ElMessage.warning(t('aiPlcProject_form.selectFile'))
    return
  }

  // 收集所有有效文件
  const files: File[] = []
  for (const item of fileList.value) {
    if (item.raw) {
      files.push(item.raw)
    }
  }

  if (files.length === 0) {
    ElMessage.warning(t('aiPlcProject_form.invalidFile'))
    return
  }

  loading.value = true
  try {
    const res: any = await api.parseMetadata(
      props.projectId || 0, 
      files,
      formData.value.projectName,
      formData.value.productionLineCode
    )
    if (res.isSuccess) {
      ElMessage.success(t('aiPlcProject_form.parseSuccessSummary', { total: res.backResult?.totalFiles || 0, success: res.backResult?.successCount || 0, fail: res.backResult?.failCount || 0 }))
      emits('success')
      onClose()
    } else {
      ElMessage.error(res.errorMessage || t('aiPlcProject_form.parseFailed'))
    }
  } catch (error: any) {
    ElMessage.error(error.message || t('aiPlcProject_form.parseFailed'))
  } finally {
    loading.value = false
  }
}

function onClose() {
  visible.value = false
  fileList.value = []
  formData.value = {
    projectName: '',
    productionLineCode: '',
  }
}
</script>

<template>
  <ElDialog
    v-model="visible"
    :title="$t('aiPlcProject_form.parseMetadataTitle')"
    width="550px"
    :close-on-click-modal="false"
    append-to-body
    destroy-on-close
  >
    <div v-loading="loading">
      <ElForm :model="formData" label-width="100px">
        <ElFormItem :label="$t('aiPlcProject_form.projectName')">
          <ElInput v-model="formData.projectName" :placeholder="$t('aiPlcProject_form.projectNameOptional')" />
        </ElFormItem>
        <ElFormItem :label="$t('aiPlcProject_form.productionLine')" required>
          <ElSelect v-model="formData.productionLineCode" :placeholder="$t('aiPlcProject_form.selectLine')" style="width: 100%">
            <ElOption
              v-for="item in productionLineOptions"
              :key="item.value"
              :label="item.label"
              :value="item.value"
            />
          </ElSelect>
        </ElFormItem>
        <ElFormItem :label="$t('aiPlcProject_form.uploadFile')">
          <ElUpload
            drag
            multiple
            :auto-upload="false"
            :file-list="fileList"
            accept=".xml,.ml,.zip"
            :on-change="handleChange"
            :on-remove="handleRemove"
            style="width: 100%"
          >
            <div class="upload-content">
              <FaIcon name="i-ep:upload-filled" class="text-3xl mb-2" />
              <div>{{ $t('aiPlcProject_form.uploadDragTip') }}</div>
              <div class="upload-tip">{{ $t('aiPlcProject_form.uploadFormatTipZip') }}</div>
            </div>
          </ElUpload>
        </ElFormItem>
      </ElForm>
    </div>
    <template #footer>
      <ElButton size="large" @click="onClose">
        {{ $t('common.cancel') }}
      </ElButton>
      <ElButton type="primary" size="large" :loading="loading" @click="onSubmit">
        {{ $t('aiPlcProject_form.startParseBtn') }}
      </ElButton>
    </template>
  </ElDialog>
</template>

<style scoped>
.upload-content {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 20px;
}

.upload-tip {
  margin-top: 8px;
  color: #909399;
  font-size: 12px;
}
</style>
