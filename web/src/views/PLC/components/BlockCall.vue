<script setup lang="ts">
import { ref, watch } from 'vue'
import BlockCallAPI from '@/api/modules/PLC/blockCall'
import { useI18n } from 'vue-i18n'
import messages from './blockCall.lang'

const props = defineProps<{
    blockId: string | number
    visible: boolean
}>()

const emit = defineEmits(['update:visible'])

const { mergeLocaleMessage } = useI18n()

// 遍历语言包并合并到当前实例
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}

const loading = ref(false)
const callList = ref<any[]>([])
const searchData = ref({
    calleeBlockName: ''
})

const pagination = ref({
    page: 1,
    limit: 10,
    total: 0
})

const getCallList = async () => {
    if (!props.blockId) return
    loading.value = true
    try {
        const res = await BlockCallAPI.list({
            page: pagination.value.page,
            limit: pagination.value.limit,
            CallerBlockId: props.blockId,
            CalleeBlockName: searchData.value.calleeBlockName || undefined
        }) as any
        if (res.data) {
            callList.value = res.data
            pagination.value.total = res.total || 0
        } else if (Array.isArray(res)) {
            callList.value = res
            pagination.value.total = res.length
        } else {
            callList.value = []
            pagination.value.total = 0
        }
    } finally {
        loading.value = false
    }
}

const handleSearch = () => {
    pagination.value.page = 1
    getCallList()
}

const handleReset = () => {
    searchData.value = { calleeBlockName: '' }
    pagination.value.page = 1
    getCallList()
}

const onPageChange = (val: number) => {
    pagination.value.page = val
    getCallList()
}

watch(() => props.visible, (val) => {
    if (val) {
        handleReset()
    }
})

const handleClose = () => {
    emit('update:visible', false)
}
</script>

<template>
    <el-drawer
        :model-value="visible"
        :title="$t('blockCall.title')"
        size="60%"
        @close="handleClose"
    >
        <div class="h-full flex flex-col p-4">
            <!-- Search Bar -->
            <div class="search-bar">
                <el-form :inline="true" @submit.prevent="handleSearch">
                    <el-form-item :label="$t('blockCall.calleeBlockName')">
                        <el-input
                            v-model="searchData.calleeBlockName"
                            :placeholder="$t('blockCall.calleeBlockNamePlaceholder')"
                            clearable
                            style="width: 180px"
                            @keyup.enter="handleSearch"
                        />
                    </el-form-item>
                    <el-form-item>
                        <el-button type="primary" @click="handleSearch">
                            <i-ep-search class="mr-1" /> {{ $t('common.search') }}
                        </el-button>
                        <el-button @click="handleReset">
                            <i-ep-refresh class="mr-1" /> {{ $t('common.reset') }}
                        </el-button>
                    </el-form-item>
                </el-form>
            </div>

            <el-table v-loading="loading" :data="callList" style="width: 100%; flex: 1" stripe>
                <el-table-column prop="networkId" :label="$t('blockCall.networkId')" width="100" />
                <el-table-column prop="callerBlockName" :label="$t('blockCall.callerBlock')" width="180" />
                <el-table-column prop="calleeBlockName" :label="$t('blockCall.calleeBlock')" width="180" />
                <el-table-column prop="calleeBlockType" :label="$t('blockCall.calleeBlockType')" width="120" />
            </el-table>

            <div class="mt-4 flex justify-end">
                <el-pagination
                    v-model:current-page="pagination.page"
                    v-model:page-size="pagination.limit"
                    :total="pagination.total"
                    layout="total, prev, pager, next"
                    @current-change="onPageChange"
                />
            </div>
        </div>
    </el-drawer>
</template>

<style scoped>
.search-bar {
    margin-bottom: 16px;
    padding: 16px;
    background: var(--el-fill-color-light);
    border-radius: 8px;
}
:deep(.el-form-item) {
    margin-bottom: 0;
}
</style>
