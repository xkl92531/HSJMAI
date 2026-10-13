<script setup lang="ts">
import { ref, watch } from 'vue'
import BlockInterfaceAPI from '@/api/modules/PLC/blockInterface'
import { useI18n } from 'vue-i18n'
import messages from './blockInterface.lang'

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
const interfaceList = ref<any[]>([])
const searchData = ref({
    variableName: '',
    dataType: ''
})

const pagination = ref({
    page: 1,
    limit: 10,
    total: 0
})

const getInterfaceList = async () => {
    if (!props.blockId) return
    loading.value = true
    try {
        const res = await BlockInterfaceAPI.list({
            page: pagination.value.page,
            limit: pagination.value.limit,
            BlockId: props.blockId,
            VariableName: searchData.value.variableName || undefined,
            DataType: searchData.value.dataType || undefined
        }) as any
        if (res.data) {
            interfaceList.value = res.data
            pagination.value.total = res.total || 0
        } else if (Array.isArray(res)) {
            interfaceList.value = res
            pagination.value.total = res.length
        } else {
            interfaceList.value = []
            pagination.value.total = 0
        }
    } finally {
        loading.value = false
    }
}

const handleSearch = () => {
    pagination.value.page = 1
    getInterfaceList()
}

const handleReset = () => {
    searchData.value = { variableName: '', dataType: '' }
    pagination.value.page = 1
    getInterfaceList()
}

const onPageChange = (val: number) => {
    pagination.value.page = val
    getInterfaceList()
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
        :title="$t('blockInterface.title')"
        size="60%"
        @close="handleClose"
    >
        <div class="h-full flex flex-col p-4">
            <!-- Search Bar -->
            <div class="search-bar">
                <el-form :inline="true" @submit.prevent="handleSearch">
                    <el-form-item :label="$t('blockInterface.variableName')">
                        <el-input
                            v-model="searchData.variableName"
                            :placeholder="$t('blockInterface.variableNamePlaceholder')"
                            clearable
                            style="width: 150px"
                            @keyup.enter="handleSearch"
                        />
                    </el-form-item>
                    <el-form-item :label="$t('blockInterface.dataType')">
                        <el-input
                            v-model="searchData.dataType"
                            :placeholder="$t('blockInterface.dataTypePlaceholder')"
                            clearable
                            style="width: 150px"
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

            <el-table v-loading="loading" :data="interfaceList" style="width: 100%; flex: 1" stripe>
                <el-table-column prop="variableName" :label="$t('blockInterface.variableName')" width="180" show-overflow-tooltip/>
                <el-table-column prop="variablePath" :label="$t('blockInterface.fullPath')" min-width="200" show-overflow-tooltip/>
                <el-table-column prop="dataType" :label="$t('blockInterface.dataType')" width="150" />
                <el-table-column prop="offset" :label="$t('blockInterface.offset')" width="100" />
                <el-table-column prop="scope" :label="$t('blockInterface.scope')" width="120" />
                <el-table-column prop="initialValue" :label="$t('blockInterface.initialValue')" width="150" show-overflow-tooltip/>
                <el-table-column prop="comment" :label="$t('blockInterface.comment')" show-overflow-tooltip />
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
