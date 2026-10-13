<script setup lang="ts">
import { ref } from 'vue'
import { ElUpload, ElButton, ElDialog, ElMessage, type UploadProps, type UploadUserFile, type UploadInstance } from 'element-plus'
import UploadAPI from '@/api/modules/PLC/upload'
import { useI18n } from 'vue-i18n'
import messages from './ioTagUpload.lang'

const props = defineProps<{
  visible: boolean
  projectId: number | string
}>()

const emit = defineEmits<{
  (e: 'update:visible', val: boolean): void
  (e: 'success'): void
}>()

const { t, mergeLocaleMessage } = useI18n()

// 遍历语言包并合并到当前实例
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}

const uploadRef = ref<UploadInstance>()
const fileList = ref<UploadUserFile[]>([])
const uploading = ref(false)

const submitUpload = async () => {
    if (fileList.value.length === 0) {
        ElMessage.warning(t('ioTagUpload.selectFileFirst'))
        return
    }
    if (!props.projectId) {
        ElMessage.warning(t('ioTagUpload.projectIdMissing'))
        return 
    }

    uploading.value = true
    try {
        const formData = new FormData()
        const rawFile = fileList.value[0].raw
        if (rawFile) {
            formData.append('file', rawFile)
            formData.append('ProjectId', String(props.projectId))

            const res = await UploadAPI.uploadIOTagsCsv(formData)
            const responseData = res as any
            if (responseData.isSuccess) {
                ElMessage.success(t('ioTagUpload.importSuccess', { count: responseData.backResult }))
                emit('success')
                handleClose()
            } else {
                ElMessage.error(responseData.errorMessage || t('ioTagUpload.importFailed'))
            }
        }
    } catch (error) {
        console.error(error)
        ElMessage.error(t('ioTagUpload.importError'))
    } finally {
        uploading.value = false
    }
}

const handleExceed: UploadProps['onExceed'] = (files) => {
    uploadRef.value!.clearFiles()
    const file = files[0] as UploadUserFile
    file.uid = Date.now()
    uploadRef.value!.handleStart(file as any)
}

const handleClose = () => {
    fileList.value = []
    emit('update:visible', false)
}
</script>

<template>
    <ElDialog
        :model-value="visible"
        :title="$t('ioTagUpload.title')"
        width="500px"
        @close="handleClose"
    >
        <ElUpload
            ref="uploadRef"
            v-model:file-list="fileList"
            :auto-upload="false"
            :limit="1"
            :on-exceed="handleExceed"
            accept=".csv"
            drag
        >
            <div class="el-upload__text">
                {{ $t('ioTagUpload.dragHere') }} <em>{{ $t('ioTagUpload.clickUpload') }}</em>
            </div>
            <template #tip>
                <div class="el-upload__tip">
                    {{ $t('ioTagUpload.csvTip') }}
                </div>
            </template>
        </ElUpload>
        <template #footer>
            <ElButton @click="handleClose">{{ $t('ioTagUpload.cancel') }}</ElButton>
            <ElButton type="primary" :loading="uploading" @click="submitUpload">
                {{ $t('common.import') }}
            </ElButton>
        </template>
    </ElDialog>
</template>
