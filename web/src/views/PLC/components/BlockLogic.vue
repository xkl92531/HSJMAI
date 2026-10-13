<script setup lang="ts">
import { ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import LogicAPI from '@/api/modules/PLC/logic'
import UploadAPI from '@/api/modules/PLC/upload'
import { useI18n } from 'vue-i18n'
import messages from './blockLogic.lang'

const props = defineProps<{
    blockId: string | number
    visible: boolean
}>()

const emit = defineEmits(['update:visible'])

const { t, mergeLocaleMessage } = useI18n()

// 遍历语言包并合并到当前实例
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}

const loading = ref(false)
const networks = ref<any[]>([])
const rerunningId = ref<string | number | null>(null)

const getNetworks = async () => {
    if (!props.blockId) return
    loading.value = true
    try {
        const res = await LogicAPI.getByBlockId(props.blockId) as any
        if (Array.isArray(res)) {
            networks.value = res
        } else if (res.data && Array.isArray(res.data)) {
            networks.value = res.data
        } else {
             // Fallback or empty
             networks.value = []
        }
    } finally {
        loading.value = false
    }
}

watch(() => props.visible, (val) => {
    if (val) {
        getNetworks()
    }
})

const handleRerunLu = (net: any) => {
    ElMessageBox.confirm(
        t('blockLogic.rerunConfirm', { id: net.networkId }),
        t('blockLogic.rerun'),
        { confirmButtonText: t('blockLogic.ok'), cancelButtonText: t('blockLogic.cancel'), type: 'warning' },
    ).then(async () => {
        rerunningId.value = net.id
        try {
            const res = await UploadAPI.rerunSingleLu(net.id) as any
            if (res.isSuccess) {
                ElMessage.success(res.backResult || t('blockLogic.rerunTriggered'))
            } else {
                ElMessage.error(res.backResult || res.errorMessage || t('blockLogic.rerunFailed'))
            }
        } catch (e: any) {
            ElMessage.error(e.message || t('blockLogic.rerunFailed'))
        } finally {
            rerunningId.value = null
        }
    }).catch(() => {})
}

const handleClose = () => {
    emit('update:visible', false)
}
</script>

<template>
    <el-drawer
        :model-value="visible"
        :title="$t('blockLogic.title')"
        size="60%"
        @close="handleClose"
    >
        <div v-loading="loading" class="h-full overflow-y-auto p-4">
            <el-empty v-if="networks.length === 0 && !loading" :description="$t('blockLogic.empty')" />
            
            <div v-for="net in networks" :key="net.id" class="mb-6 border rounded p-4 shadow-sm">
                <div class="mb-2 flex items-center justify-between border-b pb-2">
                    <h3 class="font-bold text-lg">{{ $t('blockLogic.networkTitle', { id: net.networkId }) }}</h3>
                    <div class="flex items-center gap-2">
                        <span class="text-xs text-gray-500">{{ net.programmingLanguage }}</span>
                        <el-button
                            type="warning"
                            size="small"
                            plain
                            :loading="rerunningId === net.id"
                            @click="handleRerunLu(net)"
                        >
                            <i-ep-refresh class="mr-1" /> {{ $t('blockLogic.rerun') }}
                        </el-button>
                    </div>
                </div>
                
                <div v-if="net.title" class="mb-2 text-sm font-medium text-gray-700">
                    {{ net.title }}
                </div>
                <div v-if="net.comment" class="mb-4 text-sm text-gray-500 italic">
                    {{ net.comment }}
                </div>
                
                <div class="bg-gray-50 p-3 rounded font-mono text-sm whitespace-pre-wrap overflow-x-auto">
                    {{ net.rawContent }}
                </div>
            </div>
        </div>
    </el-drawer>
</template>
