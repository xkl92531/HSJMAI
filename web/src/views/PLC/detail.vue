<route lang="yaml">
meta:
  title: 项目详情
</route>

<script setup lang="ts">
import { ref, onMounted, computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import ProjectAPI from '@/api/modules/PLC/project'
import BlockAPI from '@/api/modules/PLC/block'
import TagAPI from '@/api/modules/PLC/tag'
import ConstraintAPI from '@/api/modules/PLC/constraint'
import UploadAPI from '@/api/modules/PLC/upload'
import BlockLogic from './components/BlockLogic.vue'
import ConstraintRules from './components/ConstraintRules.vue'
import BlockInterface from './components/BlockInterface.vue'
import BlockCall from './components/BlockCall.vue'
import StopCauseWorkbench from './components/StopCauseWorkbench.vue'
import GraphStepTagPanel from '@/views/AI_PLC/Project/components/GraphStepTagPanel.vue'
import CylinderSignalEventTester from './components/CylinderSignalEventTester.vue'
import DeviceFocusSignals from './components/DeviceFocusSignals.vue'
import { useI18n } from 'vue-i18n'
import messages from './detail.lang'

const route = useRoute()
const router = useRouter()
const projectId = route.query.id as string

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}

const activeTab = ref('blocks')
const projectInfo = ref<any>({})
const blocks = ref([])
const tags = ref([])
const loading = ref(false)

// Block Logic Drawer
const logicVisible = ref(false)
const currentBlockId = ref<string | number>('')

// Constraint Rules Ref
const constraintRulesRef = ref<InstanceType<typeof ConstraintRules> | null>(null)
const analyzingTagId = ref<number | string | null>(null)

// 当前正在触发「重跑约束」的程序块 id（用于按钮 loading）
const rerunningBlockId = ref<number | string | null>(null)

// Block Interface Drawer
const interfaceVisible = ref(false)

// Block Call Drawer
const callVisible = ref(false)

// Programming languages for filter
const programmingLanguages = [
    { label: t('plc_detail.all'), value: '' },
    { label: 'DB', value: 'DB' },
    { label: 'GRAPH', value: 'GRAPH' },
    { label: 'LAD', value: 'LAD' },
    { label: 'STL', value: 'STL' },
    { label: 'SCL', value: 'SCL' },
]

// Search for blocks
const blockSearch = ref({
    blockName: '',
    programmingLanguage: ''
})

// Search for tags
const tagSearch = ref({
    tagName: '',
    address: '',
    comment: ''
})

// Pagination for Blocks
const blockPagination = ref({
    page: 1,
    limit: 10,
    total: 0
})

// Pagination for Tags
const tagPagination = ref({
    page: 1,
    limit: 10,
    total: 0
})

// 计算项目信息展示
const projectStats = computed(() => {
    return [
        { label: t('plc_detail.statBrand'), value: projectInfo.value.brand },
        { label: t('plc_detail.statProduct'), value: projectInfo.value.product },
        { label: t('plc_detail.statVersion'), value: projectInfo.value.productVersion },
        { label: t('plc_detail.statBlocks'), value: blockPagination.value.total, suffix: t('plc_detail.countUnit') },
        { label: t('plc_detail.statIOTags'), value: tagPagination.value.total, suffix: t('plc_detail.countUnit') },
    ]
})

const getProjectInfo = async () => {
    if (!projectId) return
    const res = await ProjectAPI.detail(projectId as string) as any
     projectInfo.value = res
}

const getBlocks = async () => {
    loading.value = true
    try {
        const res = await BlockAPI.list({
            page: blockPagination.value.page,
            limit: blockPagination.value.limit,
            ProjectId: projectId,
            BlockName: blockSearch.value.blockName || undefined,
            ProgrammingLanguage: blockSearch.value.programmingLanguage || undefined
        }) as any
        if (res.data) {
            blocks.value = res.data
            blockPagination.value.total = res.total || 0
        }
    } finally {
        loading.value = false
    }
}

const getTags = async () => {
    loading.value = true
    try {
        const res = await TagAPI.list({
            page: tagPagination.value.page,
            limit: tagPagination.value.limit,
            ProjectId: projectId,
            TagName: tagSearch.value.tagName || undefined,
            Address: tagSearch.value.address || undefined,
            Comment: tagSearch.value.comment || undefined
        }) as any
        if (res.data) {
            tags.value = res.data
            tagPagination.value.total = res.total || 0
        }
    } finally {
        loading.value = false
    }
}

const onBlockPageChange = (val: number) => {
    blockPagination.value.page = val
    getBlocks()
}

