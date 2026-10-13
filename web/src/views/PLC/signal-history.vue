<route lang="yaml">
meta:
  title: 信号历史数据
</route>

<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { ElMessage } from 'element-plus'
import { useI18n } from 'vue-i18n'
import StopCauseDiagnosisAPI from '@/api/modules/PLC/stopCauseDiagnosis'
import ProjectAPI from '@/api/modules/PLC/project'
import messages from './signal-history.lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const route = useRoute()

// ── 查询条件 ──
const projectId = ref<number | null>(null)
const stationCode = ref('')
const keyword = ref('')

// ── 分页 ──
const page = ref(1)
const pageSize = ref(50)

// ── 数据 ──
const loading = ref(false)
const tableData = ref<any[]>([])
const totalCount = ref(0)

// ── 项目列表 ──
const projectOptions = ref<{ label: string; value: number }[]>([])

function valueColor(val: string | null) {
  if (val === 'True' || val === '1') return '#67C23A'
  if (val === 'False' || val === '0') return '#F56C6C'
  return '#606266'
}

async function loadProjects() {
  try {
    const res: any = await ProjectAPI.list({ page: 1, limit: 100 })
    // ProjectAPI.list returns { data: [...], total: N }
    const list: any[] = Array.isArray(res?.data) ? res.data : []
    projectOptions.value = list.map((p: any) => ({
      label: p.projectName || `Project ${p.id}`,
      value: p.id,
    }))
    const qid = Number(route.query.projectId)
    if (qid && projectOptions.value.some(p => p.value === qid)) {
      projectId.value = qid
    }
    else if (projectOptions.value.length > 0) {
      projectId.value = projectOptions.value[0].value
    }
  }
  catch {
    // ignore
  }
}

async function fetchData() {
  if (!projectId.value) {
    ElMessage.warning(t('signal_history.pleaseSelectProject'))
    return
  }
  loading.value = true
  try {
    const res: any = await StopCauseDiagnosisAPI.getMonitoringHistory({
      projectId: projectId.value,
      stationCode: stationCode.value || undefined,
      keyword: keyword.value || undefined,
      page: page.value,
      pageSize: pageSize.value,
    })
    const list = res?.data ?? []
    tableData.value = list
    totalCount.value = res?.total ?? res?.count ?? list.length
  }
  catch (err: any) {
    ElMessage.error(err?.message || t('signal_history.queryFailed'))
  }
  finally {
    loading.value = false
  }
}

function handleSearch() {
  page.value = 1
  fetchData()
}

function handleReset() {
  stationCode.value = ''
  keyword.value = ''
  page.value = 1
  pageSize.value = 50
  fetchData()
}

function onPageChange(val: number) {
  page.value = val
  fetchData()
}

function onSizeChange(val: number) {
  pageSize.value = val
  page.value = 1
  fetchData()
}

// 按工位统计
const stationStats = ref<{ stationCode: string; count: number }[]>([])
function computeStationStats() {
  const map: Record<string, number> = {}
  for (const row of tableData.value) {
    const code = row.stationCode || t('signal_history.unassigned')
    map[code] = (map[code] || 0) + 1
  }
  stationStats.value = Object.entries(map)
    .map(([sc, cnt]) => ({ stationCode: sc, count: cnt }))
    .sort((a, b) => b.count - a.count)
}

watch(tableData, computeStationStats)

onMounted(async () => {
  await loadProjects()
  if (projectId.value) {
    fetchData()
  }
})
</script>

