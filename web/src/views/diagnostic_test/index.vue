<route lang="yaml">
meta:
  title: 诊断流程测试
  icon: i-carbon:analytics
</route>

<script setup lang="ts">
import { Delete, Plus } from '@element-plus/icons-vue'
import { ElLoading, ElMessage } from 'element-plus'
import { computed, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import api from '@/api'
import messages from './index.lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

// 不再自动加载Redis数据，由用户手动触发

// 表单数据 - 使用真实TransitionInfo XML结构
const diagnosisForm = reactive({
  productionLineCode: 'LINE_02',
  workstationCode: 'AssemblyStation',
  stepCode: '判断高滑台1无料',
  stuckStatusValue: '1',
  includeCrossReference: true,
  analyzeRootCause: true,
  crossReferenceDepth: 3,
})

// 变量列表 - 基于真实的TransitionInfo XML结构
const variableList = ref([
  // TransitionInfo逻辑：AutoRunMod AND (HomingKeep OR Debug[13]) AND NOT CloseStation3
  { key: 'DB420_OP100Manual.General.AutoRunMod', value: 'false' }, // ❗ 这里为false导致条件不满足
  { key: 'DB420_OP100Manual.General.HomingKeep', value: 'true' },
  { key: 'Debug_Test.Debug[13]', value: 'false' },
  { key: 'DB110_OP010_User.Cmd.CloseStation3', value: 'false' },
])

// 诊断状态
const isRunning = ref(false)
const currentStatus = ref('')
const sessionId = ref('')
const duration = ref(0)

// 诊断步骤
const diagnosticSteps = ref<any[]>([])

// 诊断结果
const diagnosticResult = ref<any>(null)

// 活动标签
const activeTab = ref('overview')

// 调试信息
const debugInfo = ref({
  lastRequest: null as any,
  lastResponse: null as any,
  lastError: null as any,
  apiUrl: '',
})

// Redis数据加载状态
const isLoadingRedisData = ref(false)
const redisDataCount = ref(0)

// 帮助对话框
const showHelpDialog = ref(false)

// 生成真实场景数据
async function generateRealScenarioData() {
  isLoadingRedisData.value = true
  try {
    const response = await api.post('/api/RedisTestData/generate-real-scenario')
    if (response.data && response.data.success) {
      ElMessage.success(`${response.data.message}（${response.data.scenario}）`)
      // 生成后自动刷新
      await fetchRedisData()
    }
  }
  catch (error: any) {
    console.error('生成真实场景数据失败:', error)
    ElMessage.error(t('diagnosticTest.generateFailed', { msg: error.message }))
  }
  finally {
    isLoadingRedisData.value = false
  }
}

// 添加变量
function addVariable() {
  variableList.value.push({ key: '', value: '' })
}

// 删除变量
function removeVariable(index: number) {
  variableList.value.splice(index, 1)
}

// 重置表单
function resetForm() {
  diagnosisForm.productionLineCode = 'LINE_02'
  diagnosisForm.workstationCode = 'AssemblyStation'
  diagnosisForm.stepCode = '判断高滑台1无料'
  diagnosisForm.stuckStatusValue = '1'
  diagnosisForm.includeCrossReference = true
  diagnosisForm.analyzeRootCause = true
  diagnosisForm.crossReferenceDepth = 3
  variableList.value = [
    // TransitionInfo逻辑：AutoRunMod AND (HomingKeep OR Debug[13]) AND NOT CloseStation3
    { key: 'DB420_OP100Manual.General.AutoRunMod', value: 'false' },
    { key: 'DB420_OP100Manual.General.HomingKeep', value: 'true' },
    { key: 'Debug_Test.Debug[13]', value: 'false' },
    { key: 'DB110_OP010_User.Cmd.CloseStation3', value: 'false' },
  ]
  diagnosticSteps.value = []
  diagnosticResult.value = null
  currentStatus.value = ''
  duration.value = 0
  ElMessage.info(t('diagnosticTest.resetDone'))
}

// Redis真实测试数据配置（基于真实TransitionInfo XML结构）
const redisTestData = [
  {
    name: 'LoadingStation',
    label: '上料工站',
    productionLineCode: 'LINE_02',
    workstationCode: 'LoadingStation',
    stepCode: '下链滑台1被达边步',
    variables: [
      // TransitionInfo中的变量示例（基于真实XML）
      { key: 'DB420_OP100Manual.General.AutoRunMod', value: 'true' },
      { key: 'DB420_OP100Manual.General.HomingKeep', value: 'true' },
      { key: 'Debug_Test.Debug[13]', value: 'false' },
      { key: 'DB110_OP010_User.Cmd.CloseStation3', value: 'false' },
    ],
  },
  {
    name: 'PositioningStation',
    label: '定位工站',
    productionLineCode: 'LINE_02',
    workstationCode: 'PositioningStation',
    stepCode: '判断该桌台1状态',
    variables: [
      { key: 'DB420_OP100Manual.General.AutoRunMod', value: 'true' },
      { key: 'DB420_OP100Manual.General.HomingKeep', value: 'false' },
      { key: 'Debug_Test.Debug[13]', value: 'true' },
      { key: 'DB110_OP010_User.Cmd.CloseStation3', value: 'false' },
    ],
  },
  {
    name: 'UnloadingStation',
    label: '下料工站',
    productionLineCode: 'LINE_02',
    workstationCode: 'UnloadingStation',
    stepCode: '落低该桌台1在下链位',
    variables: [
      { key: 'DB420_OP100Manual.General.AutoRunMod', value: 'true' },
      { key: 'DB420_OP100Manual.General.HomingKeep', value: 'true' },
      { key: 'Debug_Test.Debug[13]', value: 'false' },
      { key: 'DB110_OP010_User.Cmd.CloseStation3', value: 'false' },
    ],
  },
  {
    name: 'AssemblyStation',
    label: '装配工站',
    productionLineCode: 'LINE_02',
    workstationCode: 'AssemblyStation',
    stepCode: '判断高滑台1无料',
    variables: [
      // 条件未满足的场景：步骤卡住
      { key: 'DB420_OP100Manual.General.AutoRunMod', value: 'false' }, // ❗ AutoRunMod=false 导致条件不通过
      { key: 'DB420_OP100Manual.General.HomingKeep', value: 'true' },
      { key: 'Debug_Test.Debug[13]', value: 'false' },
      { key: 'DB110_OP010_User.Cmd.CloseStation3', value: 'false' },
    ],
  },
  {
    name: 'InspectionStation',
    label: '检测工站',
    productionLineCode: 'LINE_02',
    workstationCode: 'InspectionStation',
    stepCode: '落低该桌台上在E位',
    variables: [
      { key: 'DB420_OP100Manual.General.AutoRunMod', value: 'true' },
      { key: 'DB420_OP100Manual.General.HomingKeep', value: 'true' },
      { key: 'Debug_Test.Debug[13]', value: 'false' },
      { key: 'DB110_OP010_User.Cmd.CloseStation3', value: 'true' }, // ❗ CloseStation3=true 导致条件不通过（常闭触点）
    ],
  },
]

// 从后端API获取Redis实际数据
async function fetchRedisData() {
  isLoadingRedisData.value = true
  try {
    const response = await api.get('/api/RedisTestData/view')
    if (response.data && response.data.success) {
      const redisKeys = response.data.data || []
      redisDataCount.value = redisKeys.length

      // 解析Redis数据并更新redisTestData
      const parsedData = parseRedisData(redisKeys)
      if (parsedData.length > 0) {
        redisTestData.length = 0 // 清空现有数据
        redisTestData.push(...parsedData)
        ElMessage.success(t('diagnosticTest.loadedStations', { count: parsedData.length }))
      }
      else {
        ElMessage.warning(t('diagnosticTest.noRedisData'))
      }
    }
  }
  catch (error: any) {
    console.error('获取Redis数据失败:', error)
    ElMessage.error(t('diagnosticTest.fetchRedisFailed', { msg: error.message }))
  }
  finally {
    isLoadingRedisData.value = false
  }
}

// 解析Redis数据
// 格式: prod:block:LINE_02:AssemblyStation:判断高滑台1无料:Run:FB460_MainGraph.RT_DATA.EXEC_BITS.EXEC_BITS[5] = 1
function parseRedisData(redisKeys: string[]) {
  const dataMap = new Map<string, any>()

  redisKeys.forEach((line) => {
    // 分割 key = value
    const [key, value] = line.split(' = ')
    if (!key || !key.startsWith('prod:block:')) {
      return
    }

    // 解析key: prod:block:LINE:STATION:STEP:ACTION:VARIABLE
    const parts = key.split(':')
    if (parts.length < 7) {
      return
    }

    const lineCode = parts[2]
    const station = parts[3]
    const step = parts[4]
    const variable = parts.slice(6).join(':') // 变量名可能包含冒号

    // 以工站为键组织数据
    const mapKey = `${lineCode}_${station}`
    if (!dataMap.has(mapKey)) {
      dataMap.set(mapKey, {
        name: station,
        label: station,
        productionLineCode: lineCode,
        workstationCode: station,
        stepCode: step,
        variables: [],
      })
    }

    // 添加变量
    const stationData = dataMap.get(mapKey)
    stationData.variables.push({
      key: variable,
      value: value?.trim() || '1',
    })
  })

  return Array.from(dataMap.values())
}

// 加载测试数据
function loadTestData() {
  // 默认加载装配工站数据（用户指定的测试数据）
  loadRedisTestData(redisTestData[3])
}

// 加载指定Redis测试数据
function loadRedisTestData(data: any) {
  diagnosisForm.productionLineCode = data.productionLineCode
  diagnosisForm.workstationCode = data.workstationCode
  diagnosisForm.stepCode = data.stepCode
  diagnosisForm.stuckStatusValue = '1'
  diagnosisForm.includeCrossReference = true
  diagnosisForm.analyzeRootCause = true
  diagnosisForm.crossReferenceDepth = 3

  variableList.value = [...data.variables]
  ElMessage.success(t('diagnosticTest.loadedStationData', { station: data.workstationCode, step: data.stepCode }))
}

// 执行诊断
async function runDiagnosis() {
  if (!diagnosisForm.productionLineCode) {
    ElMessage.error(t('diagnosticTest.inputLineCode'))
    return
  }

  isRunning.value = true
  diagnosticSteps.value = []
  diagnosticResult.value = null
  currentStatus.value = 'diagnosticTest.statusInit'
  duration.value = 0

  // 不使用全屏loading，只显示状态提示
  ElMessage.info(t('diagnosticTest.startDiagnosisTip'))

  try {
    // 构建请求数据
    const actualValues: Record<string, string> = {}
    variableList.value.forEach((item) => {
      if (item.key && item.value) {
        actualValues[item.key] = item.value
      }
    })

    const requestData = {
      ...diagnosisForm,
      actualValues,
    }

    debugInfo.value.lastRequest = requestData
    debugInfo.value.apiUrl = `${import.meta.env.VITE_APP_API_BASEURL}/api/DiagnosticTest/RunDiagnosis`

    // 调用API
    const response = await api.post('/api/DiagnosticTest/RunDiagnosis', requestData) as any

    debugInfo.value.lastResponse = response
    if (!response) {
      throw new Error(t('diagnosticTest.emptyResponseError'))
    }

    // 添加调试信息
    ElMessage.success(t('diagnosticTest.receivedSteps', { count: response?.progress?.length || 0 }))

    sessionId.value = response.sessionId
    const allSteps = response.progress || []
    diagnosticResult.value = response.result
    currentStatus.value = 'diagnosticTest.statusRunning'

    // 逐步显示步骤，模拟实时效果
    for (let i = 0; i < allSteps.length; i++) {
      // 重要：使用数组解构来触发Vue响应式更新
      diagnosticSteps.value = [...diagnosticSteps.value, allSteps[i]]

      // 每个步骤显示后等待一小段时间
      if (i < allSteps.length - 1) {
        await new Promise(resolve => setTimeout(resolve, 500))
      }
    }

    currentStatus.value = response.summary?.status || 'diagnosticTest.statusComplete'
    duration.value = response.summary?.duration?.toFixed(2) || 0

    ElMessage.success(t('diagnosticTest.diagnosisDone'))
  }
  catch (error: any) {
    debugInfo.value.lastError = {
      message: error.message,
      response: error.response?.data,
      status: error.response?.status,
    }
    console.error('❌ 诊断失败:', error)
    console.error('错误详情:', error.response)
    ElMessage.error(t('diagnosticTest.diagnosisFailed', { msg: error.message || t('diagnosticTest.unknownError') }))
    currentStatus.value = 'diagnosticTest.statusFailed'
  }
  finally {
    isRunning.value = false
  }
}

// 状态标签类型
const statusTagType = computed(() => {
  if (currentStatus.value === 'diagnosticTest.statusComplete') {
    return 'success'
  }
  if (currentStatus.value === 'diagnosticTest.statusRunning') {
    return 'primary'
  }
  if (currentStatus.value === 'diagnosticTest.statusFailed') {
    return 'danger'
  }
  return 'info'
})

// 已知状态键翻译显示，后端返回的状态原样透传
const knownStatusKeys = new Set(['diagnosticTest.statusInit', 'diagnosticTest.statusRunning', 'diagnosticTest.statusComplete', 'diagnosticTest.statusFailed'])
function formatStatus(status: string) {
  return knownStatusKeys.has(status) ? t(status) : status
}

// 获取步骤类型
function getStepType(status: string): 'primary' | 'success' | 'warning' | 'info' | 'danger' {
  const typeMap: Record<string, 'primary' | 'success' | 'warning' | 'info' | 'danger'> = {
    success: 'success',
    error: 'danger',
    warning: 'warning',
    info: 'primary',
    loading: 'primary',
  }
  return typeMap[status] || 'info'
}

// 获取步骤图标
function getStepIcon(status: string) {
  const iconMap: Record<string, any> = {
    success: 'carbon:checkmark-filled',
    error: 'carbon:close-filled',
    warning: 'carbon:warning-filled',
    loading: 'carbon:in-progress',
    info: 'carbon:information-filled',
  }
  return iconMap[status] || 'carbon:dot-mark'
}

// 获取步骤颜色
function getStepColor(status: string) {
  const colorMap: Record<string, string> = {
    success: '#67C23A',
    error: '#F56C6C',
    warning: '#E6A23C',
    loading: '#409EFF',
    info: '#909399',
  }
  return colorMap[status] || '#909399'
}

// 获取状态标签类型
function getStatusTagType(status: string) {
  const typeMap: Record<string, any> = {
    success: 'success',
    error: 'danger',
    warning: 'warning',
    loading: '',
    info: 'info',
  }
  return typeMap[status] || 'info'
}

// 获取状态文本
function getStatusText(status: string) {
  const textMap: Record<string, string> = {
    success: t('diagnosticTest.successText'),
    error: t('diagnosticTest.textError'),
    warning: t('diagnosticTest.textWarning'),
    loading: t('diagnosticTest.statusRunning'),
    info: t('diagnosticTest.textInfo'),
  }
  return textMap[status] || status
}

// 格式化时间戳
function formatTimestamp(timestamp: string) {
  if (!timestamp) {
    return ''
  }
  const date = new Date(timestamp)
  return date.toLocaleTimeString('zh-CN', { hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit' })
}

// 结果JSON
const resultJson = computed(() => {
  return diagnosticResult.value ? JSON.stringify(diagnosticResult.value, null, 2) : ''
})
</script>

<template>
  <div class="diagnostic-test-container">
    <!-- 头部 -->
    <div class="header-section">
      <h1 class="title">
        <icon-carbon:analytics class="title-icon" />
        {{ $t('diagnosticTest.pageTitle') }}
      </h1>
      <p class="subtitle">
        {{ $t('diagnosticTest.pageSubtitle') }}
        <el-button
          type="info"
          size="small"
          text
          style="margin-left: 10px"
          @click="showHelpDialog = true"
        >
          <icon-carbon:help />
          {{ $t('diagnosticTest.viewGuide') }}
        </el-button>
      </p>
    </div>

    <!-- 帮助对话框 -->
    <el-dialog
      v-model="showHelpDialog"
      :title="$t('diagnosticTest.helpTitle')"
      width="80%"
      top="5vh"
    >
      <div style="max-height: 70vh; overflow-y: auto; padding: 20px">
        <h3>{{ $t('diagnosticTest.helpChainTitle') }}</h3>
        <el-steps :active="6" finish-status="success" style="margin: 20px 0">
          <el-step :title="$t('diagnosticTest.stepUserInput')" :description="$t('diagnosticTest.stepUserInputDesc')" />
          <el-step :title="$t('diagnosticTest.stepRedisQuery')" :description="$t('diagnosticTest.stepRedisQueryDesc')" />
          <el-step :title="$t('diagnosticTest.stepAiAnalysis')" :description="$t('diagnosticTest.stepAiAnalysisDesc')" />
          <el-step :title="$t('diagnosticTest.stepDbQuery')" :description="$t('diagnosticTest.stepDbQueryDesc')" />
          <el-step :title="$t('diagnosticTest.stepOutEdge')" :description="$t('diagnosticTest.stepOutEdgeDesc')" />
          <el-step title="TransitionInfo" :description="$t('diagnosticTest.stepTransitionInfoDesc')" />
          <el-step :title="$t('diagnosticTest.stepConditionVerify')" :description="$t('diagnosticTest.stepConditionVerifyDesc')" />
        </el-steps>

        <el-divider />

        <h3>{{ $t('diagnosticTest.helpActualTitle') }}</h3>
        <el-alert type="warning" :closable="false" style="margin: 15px 0">
          <p><strong>{{ $t('diagnosticTest.helpKeyLabel') }}</strong>{{ $t('diagnosticTest.helpVarPrefix') }}<strong>{{ $t('diagnosticTest.helpMust') }}</strong>{{ $t('diagnosticTest.helpVarSuffix') }}</p>
          <p style="margin-top: 10px">
            {{ $t('diagnosticTest.helpExampleIntro') }}
          </p>
          <pre style="background: #f5f7fa; padding: 10px; margin-top: 10px; border-radius: 4px">{ "DB100.DBX0.0": "true" }</pre>
        </el-alert>

        <h3>{{ $t('diagnosticTest.helpSampleTitle') }}</h3>
        <pre style="background: #f5f7fa; padding: 15px; border-radius: 4px; font-size: 12px">
{
  "productionLineCode": "LINE_02",
  "workstationCode": "AssemblyStation",
  "stepCode": "判断高滑台1无料",
  "stuckStatusValue": "1",
  "includeCrossReference": true,
  "analyzeRootCause": true,
  "crossReferenceDepth": 3,
  "actualValues": {
    "FB460_MainGraph.RT_DATA.EXEC_BITS.EXEC_BITS[5]": "1",
    "DB100.DBX0.0": "false",
    "DB100.DBD4": "85.5"
  }
}</pre>

        <h3>{{ $t('diagnosticTest.helpXmlTitle') }}</h3>
        <el-alert type="success" :closable="false" style="margin: 15px 0">
          <p><strong>{{ $t('diagnosticTest.helpRealStructure') }}</strong></p>
          <pre style="background: #f5f7fa; padding: 10px; margin-top: 10px; border-radius: 4px; font-size: 11px">
&lt;Transition Name="下线滑台1顺控启动"&gt;
  &lt;FlgNet&gt;
    &lt;Parts&gt;
      &lt;!-- 变量1: DB420_OP100Manual.General.AutoRunMod --&gt;
      &lt;Access Scope="GlobalVariable" UId="21"&gt;
        &lt;Symbol&gt;
          &lt;Component Name="DB420_OP100Manual" /&gt;
          &lt;Component Name="General" /&gt;
          &lt;Component Name="AutoRunMod" /&gt;
        &lt;/Symbol&gt;
      &lt;/Access&gt;

      &lt;!-- 变量2: DB420_OP100Manual.General.HomingKeep --&gt;
      &lt;Access Scope="GlobalVariable" UId="22"&gt;...&lt;/Access&gt;

      &lt;!-- 变量3: Debug_Test.Debug[13] --&gt;
      &lt;Access Scope="GlobalVariable" UId="23"&gt;...&lt;/Access&gt;

      &lt;!-- 变量4: DB110_OP010_User.Cmd.CloseStation3 --&gt;
      &lt;Access Scope="GlobalVariable" UId="24"&gt;...&lt;/Access&gt;

      &lt;!-- 逻辑门 --&gt;
      &lt;Part Name="Contact" UId="25" /&gt;  &lt;!-- AutoRunMod --&gt;
      &lt;Part Name="Contact" UId="26" /&gt;  &lt;!-- HomingKeep --&gt;
      &lt;Part Name="Contact" UId="27" /&gt;  &lt;!-- Debug[13] --&gt;
      &lt;Part Name="O" UId="28"&gt;...&lt;/Part&gt;  &lt;!-- OR门 --&gt;
      &lt;Part Name="Contact" UId="29"&gt;&lt;Negated /&gt;&lt;/Part&gt;  &lt;!-- NOT CloseStation3 --&gt;
    &lt;/Parts&gt;
  &lt;/FlgNet&gt;
&lt;/Transition&gt;</pre>
          <p style="margin-top: 10px">
            <strong>{{ $t('diagnosticTest.helpLogicFormula') }}</strong>
          </p>
          <pre style="background: #e6f7ff; padding: 10px; border-radius: 4px">
AutoRunMod AND (HomingKeep OR Debug[13]) AND (NOT CloseStation3)</pre>
          <p style="margin-top: 10px">
            <strong>{{ $t('diagnosticTest.helpSatisfiedTitle') }}</strong>
          </p>
          <pre style="background: #f6ffed; padding: 10px; border-radius: 4px">
AutoRunMod = true
HomingKeep = true ({{ $t('diagnosticTest.helpOr') }} Debug[13] = true)
CloseStation3 = false</pre>
          <p style="margin-top: 10px">
            <strong>{{ $t('diagnosticTest.helpUnsatisfiedTitle') }}</strong>
          </p>
          <pre style="background: #fff1f0; padding: 10px; border-radius: 4px">
AutoRunMod = false  ← {{ $t('diagnosticTest.helpFalseExplain') }}
HomingKeep = true
Debug[13] = false
CloseStation3 = false</pre>
        </el-alert>

        <h3>{{ $t('diagnosticTest.helpRedisFormatTitle') }}</h3>
        <pre style="background: #f5f7fa; padding: 15px; border-radius: 4px; font-size: 12px">
{{ $t('diagnosticTest.redisFormatLabel') }}prod:block:{LINE}:{STATION}:{STEP}:{ACTION}:{VARIABLE} = VALUE

{{ $t('diagnosticTest.exampleLabel') }}
prod:block:LINE_02:AssemblyStation:判断高滑台1无料:Run:FB460_MainGraph.RT_DATA.EXEC_BITS.EXEC_BITS[5] = 1</pre>

        <h3>{{ $t('diagnosticTest.helpFlowTitle') }}</h3>
        <ol style="line-height: 2">
          <li>{{ $t('diagnosticTest.flowStep1') }}</li>
          <li>{{ $t('diagnosticTest.flowStep2') }}</li>
          <li>{{ $t('diagnosticTest.flowStep3') }}</li>
          <li>{{ $t('diagnosticTest.flowStep4') }}</li>
          <li>{{ $t('diagnosticTest.flowStep5') }}</li>
        </ol>

        <p style="margin-top: 20px; color: #909399; font-size: 12px">
          {{ $t('diagnosticTest.helpMoreRef') }}<code>api/DIAGNOSTIC_TEST_DATA_GUIDE.md</code>
        </p>
      </div>
    </el-dialog>

    <el-alert type="info" :closable="false" style="margin-bottom: 15px">
      <template #title>
        <div style="display: flex; align-items: center; justify-content: space-between">
          <strong>{{ $t('diagnosticTest.redisSourceTitle') }}</strong>
          <div>
            <el-button
              type="success"
              size="small"
              :loading="isLoadingRedisData"
              style="margin-right: 8px"
              @click="generateRealScenarioData"
            >
              <icon-carbon:magic-wand v-if="!isLoadingRedisData" />
              {{ $t('diagnosticTest.generateScenario') }}
            </el-button>
            <el-button
              type="primary"
              size="small"
              :loading="isLoadingRedisData"
              @click="fetchRedisData"
            >
              <icon-carbon:refresh v-if="!isLoadingRedisData" />
              {{ $t('diagnosticTest.refreshData') }}
            </el-button>
          </div>
        </div>
      </template>
      <div style="margin-top: 8px; font-size: 13px">
        <p v-if="redisDataCount > 0">
          {{ $t('diagnosticTest.redisStatPrefix') }}<strong>{{ redisTestData.length }}</strong>{{ $t('diagnosticTest.redisStatMid') }}<strong>{{ redisDataCount }}</strong>{{ $t('diagnosticTest.redisStatSuffix') }}
        </p>
        <p v-else>
          {{ $t('diagnosticTest.redisConnectedPrefix') }}<strong>LINE_02</strong>{{ $t('diagnosticTest.redisConnectedMid') }}<strong>{{ redisTestData.length }}{{ $t('diagnosticTest.stationUnit') }}</strong>{{ $t('diagnosticTest.redisConnectedSuffix') }}
        </p>
        <p style="margin-top: 5px">
          <el-tag
            v-for="data in redisTestData"
            :key="data.name"
            size="small"
            :type="data.name === 'AssemblyStation' ? 'success' : ''"
            style="margin-right: 6px"
          >
            {{ data.workstationCode }}{{ data.name === 'AssemblyStation' ? $t('diagnosticTest.defaultBadge') : '' }}
          </el-tag>
        </p>
        <p style="margin-top: 8px; color: #67C23A">
          🎯 {{ $t('diagnosticTest.currentTestLabel') }}: <strong>{{ diagnosisForm.workstationCode }} → {{ diagnosisForm.stepCode }}</strong>
        </p>
        <el-divider style="margin: 10px 0" />
        <p style="font-size: 12px; color: #909399; line-height: 1.6">
          <strong>{{ $t('diagnosticTest.processIntro') }}</strong><br>
          {{ $t('diagnosticTest.processLine1') }}<br>
          {{ $t('diagnosticTest.processLine2') }}<br>
          {{ $t('diagnosticTest.processLine3') }}<br>
          {{ $t('diagnosticTest.processLine4') }}<br>
          {{ $t('diagnosticTest.processLine5') }}<br>
          {{ $t('diagnosticTest.processLine6') }}
        </p>
      </div>
    </el-alert>

    <!-- 调试信息面板 -->
    <el-alert
      v-if="debugInfo.lastError"
      type="error"
      :closable="false"
      show-icon
      style="margin-bottom: 15px"
    >
      <template #title>
        <strong>{{ $t('diagnosticTest.apiFailedTitle') }}</strong>
      </template>
      <div style="margin-top: 10px">
        <p><strong>{{ $t('diagnosticTest.errorMsgLabel') }}:</strong> {{ debugInfo.lastError.message }}</p>
        <p v-if="debugInfo.lastError.status">
          <strong>{{ $t('diagnosticTest.httpStatusLabel') }}:</strong> {{ debugInfo.lastError.status }}
        </p>
        <p><strong>{{ $t('diagnosticTest.apiUrlLabel') }}:</strong> {{ debugInfo.apiUrl }}</p>
        <el-button size="small" type="primary" style="margin-top: 10px" @click="debugInfo.lastError = null">
          {{ $t('diagnosticTest.closeBtn') }}
        </el-button>
      </div>
    </el-alert>

    <el-alert
      v-if="debugInfo.lastResponse && !debugInfo.lastError"
      type="success"
      :closable="true"
      show-icon
      style="margin-bottom: 15px"
      @close="debugInfo.lastResponse = null"
    >
      <template #title>
        <strong>{{ $t('diagnosticTest.apiSuccessTitle') }}</strong>
      </template>
      <div style="margin-top: 10px">
        <p><strong>{{ $t('diagnosticTest.sessionIdLabel') }}:</strong> {{ debugInfo.lastResponse.sessionId }}</p>
        <p><strong>{{ $t('diagnosticTest.stepCountLabel') }}:</strong> {{ debugInfo.lastResponse.progress?.length || 0 }}</p>
        <p><strong>{{ $t('diagnosticTest.durationLabel') }}:</strong> {{ debugInfo.lastResponse.summary?.duration?.toFixed(4) }}{{ $t('diagnosticTest.secondsUnit') }}</p>
      </div>
    </el-alert>

    <!-- 测试参数配置 -->
    <el-card class="config-card" shadow="hover">
      <template #header>
        <div class="card-header">
          <icon-carbon:settings class="header-icon" />
          <span>{{ $t('diagnosticTest.configTitle') }}</span>
        </div>
      </template>

      <el-form :model="diagnosisForm" label-width="140px" class="diagnosis-form">
        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item :label="$t('diagnosticTest.lineCodeLabel')" required>
              <el-input v-model="diagnosisForm.productionLineCode" :placeholder="$t('diagnosticTest.lineCodePlaceholder')" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('diagnosticTest.stationCodeLabel')">
              <el-input v-model="diagnosisForm.workstationCode" :placeholder="$t('diagnosticTest.stationCodePlaceholder')" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="20">
          <el-col :span="12">
            <el-form-item :label="$t('diagnosticTest.stepCodeLabel')">
              <el-input v-model="diagnosisForm.stepCode" :placeholder="$t('diagnosticTest.stepCodePlaceholder')" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('diagnosticTest.stuckValueLabel')">
              <el-input v-model="diagnosisForm.stuckStatusValue" :placeholder="$t('diagnosticTest.stuckValuePlaceholder')" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-row :gutter="20">
          <el-col :span="8">
            <el-form-item :label="$t('diagnosticTest.crossRefLabel')">
              <el-switch v-model="diagnosisForm.includeCrossReference" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item :label="$t('diagnosticTest.rootCauseLabel')">
              <el-switch v-model="diagnosisForm.analyzeRootCause" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item :label="$t('diagnosticTest.crossRefDepthLabel')">
              <el-input-number v-model="diagnosisForm.crossReferenceDepth" :min="1" :max="10" />
            </el-form-item>
          </el-col>
        </el-row>

        <el-divider content-position="left">
          {{ $t('diagnosticTest.actualValuesDivider') }}
        </el-divider>

        <el-alert type="warning" :closable="true" style="margin-bottom: 15px">
          <p style="font-size: 12px; margin: 0">
            <strong>{{ $t('diagnosticTest.importantLabel') }}</strong>{{ $t('diagnosticTest.actualImportantText') }}<br>
            <strong>{{ $t('diagnosticTest.exampleLabel2') }}</strong>{{ $t('diagnosticTest.actualExampleText') }}
          </p>
        </el-alert>

        <div class="variable-values">
          <div v-for="(value, index) in variableList" :key="index" class="variable-item">
            <el-input v-model="value.key" :placeholder="$t('diagnosticTest.varNamePlaceholder')" style="width: 45%" />
            <el-input v-model="value.value" :placeholder="$t('diagnosticTest.varValuePlaceholder')" style="width: 45%; margin-left: 10px" />
            <el-button type="danger" :icon="Delete" circle style="margin-left: 10px" @click="removeVariable(index)" />
          </div>
          <el-button type="primary" :icon="Plus" @click="addVariable">
            {{ $t('diagnosticTest.addVariable') }}
          </el-button>
        </div>

        <el-divider />

        <div class="action-buttons">
          <el-button type="primary" size="large" :loading="isRunning" @click="runDiagnosis">
            <icon-carbon:play-filled v-if="!isRunning" />
            {{ $t('diagnosticTest.startDiagnosis') }}
          </el-button>
          <el-button type="default" size="large" @click="resetForm">
            <icon-carbon:reset />
            {{ $t('common.reset') }}
          </el-button>

          <!-- Redis测试数据快速选择 -->
          <el-dropdown split-button type="warning" size="large" @click="loadTestData">
            <icon-carbon:data-1 />
            {{ $t('diagnosticTest.loadTestData') }}
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item
                  v-for="data in redisTestData"
                  :key="data.name"
                  @click="loadRedisTestData(data)"
                >
                  <icon-carbon:building style="margin-right: 8px" />
                  <strong>{{ data.workstationCode }}</strong> - {{ data.stepCode }}
                </el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
        </div>
      </el-form>
    </el-card>

    <!-- 等待执行提示 -->
    <el-empty
      v-if="!isRunning && diagnosticSteps.length === 0 && !diagnosticResult"
      :description="$t('diagnosticTest.emptyDescription')"
      :image-size="200"
      style="margin-top: 40px"
    >
      <template #image>
        <icon-carbon:analytics style="font-size: 120px; color: #409EFF" />
      </template>
      <el-button type="primary" size="large" @click="loadTestData">
        <icon-carbon:data-1 />
        {{ $t('diagnosticTest.quickLoad') }}
      </el-button>
    </el-empty>

    <!-- 调试信息 -->
    <el-card v-if="debugInfo.lastResponse" style="margin-top: 15px; background: #f0f9ff; border: 2px solid #3b82f6;">
      <div style="font-family: monospace; font-size: 12px;">
        <p><strong>{{ $t('diagnosticTest.debugInfoTitle') }}</strong></p>
        <p>isRunning: {{ isRunning }}</p>
        <p>diagnosticSteps.length: {{ diagnosticSteps.length }}</p>
        <p>diagnosticResult: {{ diagnosticResult ? $t('diagnosticTest.setFlag') : $t('diagnosticTest.unsetFlag') }}</p>
        <p>currentStatus: {{ currentStatus }}</p>
        <p>{{ $t('diagnosticTest.sessionIdLabel') }}: {{ sessionId }}</p>
      </div>
    </el-card>

    <!-- 诊断流程追踪 -->
    <el-card v-if="diagnosticSteps.length > 0" class="progress-card" shadow="hover">
      <template #header>
        <div class="card-header">
          <icon-carbon:flow class="header-icon" />
          <span>{{ $t('diagnosticTest.progressTitle') }}</span>
          <el-tag :type="statusTagType" style="margin-left: auto">
            {{ formatStatus(currentStatus) }}
          </el-tag>
          <span v-if="duration" class="duration">{{ $t('diagnosticTest.durationLabel') }}: {{ duration }}s</span>
        </div>
      </template>

      <el-timeline>
        <el-timeline-item
          v-for="(step, index) in diagnosticSteps"
          :key="index"
          :timestamp="formatTimestamp(step.timestamp)"
          :type="getStepType(step.status)"
          :icon="getStepIcon(step.status)"
          :color="getStepColor(step.status)"
        >
          <el-card :class="`step-card step-${step.status}`" shadow="hover">
            <div class="step-header">
              <span class="step-number">#{{ step.stepNumber }}</span>
              <span class="step-name">{{ step.name }}</span>
              <el-tag :type="getStatusTagType(step.status)" size="small">
                {{ getStatusText(step.status) }}
              </el-tag>
            </div>
            <pre class="step-description">{{ step.description }}</pre>
          </el-card>
        </el-timeline-item>
      </el-timeline>
    </el-card>

    <!-- 诊断结果详情 -->
    <el-card v-if="diagnosticResult" class="result-card" shadow="hover">
      <template #header>
        <div class="card-header">
          <icon-carbon:result class="header-icon" />
          <span>{{ $t('diagnosticTest.resultTitle') }}</span>
        </div>
      </template>

      <el-tabs v-model="activeTab" type="border-card">
        <!-- 概览 -->
        <el-tab-pane :label="$t('diagnosticTest.tabOverview')" name="overview">
          <el-descriptions :column="2" border>
            <el-descriptions-item :label="$t('diagnosticTest.diagStatusLabel')">
              <el-tag :type="diagnosticResult.success ? 'success' : 'danger'">
                {{ diagnosticResult.success ? $t('diagnosticTest.successText') : $t('diagnosticTest.failedText') }}
              </el-tag>
            </el-descriptions-item>
            <el-descriptions-item :label="$t('diagnosticTest.stuckStepIdLabel')">
              {{ diagnosticResult.stuckStep?.stepId || 'N/A' }}
            </el-descriptions-item>
            <el-descriptions-item :label="$t('diagnosticTest.stepNameLabel')">
              {{ diagnosticResult.stuckStep?.stepName || 'N/A' }}
            </el-descriptions-item>
            <el-descriptions-item :label="$t('diagnosticTest.outEdgeCountLabel')">
              {{ diagnosticResult.stuckStep?.outgoingConnectionCount || 0 }}
            </el-descriptions-item>
            <el-descriptions-item :label="$t('diagnosticTest.transitionCountLabel')">
              {{ diagnosticResult.outgoingTransitions?.length || 0 }}
            </el-descriptions-item>
            <el-descriptions-item :label="$t('diagnosticTest.varRefCountLabel')">
              {{ diagnosticResult.variableCrossReferences?.length || 0 }}
            </el-descriptions-item>
          </el-descriptions>

          <el-divider content-position="left">
            {{ $t('diagnosticTest.summaryDivider') }}
          </el-divider>
          <el-alert
            v-if="diagnosticResult.summary"
            :title="diagnosticResult.summary"
            type="info"
            :closable="false"
            show-icon
          />
        </el-tab-pane>

        <!-- 转换分析 -->
        <el-tab-pane :label="$t('diagnosticTest.tabTransitions')" name="transitions">
          <el-table :data="diagnosticResult.outgoingTransitions" stripe border>
            <el-table-column prop="transitionId" :label="$t('diagnosticTest.colTransitionId')" width="100" />
            <el-table-column prop="transitionName" :label="$t('diagnosticTest.colTransitionName')" width="150" />
            <el-table-column prop="targetStepName" :label="$t('diagnosticTest.colTargetStep')" width="150" />
            <el-table-column :label="$t('diagnosticTest.colStatus')" width="100">
              <template #default="scope">
                <el-tag :type="scope.row.isPassed ? 'success' : 'danger'">
                  {{ scope.row.isPassed ? $t('diagnosticTest.passedText') : $t('diagnosticTest.notPassedText') }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column :label="$t('diagnosticTest.colConfidence')" width="120">
              <template #default="scope">
                <el-progress :percentage="Math.round(scope.row.score * 100)" :status="scope.row.isPassed ? 'success' : 'exception'" />
              </template>
            </el-table-column>
            <el-table-column :label="$t('diagnosticTest.colVariables')" width="200">
              <template #default="scope">
                <el-tag v-for="v in scope.row.variables" :key="v" size="small" style="margin: 2px">
                  {{ v }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column prop="conditionDetails" :label="$t('diagnosticTest.colConditionDetails')" show-overflow-tooltip />
          </el-table>
        </el-tab-pane>

        <!-- 交叉引用 -->
        <el-tab-pane :label="$t('diagnosticTest.tabCrossRef')" name="crossref">
          <el-table :data="diagnosticResult.variableCrossReferences" stripe border>
            <el-table-column prop="variableName" :label="$t('diagnosticTest.colVariableName')" width="200" />
            <el-table-column prop="variableType" :label="$t('diagnosticTest.colType')" width="120" />
            <el-table-column prop="currentValue" :label="$t('diagnosticTest.colCurrentValue')" width="120" />
            <el-table-column :label="$t('diagnosticTest.colRefCount')" width="100">
              <template #default="scope">
                <el-badge :value="scope.row.references?.length || 0" type="primary">
                  <el-button size="small">
                    {{ $t('diagnosticTest.viewBtn') }}
                  </el-button>
                </el-badge>
              </template>
            </el-table-column>
            <el-table-column :label="$t('diagnosticTest.colAnomalyStatus')" width="120">
              <template #default="scope">
                <el-tag v-if="scope.row.hasAnomalies" type="warning">
                  {{ $t('diagnosticTest.hasAnomaly') }}
                </el-tag>
                <el-tag v-else type="success">
                  {{ $t('common.normal') }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column prop="anomalyDescription" :label="$t('diagnosticTest.colAnomalyDesc')" show-overflow-tooltip />
          </el-table>
        </el-tab-pane>

        <!-- 根因分析 -->
        <el-tab-pane :label="$t('diagnosticTest.tabRootCause')" name="rootcause">
          <div v-if="diagnosticResult.rootCause" class="root-cause">
            <el-descriptions :column="1" border>
              <el-descriptions-item :label="$t('diagnosticTest.problemTypeLabel')">
                <el-tag type="danger">
                  {{ diagnosticResult.rootCause.problemType }}
                </el-tag>
              </el-descriptions-item>
              <el-descriptions-item :label="$t('diagnosticTest.likelihoodLabel')">
                <el-progress :percentage="Math.round(diagnosticResult.rootCause.likelihood * 100)" status="warning" />
              </el-descriptions-item>
              <el-descriptions-item :label="$t('diagnosticTest.descriptionLabel')">
                {{ diagnosticResult.rootCause.description }}
              </el-descriptions-item>
              <el-descriptions-item :label="$t('diagnosticTest.affectedVarsLabel')">
                <el-tag
                  v-for="v in diagnosticResult.rootCause.affectedVariables"
                  :key="v"
                  type="warning"
                  style="margin: 2px"
                >
                  {{ v }}
                </el-tag>
              </el-descriptions-item>
            </el-descriptions>

            <el-divider content-position="left">
              {{ $t('diagnosticTest.solutionsDivider') }}
            </el-divider>
            <el-steps direction="vertical" :active="diagnosticResult.rootCause.suggestedSolutions?.length || 0">
              <el-step
                v-for="(solution, index) in diagnosticResult.rootCause.suggestedSolutions"
                :key="index"
                :title="$t('diagnosticTest.solutionTitle', { index: index + 1 })"
                :description="solution"
                status="process"
              />
            </el-steps>
          </div>
          <el-empty v-else :description="$t('diagnosticTest.noRootCause')" />
        </el-tab-pane>

        <!-- 原始数据 -->
        <el-tab-pane :label="$t('diagnosticTest.tabRawData')" name="raw">
          <el-input
            v-model="resultJson"
            type="textarea"
            :rows="20"
            readonly
            class="json-viewer"
          />
        </el-tab-pane>
      </el-tabs>
    </el-card>
  </div>
</template>

<style scoped lang="scss">
.diagnostic-test-container {
  padding: 20px;
  background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
  min-height: 100vh;
}

.header-section {
  text-align: center;
  color: white;
  margin-bottom: 30px;

  .title {
    font-size: 32px;
    font-weight: bold;
    margin-bottom: 10px;
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 15px;

    .title-icon {
      font-size: 40px;
    }
  }

  .subtitle {
    font-size: 16px;
    opacity: 0.9;
  }
}

.config-card,
.progress-card,
.result-card {
  margin-bottom: 20px;
  border-radius: 10px;
}

.card-header {
  display: flex;
  align-items: center;
  gap: 10px;
  font-weight: bold;
  font-size: 16px;

  .header-icon {
    font-size: 20px;
    color: #409eff;
  }

  .duration {
    margin-left: 10px;
    color: #909399;
  }
}

.diagnosis-form {
  :deep(.el-form-item__label) {
    font-weight: 500;
  }
}

.variable-values {
  .variable-item {
    display: flex;
    align-items: center;
    margin-bottom: 10px;
  }
}

.action-buttons {
  display: flex;
  justify-content: center;
  gap: 15px;
  margin-top: 20px;
}

.step-card {
  margin-top: 10px;
  border-radius: 8px;

  &.step-success {
    border-left: 4px solid #67c23a;
  }

  &.step-error {
    border-left: 4px solid #f56c6c;
  }

  &.step-warning {
    border-left: 4px solid #e6a23c;
  }

  &.step-loading {
    border-left: 4px solid #409eff;
  }

  &.step-info {
    border-left: 4px solid #909399;
  }
}

.step-header {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 10px;

  .step-number {
    font-weight: bold;
    color: #409eff;
  }

  .step-name {
    font-weight: 600;
    flex: 1;
  }
}

.step-description {
  margin: 0;
  padding: 10px;
  background: #f5f7fa;
  border-radius: 4px;
  font-family: 'Consolas', 'Monaco', monospace;
  font-size: 13px;
  white-space: pre-wrap;
  word-break: break-all;
}

.root-cause {
  .el-descriptions {
    margin-bottom: 20px;
  }
}

.json-viewer {
  font-family: 'Consolas', 'Monaco', monospace;
  font-size: 12px;
}

:deep(.el-timeline-item__timestamp) {
  font-size: 12px;
  color: #909399;
}
</style>