const onTagPageChange = (val: number) => {
    tagPagination.value.page = val
    getTags()
}

const handleOpenLogic = (row: any) => {
    currentBlockId.value = row.id
    logicVisible.value = true
}

const handleOpenInterface = (row: any) => {
    currentBlockId.value = row.id
    interfaceVisible.value = true
}

const handleOpenCall = (row: any) => {
    currentBlockId.value = row.id
    callVisible.value = true
}

const handleRerunBlock = (row: any) => {
    ElMessageBox.confirm(
        t('plc_detail.rerunConfirmMsg', { name: row.blockName }),
        t('plc_detail.rerunConfirmTitle'),
        { confirmButtonText: t('plc_detail.confirmOk'), cancelButtonText: t('common.cancel'), type: 'warning' },
    ).then(async () => {
        rerunningBlockId.value = row.id
        try {
            const res = await UploadAPI.rerunBlock(row.id) as any
            if (res.isSuccess) {
                ElMessage.success(res.backResult || t('plc_detail.rerunTriggered'))
            } else {
                ElMessage.error(res.backResult || res.errorMessage || t('plc_detail.rerunFailed'))
            }
        } catch (error: any) {
            ElMessage.error(error.message || t('plc_detail.rerunFailed'))
        } finally {
            rerunningBlockId.value = null
        }
    }).catch(() => {
        // 用户取消
    })
}

const handleSearchBlocks = () => {
    blockPagination.value.page = 1
    getBlocks()
}

const handleResetBlockSearch = () => {
    blockSearch.value = { blockName: '', programmingLanguage: '' }
    blockPagination.value.page = 1
    getBlocks()
}

const handleSearchTags = () => {
    tagPagination.value.page = 1
    getTags()
}

const handleResetTagSearch = () => {
    tagSearch.value = { tagName: '', address: '', comment: '' }
    tagPagination.value.page = 1
    getTags()
}

const handleParseTag = async (row: any) => {
    ElMessageBox.confirm(
        t('plc_detail.parseConfirmMsg', { name: row.tagName }),
        t('plc_detail.parseConfirmTitle'),
        {
            confirmButtonText: t('plc_detail.confirmOk'),
            cancelButtonText: t('common.cancel'),
            type: 'warning',
        }
    ).then(async () => {
        loading.value = true
        try {
            await TagAPI.parseByAI(row.id)
            ElMessage.success(t('plc_detail.parseSuccess'))
            getTags() // 刷新数据
        } catch (error: any) {
            ElMessage.error(error.message || t('plc_detail.parseFailed'))
        } finally {
            loading.value = false
        }
    }).catch(() => {
        // 用户取消
    })
}

const handleAnalyzeConstraint = async (row: any) => {
    analyzingTagId.value = row.id
    try {
        const res = await ConstraintAPI.analyzeSignal(row.id) as any
        if (res.isSuccess) {
            const totalRules = res.backResult?.totalRules || 0
            ElMessage.success(t('plc_detail.analyzeDone', { count: totalRules }))
            // 刷新约束规则列表
            constraintRulesRef.value?.refresh()
        } else {
            ElMessage.error(res.errorMessage || t('plc_detail.analyzeFailed'))
        }
    } catch (error: any) {
        ElMessage.error(error.message || t('plc_detail.analyzeFailed'))
    } finally {
        analyzingTagId.value = null
    }
}

onMounted(() => {
    if (projectId) {
        getProjectInfo()
        getBlocks()
        getTags()
    }
})
</script>