<template>
  <div class="h-full">
    <FaPageMain class="h-full flex flex-col">
      <FaSearchBar :show-toggle="false">
        <template #default>
          <el-form inline class="w-full">
            <el-form-item :label="$t('signal_history.project')">
              <el-select
                v-model="projectId"
                :placeholder="$t('signal_history.selectProject')"
                style="width: 200px"
                @change="handleSearch"
              >
                <el-option
                  v-for="opt in projectOptions"
                  :key="opt.value"
                  :label="opt.label"
                  :value="opt.value"
                />
              </el-select>
            </el-form-item>
            <el-form-item :label="$t('signal_history.station')">
              <el-input
                v-model="stationCode"
                :placeholder="$t('signal_history.stationPlaceholder')"
                clearable
                style="width: 130px"
              />
            </el-form-item>
            <el-form-item :label="$t('signal_history.keywordLabel')">
              <el-input
                v-model="keyword"
                :placeholder="$t('signal_history.keywordPlaceholder')"
                clearable
                style="width: 200px"
              />
            </el-form-item>
            <el-form-item>
              <el-button type="primary" :loading="loading" @click="handleSearch">
                <FaIcon name="i-ep:search" class="mr-1" />
                {{ $t('signal_history.search') }}
              </el-button>
              <el-button @click="handleReset">
                {{ $t('common.reset') }}
              </el-button>
            </el-form-item>
            <el-form-item v-if="totalCount > 0">
              <el-tag type="info">
                {{ $t('signal_history.totalRecords', { count: totalCount }) }}
              </el-tag>
            </el-form-item>
          </el-form>
        </template>
      </FaSearchBar>

      <el-divider border-style="dashed" class="my-4" />

      <!-- 工位分布 -->
      <div v-if="stationStats.length > 0" class="mb-3 flex flex-wrap gap-2">
        <span class="text-sm text-gray-500 self-center mr-1">{{ $t('signal_history.stationDistribution') }}</span>
        <el-tag
          v-for="stat in stationStats"
          :key="stat.stationCode"
          class="cursor-pointer"
          :type="stationCode === stat.stationCode ? 'primary' : 'info'"
          @click="stationCode = stat.stationCode; handleSearch()"
        >
          {{ stat.stationCode }} ({{ stat.count }})
        </el-tag>
      </div>

      <!-- 数据表格 -->
      <el-table
        v-loading="loading"
        :data="tableData"
        stripe
        border
        size="small"
        style="width: 100%; flex: 1;"
        height="100%"
      >
        <el-table-column prop="stationCode" :label="$t('signal_history.station')" width="90" fixed />
        <el-table-column prop="variableName" :label="$t('signal_history.varName')" min-width="200" show-overflow-tooltip />
        <el-table-column prop="signalKey" :label="$t('signal_history.signalKeyLabel')" min-width="160" show-overflow-tooltip />
        <el-table-column :label="$t('signal_history.change')" width="140">
          <template #default="{ row }">
            <span :style="{ color: valueColor(row.oldValue) }">{{ row.oldValue ?? '-' }}</span>
            <span class="mx-1 text-gray-400">→</span>
            <span :style="{ color: valueColor(row.newValue), fontWeight: 600 }">{{ row.newValue ?? '-' }}</span>
          </template>
        </el-table-column>
        <el-table-column prop="changedAt" :label="$t('signal_history.changedAt')" width="160">
          <template #default="{ row }">
            {{ row.changedAt ? new Date(row.changedAt).toLocaleString('zh-CN') : '-' }}
          </template>
        </el-table-column>
        <el-table-column prop="signalType" :label="$t('signal_history.signalType')" width="90" show-overflow-tooltip />
        <el-table-column prop="groupName" :label="$t('signal_history.group')" width="110" show-overflow-tooltip />
        <el-table-column prop="stationMode" :label="$t('signal_history.stationMode')" width="90" />
        <el-table-column prop="quality" :label="$t('signal_history.quality')" width="70">
          <template #default="{ row }">
            <el-tag
              :type="row.quality === 'Good' ? 'success' : row.quality ? 'warning' : 'info'"
              size="small"
            >
              {{ row.quality || '-' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="bundleId" label="Bundle" width="120" show-overflow-tooltip />
        <el-table-column prop="eventId" label="Event ID" width="90" />
        <el-table-column prop="logicUnitId" label="LogicUnit" width="90" />
      </el-table>

      <div class="mt-4 flex justify-end">
        <el-pagination
          v-model:current-page="page"
          v-model:page-size="pageSize"
          :total="totalCount"
          :page-sizes="[20, 50, 100, 200]"
          layout="total, sizes, prev, pager, next, jumper"
          @current-change="onPageChange"
          @size-change="onSizeChange"
        />
      </div>
    </FaPageMain>
  </div>
</template>