<template>
  <div>
    <FaPageHeader :title="projectInfo.projectName || t('plc_detail.pageTitle')" @back="router.back()">
        <template #description>
            <div class="project-stats">
                <template v-for="(stat, index) in projectStats" :key="index">
                    <div class="stat-item">
                        <span class="stat-label">{{ stat.label }}</span>
                        <span class="stat-value" :class="{ 'empty': !stat.value }">
                            {{ stat.value || '-' }}{{ stat.value && stat.suffix ? stat.suffix : '' }}
                        </span>
                    </div>
                    <el-divider v-if="index < projectStats.length - 1" direction="vertical" />
                </template>
            </div>
        </template>
    </FaPageHeader>

    <FaPageMain>
        <el-tabs v-model="activeTab">
            <el-tab-pane :label="$t('plc_detail.tabBlocks')" name="blocks">
                <!-- Search Bar -->
                <div class="search-bar">
                    <el-form :inline="true" @submit.prevent="handleSearchBlocks">
                        <el-form-item :label="$t('plc_detail.blockName')">
                            <el-input
                                v-model="blockSearch.blockName"
                                :placeholder="$t('plc_detail.blockNamePlaceholder')"
                                clearable
                                style="width: 180px"
                                @keyup.enter="handleSearchBlocks"
                            />
                        </el-form-item>
                        <el-form-item :label="$t('plc_detail.programmingLanguage')">
                            <el-select
                                v-model="blockSearch.programmingLanguage"
                                :placeholder="$t('plc_detail.all')"
                                clearable
                                style="width: 140px"
                            >
                                <el-option
                                    v-for="lang in programmingLanguages"
                                    :key="lang.value"
                                    :label="lang.label"
                                    :value="lang.value"
                                />
                            </el-select>
                        </el-form-item>
                        <el-form-item>
                            <el-button type="primary" @click="handleSearchBlocks">
                                <i-ep-search class="mr-1" /> {{ $t('common.search') }}
                            </el-button>
                            <el-button @click="handleResetBlockSearch">
                                <i-ep-refresh class="mr-1" /> {{ $t('common.reset') }}
                            </el-button>
                        </el-form-item>
                    </el-form>
                </div>

                <el-table v-loading="loading" :data="blocks" style="width: 100%" stripe>
                    <el-table-column prop="blockName" :label="$t('plc_detail.blockName')">
                        <template #default="{ row }">
                            <el-button link type="primary" @click="handleOpenLogic(row)">
                                {{ row.blockName }}
                            </el-button>
                        </template>
                    </el-table-column>
                    <el-table-column prop="blockType" :label="$t('plc_detail.blockType')" width="100">
                        <template #default="{ row }">
                            <el-tag size="small" :type="row.blockType === 'FB' ? 'primary' : row.blockType === 'FC' ? 'success' : row.blockType === 'DB' ? 'warning' : 'info'">
                                {{ row.blockType }}
                            </el-tag>
                        </template>
                    </el-table-column>
                    <el-table-column prop="blockNumber" :label="$t('plc_detail.blockNumber')" width="100" />
                    <el-table-column prop="programmingLanguage" :label="$t('plc_detail.programmingLanguage')" width="120" />
                    <el-table-column prop="description" :label="$t('plc_detail.description')" show-overflow-tooltip />
                    <el-table-column :label="$t('common.detail')" width="260" align="center">
                        <template #default="{ row }">
                            <el-button link type="primary" @click="handleOpenInterface(row)">
                                {{ $t('plc_detail.interfaceVars') }}
                            </el-button>
                            <el-button link type="primary" @click="handleOpenCall(row)">
                                {{ $t('plc_detail.referenceBlocks') }}
                            </el-button>
                            <el-button
                                v-if="!['DB', 'GlobalDB', 'InstanceDB', 'UDT'].includes(row.blockType)"
                                link
                                type="warning"
                                :loading="rerunningBlockId === row.id"
                                @click="handleRerunBlock(row)"
                            >
                                <i-ep-refresh-right class="mr-1" /> {{ $t('plc_detail.rerunConstraints') }}
                            </el-button>
                        </template>
                    </el-table-column>
                </el-table>
                <div class="mt-4 flex justify-end">
                    <el-pagination
                        v-model:current-page="blockPagination.page"
                        v-model:page-size="blockPagination.limit"
                        :total="blockPagination.total"
                        layout="total, prev, pager, next"
                        @current-change="onBlockPageChange"
                    />
                </div>
            </el-tab-pane>

            <el-tab-pane :label="$t('plc_detail.tabIOTags')" name="tags">
                <!-- Search Bar -->
                <div class="search-bar">
                    <el-form :inline="true" @submit.prevent="handleSearchTags">
                        <el-form-item :label="$t('plc_detail.tagName')">
                            <el-input
                                v-model="tagSearch.tagName"
                                :placeholder="$t('plc_detail.tagNamePlaceholder')"
                                clearable
                                style="width: 150px"
                                @keyup.enter="handleSearchTags"
                            />
                        </el-form-item>
                        <el-form-item :label="$t('plc_detail.address')">
                            <el-input
                                v-model="tagSearch.address"
                                :placeholder="$t('plc_detail.addressPlaceholder')"
                                clearable
                                style="width: 150px"
                                @keyup.enter="handleSearchTags"
                            />
                        </el-form-item>
                        <el-form-item :label="$t('plc_detail.comment')">
                            <el-input
                                v-model="tagSearch.comment"
                                :placeholder="$t('plc_detail.commentPlaceholder')"
                                clearable
                                style="width: 180px"
                                @keyup.enter="handleSearchTags"
                            />
                        </el-form-item>
                        <el-form-item>
                            <el-button type="primary" @click="handleSearchTags">
                                <i-ep-search class="mr-1" /> {{ $t('common.search') }}
                            </el-button>
                            <el-button @click="handleResetTagSearch">
                                <i-ep-refresh class="mr-1" /> {{ $t('common.reset') }}
                            </el-button>
                        </el-form-item>
                    </el-form>
                </div>

                <el-table v-loading="loading" :data="tags" style="width: 100%" stripe>

                    <el-table-column prop="tagName" :label="$t('plc_detail.tagName')" align="center"/>
                    <el-table-column prop="address" :label="$t('plc_detail.address')" width="150" align="center"/>
                    <el-table-column prop="dataType" :label="$t('plc_detail.dataType')" width="120" align="center"/>
                    <el-table-column prop="tableName" :label="$t('plc_detail.tableName')" width="120" align="center"/>
                    <el-table-column prop="comment" :label="$t('plc_detail.comment')" show-overflow-tooltip />
                    <el-table-column prop="normal_Value" :label="$t('plc_detail.normalValue')" show-overflow-tooltip align="center"/>
                    <el-table-column prop="annotation" :label="$t('plc_detail.normalAnnotation')" show-overflow-tooltip />
                    <el-table-column prop="tagType" :label="$t('plc_detail.tagType')" show-overflow-tooltip align="center"/>
                    <el-table-column prop="tagEffect" :label="$t('plc_detail.tagEffect')" show-overflow-tooltip />
                    <el-table-column :label="$t('common.operate')" width="180" fixed="right" align="center">
                        <template #default="{ row }">
                            <el-button
                                link
                                type="primary"
                                size="small"
                                @click="handleParseTag(row)"
                            >
                                <i-ep-cpu class="mr-1" /> {{ $t('plc_detail.parseBtn') }}
                            </el-button>
                            <el-button
                                link
                                type="warning"
                                size="small"
                                :loading="analyzingTagId === row.id"
                                @click="handleAnalyzeConstraint(row)"
                            >
                                <i-ep-connection class="mr-1" /> {{ $t('plc_detail.analyzeBtn') }}
                            </el-button>
                        </template>
                    </el-table-column>
                </el-table>
                 <div class="mt-4 flex justify-end">
                    <el-pagination
                        v-model:current-page="tagPagination.page"
                        v-model:page-size="tagPagination.limit"
                        :total="tagPagination.total"
                        layout="total, prev, pager, next"
                        @current-change="onTagPageChange"
                    />
                </div>
            </el-tab-pane>
            <el-tab-pane :label="$t('plc_detail.tabConstraints')" name="constraints">
                <ConstraintRules
                    v-if="projectId"
                    ref="constraintRulesRef"
                    :project-id="projectId"
                />
            </el-tab-pane>
            <el-tab-pane :label="$t('plc_detail.tabStopCause')" name="stop-cause">
                <StopCauseWorkbench v-if="projectId" :project-id="projectId" />
            </el-tab-pane>
            <el-tab-pane :label="$t('plc_detail.tabDeviceFocus')" name="device-focus">
                <DeviceFocusSignals v-if="projectId" :project-id="projectId" />
            </el-tab-pane>
            <el-tab-pane :label="$t('plc_detail.tabGraphStep')" name="graph-step-tag" lazy>
            <GraphStepTagPanel :project-id="projectId" />
        </el-tab-pane>
        <el-tab-pane :label="$t('plc_detail.tabCylinderSignal')" name="cylinder-signal-event">
                <CylinderSignalEventTester v-if="projectId" :project-id="projectId" />
            </el-tab-pane>
        </el-tabs>
    </FaPageMain>

    <BlockLogic
        v-model:visible="logicVisible"
        :block-id="currentBlockId"
    />

    <BlockInterface
        v-model:visible="interfaceVisible"
        :block-id="currentBlockId"
    />

    <BlockCall
        v-model:visible="callVisible"
        :block-id="currentBlockId"
    />
  </div>
</template>

<style scoped>
.project-stats {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 8px;
}

.stat-item {
    display: flex;
    align-items: center;
    gap: 6px;
}

.stat-label {
    color: var(--el-text-color-secondary);
    font-size: 13px;
}

.stat-value {
    color: var(--el-text-color-primary);
    font-weight: 500;
    font-size: 13px;
}

.stat-value.empty {
    color: var(--el-text-color-placeholder);
    font-weight: 400;
}

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
