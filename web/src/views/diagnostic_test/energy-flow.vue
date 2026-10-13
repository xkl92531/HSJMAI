<route lang="yaml">
meta:
  title: PLC能量流诊断测试
  icon: i-carbon:energy
</route>

<script setup lang="ts">
import { Delete, Plus } from '@element-plus/icons-vue'
import { ElLoading, ElMessage } from 'element-plus'
import hljs from 'highlight.js'
import { marked } from 'marked'
import mermaid from 'mermaid'
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import api from '@/api'
import messages from './energy-flow.lang'
import 'highlight.js/styles/github.css'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

// 初始化 Mermaid
mermaid.initialize({
  startOnLoad: false,
  theme: 'neutral',
  securityLevel: 'loose',
})

// 配置 marked 使用自定义渲染器
const renderer = new marked.Renderer()

// 重写 code 渲染方法以支持 Mermaid
renderer.code = function (token: any) {
  const code = token.text || token
  const lang = token.lang || token.language || ''

  if (lang === 'mermaid') {
    return `<div class="mermaid">${code}</div>`
  }

  const language = hljs.getLanguage(lang) ? lang : 'plaintext'
  const highlighted = hljs.highlight(code, { language }).value
  return `<pre><code class="hljs language-${language}">${highlighted}</code></pre>`
}

marked.setOptions({
  renderer,
  langPrefix: 'hljs language-',
} as any)

// 逻辑单元类型和ID
const logicUnitType = ref('NETWORK')

// 逻辑单元ID配置
const NETWORK_ID = '4541728597370605572'
const TRANSITION_ID = '4541728597395771432'

const logicUnitId = computed(() => {
  return logicUnitType.value === 'NETWORK' ? NETWORK_ID : TRANSITION_ID
})

// NETWORK 分组配置
const networkGroups = [
  { id: 1, name: 'energyFlow.groupInputParams', uidRange: [21, 26] },
  { id: 2, name: 'energyFlow.groupMopOutput', uidRange: [27, 38] },
]

// TRANSITION 分组配置
const transitionGroups = [
  { id: 1, name: 'energyFlow.groupDb380Check', uidRange: [21, 22] },
  { id: 2, name: 'energyFlow.groupDb580Check', uidRange: [23, 24] },
  { id: 3, name: 'energyFlow.groupDb680Check', uidRange: [25, 26] },
  { id: 4, name: 'energyFlow.groupDb780Check', uidRange: [27, 28] },
]

// 变量分组配置
const variableGroups = ref<Array<{ id: number, name: string, uidRange: number[], collapsed: boolean }>>([])

// 初始化分组
function _initGroups() {
  const groups = logicUnitType.value === 'NETWORK' ? networkGroups : transitionGroups
  variableGroups.value = groups.map(g => ({ ...g, collapsed: false }))
}

// 切换分组折叠状态
function toggleGroup(groupId: number) {
  const group = variableGroups.value.find(g => g.id === groupId)
  if (group) {
    group.collapsed = !group.collapsed
  }
}

// 变量列表
const variableList = ref([
  { uid: '', value: true },
])

// 诊断状态
const isRunning = ref(false)
const duration = ref(0)

// 诊断结果
const diagnosticResult = ref<any>(null)
const rawResponse = ref('')
const singleDiagnosisTyping = ref(false)

// 美化 Markdown 内容
const renderedRawResponse = computed(() => {
  if (!rawResponse.value) {
    return ''
  }
  try {
    return marked.parse(rawResponse.value)
  }
  catch (error) {
    console.error('Markdown 解析失败:', error)
    return rawResponse.value
  }
})

// 自动测试
const autoTestRunning = ref(false)
const autoTestProgress = ref(0)
const autoTestTotal = ref(0)
const autoTestResults = ref<any[]>([])

// 统计指标
const stats = computed(() => {
  const total = variableList.value.length
  const trueCount = variableList.value.filter(v => v.value === true).length
  const falseCount = total - trueCount
  const trueRate = total > 0 ? Math.round((trueCount / total) * 100) : 0

  const testPassed = autoTestResults.value.filter(r => r.success === true).length
  const testFailed = autoTestResults.value.filter(r => r.success === false).length
  const testTotal = autoTestResults.value.length

  return {
    total,
    trueCount,
    falseCount,
    trueRate,
    testPassed,
    testFailed,
    testTotal,
    testPassRate: testTotal > 0 ? Math.round((testPassed / testTotal) * 100) : 0,
  }
})

// 分组变量列表
const groupedVariables = computed(() => {
  return variableGroups.value.map(group => ({
    ...group,
    variables: variableList.value.filter((v) => {
      const uid = Number.parseInt(v.uid) || 0
      return uid >= group.uidRange[0] && uid <= group.uidRange[1]
    }),
  }))
})

// 未分组变量
const ungroupedVariables = computed(() => {
  return variableList.value.filter((v) => {
    const uid = Number.parseInt(v.uid) || 0
    return !variableGroups.value.some(g => uid >= g.uidRange[0] && uid <= g.uidRange[1])
  })
})

// 调试信息
const debugInfo = ref({
  lastRequest: null as any,
  lastResponse: null as any,
  lastError: null as any,
  apiUrl: '',
})

// 重新渲染 Mermaid 图表
async function renderMermaid() {
  await nextTick()
  try {
    const nodes = document.querySelectorAll('.mermaid:not([data-processed="true"])')
    if (nodes.length > 0) {
      await mermaid.run({
        nodes: nodes as any,
      })
    }
  }
  catch (error) {
    console.warn('Mermaid 渲染失败:', error)
  }
}

watch(renderedRawResponse, () => {
  renderMermaid()
})

watch(autoTestResults, () => {
  renderMermaid()
}, { deep: true })

onMounted(() => {
  _initGroups()
  loadSampleData()
  renderMermaid()
})

// 添加变量
function addVariable() {
  const maxUid = variableList.value.reduce((max, v) => {
    const uidNum = Number.parseInt(v.uid) || 0
    return uidNum > max ? uidNum : max
  }, 0)
  variableList.value.push({ uid: String(maxUid + 1), value: true })
}

// 删除变量
function removeVariable(variable: any) {
  const index = variableList.value.indexOf(variable)
  if (index > -1) {
    variableList.value.splice(index, 1)
  }
}

// 批量操作
function setAllTrue() {
  variableList.value.forEach((v) => {
    v.value = true
  })
  ElMessage.success(t('energyFlow.setTrueDone'))
}

function setAllFalse() {
  variableList.value.forEach((v) => {
    v.value = false
  })
  ElMessage.success(t('energyFlow.setFalseDone'))
}

function toggleAllValues() {
  variableList.value.forEach((v) => {
    v.value = !v.value
  })
  ElMessage.success(t('energyFlow.toggleDone'))
}

function randomizeValues() {
  variableList.value.forEach((v) => {
    v.value = Math.random() > 0.5
  })
  ElMessage.success(t('energyFlow.randomizeDone'))
}

function injectRandomErrors(count = 3) {
  const indices = [...Array.from({ length: variableList.value.length }, (_, i) => i)]
  for (let i = indices.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1))
    ;[indices[i], indices[j]] = [indices[j], indices[i]]
  }
  indices.slice(0, Math.min(count, variableList.value.length)).forEach((i) => {
    variableList.value[i].value = false
  })
  ElMessage.success(t('energyFlow.injectDone', { count: Math.min(count, variableList.value.length) }))
}

// JSON 导入导出
function importFromJSON() {
  const input = document.createElement('input')
  input.type = 'file'
  input.accept = '.json'
  input.onchange = (e: any) => {
    const file = e.target.files[0]
    if (!file) {
      return
    }
    const reader = new FileReader()
    reader.onload = (event: any) => {
      try {
        const data = JSON.parse(event.target.result)
        if (Array.isArray(data)) {
          variableList.value = data.map((v: any) => ({
            uid: v.uid || '',
            value: v.value === true || v.value === 'true',
          }))
          ElMessage.success(t('energyFlow.importSuccess'))
        }
        else {
          ElMessage.error(t('energyFlow.jsonFormatError'))
        }
      }
      catch {
        ElMessage.error(t('energyFlow.jsonParseError'))
      }
    }
    reader.readAsText(file)
  }
  input.click()
}

function exportToJSON() {
  const dataStr = JSON.stringify(variableList.value, null, 2)
  const dataBlob = new Blob([dataStr], { type: 'application/json' })
  const url = URL.createObjectURL(dataBlob)
  const link = document.createElement('a')
  link.href = url
  link.download = `variables-${Date.now()}.json`
  link.click()
  URL.revokeObjectURL(url)
  ElMessage.success(t('energyFlow.exportDone'))
}

// 重置表单
function resetForm() {
  variableList.value = [
    { uid: '', value: true },
  ]
  diagnosticResult.value = null
  rawResponse.value = ''
  debugInfo.value.lastRequest = null
  debugInfo.value.lastResponse = null
  debugInfo.value.lastError = null
}

// 场景加载函数
function loadScenarioData(data: Record<string, boolean>) {
  variableList.value = Object.entries(data).map(([uid, value]) => ({
    uid,
    value,
  }))
}

function loadSampleData() {
  loadScenarioData({
    21: true, // 常量True
    22: false, // 常量False
    23: false, // 常量False (Halt=false)
    24: false, // AlarmExtern=false (无外部报警)
    25: true, // Safety_OK=true (安全信号正常)
    26: true, // Genaral=true (通用条件正常)
    27: true, // MOP_AUTO (自动模式开启)
    28: true, // MOP_TAP (TAP激活，与AUTO配合)
    29: true, // MOP_T_PUSH (推送激活)
    30: false, // MOP_MAN (非手动模式)
    31: false, // MOP_HALT (无停机)
    32: false, // MOP_OPS_ZERO
    33: false, // MOP_LOCK (无锁定)
    34: false, // MOP_SUP
    35: false, // MOP_TMS_HALT
    36: true, // MOP_IL_PERM (互锁权限开启)
    37: true, // MOP_T_PERM (权限正常)
    38: false, // MOP_ACK
  } as Record<string, boolean>)
  ElMessage.success(t('energyFlow.loadedScenario1'))
}
function loadScenario2() {
  loadScenarioData({
    21: true, // 常量True
    22: false, // 常量False
    23: false, // 常量False
    24: true, // AlarmExtern=true (⚠️ 外部报警触发)
    25: true, // Safety_OK=true
    26: true, // Genaral=true
    27: false, // MOP_AUTO
    28: false, // MOP_TAP
    29: false, // MOP_T_PUSH
    30: false, // MOP_MAN
    31: true, // MOP_HALT (报警导致停机)
    32: false, // MOP_OPS_ZERO
    33: true, // MOP_LOCK (报警导致锁定)
    34: false, // MOP_SUP
    35: false, // MOP_TMS_HALT
    36: false, // MOP_IL_PERM (互锁权限关闭)
    37: false, // MOP_T_PERM
    38: false, // MOP_ACK
  } as Record<string, boolean>)
  ElMessage.success(t('energyFlow.loadedScenario2'))
}

function loadScenario3() {
  loadScenarioData({
    21: true, // 常量True
    22: false, // 常量False
    23: false, // 常量False
    24: false, // AlarmExtern=false
    25: false, // Safety_OK=false (⚠️ 安全信号丢失)
    26: true, // Genaral=true
    27: false, // MOP_AUTO
    28: false, // MOP_TAP
    29: false, // MOP_T_PUSH
    30: false, // MOP_MAN
    31: true, // MOP_HALT (安全停机)
    32: false, // MOP_OPS_ZERO
    33: true, // MOP_LOCK (安全锁定)
    34: false, // MOP_SUP
    35: false, // MOP_TMS_HALT
    36: false, // MOP_IL_PERM
    37: false, // MOP_T_PERM
    38: false, // MOP_ACK
  } as Record<string, boolean>)
  ElMessage.success(t('energyFlow.loadedScenario3'))
}

function loadScenario4() {
  loadScenarioData({
    21: true, // 常量True
    22: false, // 常量False
    23: true, // 常量False -> Halt=true (⚠️ 手动暂停)
    24: false, // AlarmExtern=false
    25: true, // Safety_OK=true
    26: true, // Genaral=true
    27: false, // MOP_AUTO
    28: false, // MOP_TAP
    29: false, // MOP_T_PUSH
    30: false, // MOP_MAN
    31: true, // MOP_HALT (手动暂停状态)
    32: false, // MOP_OPS_ZERO
    33: false, // MOP_LOCK
    34: false, // MOP_SUP
    35: false, // MOP_TMS_HALT
    36: false, // MOP_IL_PERM
    37: false, // MOP_T_PERM
    38: true, // MOP_ACK (需要确认)
  } as Record<string, boolean>)
  ElMessage.success(t('energyFlow.loadedScenario4'))
}

// TRANSITION 场景
function loadTransitionScenario1() {
  loadScenarioData({
    21: true, // DB380_Parameter.SRV_Sync[2].WorkPositonOK[1]
    22: true, // DB380_Parameter.SRV_Sync[4].WorkPositonOK[1]
    23: true, // DB580_Parameter.SRV_Sync[2].WorkPositonOK[1]
    24: true, // DB580_Parameter.SRV_Sync[4].WorkPositonOK[1]
    25: true, // DB680_Parameter.SRV_Sync[2].WorkPositonOK[1]
    26: true, // DB680_Parameter.SRV_Sync[4].WorkPositonOK[1]
    27: true, // DB780_Parameter.SRV_Sync[2].WorkPositonOK[1]
    28: true, // DB780_Parameter.SRV_Sync[4].WorkPositonOK[1]
  } as Record<string, boolean>)
  ElMessage.success(t('energyFlow.loadedTransition1'))
}

function loadTransitionScenario2() {
  loadScenarioData({
    21: true, // DB380_Parameter.SRV_Sync[2].WorkPositonOK[1]
    22: false, // ⚠️ DB380_Parameter.SRV_Sync[4].WorkPositonOK[1] 未就绪
    23: true, // DB580_Parameter.SRV_Sync[2].WorkPositonOK[1]
    24: true, // DB580_Parameter.SRV_Sync[4].WorkPositonOK[1]
    25: true, // DB680_Parameter.SRV_Sync[2].WorkPositonOK[1]
    26: true, // DB680_Parameter.SRV_Sync[4].WorkPositonOK[1]
    27: true, // DB780_Parameter.SRV_Sync[2].WorkPositonOK[1]
    28: true, // DB780_Parameter.SRV_Sync[4].WorkPositonOK[1]
  } as Record<string, boolean>)
  ElMessage.success(t('energyFlow.loadedTransition2'))
}

function loadTransitionScenario3() {
  loadScenarioData({
    21: true, // DB380_Parameter.SRV_Sync[2].WorkPositonOK[1]
    22: true, // DB380_Parameter.SRV_Sync[4].WorkPositonOK[1]
    23: false, // ⚠️ DB580_Parameter.SRV_Sync[2].WorkPositonOK[1] 未就绪
    24: false, // ⚠️ DB580_Parameter.SRV_Sync[4].WorkPositonOK[1] 未就绪
    25: true, // DB680_Parameter.SRV_Sync[2].WorkPositonOK[1]
    26: true, // DB680_Parameter.SRV_Sync[4].WorkPositonOK[1]
    27: true, // DB780_Parameter.SRV_Sync[2].WorkPositonOK[1]
    28: true, // DB780_Parameter.SRV_Sync[4].WorkPositonOK[1]
  } as Record<string, boolean>)
  ElMessage.success(t('energyFlow.loadedTransition3'))
}

function loadTransitionScenario4() {
  loadScenarioData({
    21: true, // DB380_Parameter.SRV_Sync[2].WorkPositonOK[1]
    22: true, // DB380_Parameter.SRV_Sync[4].WorkPositonOK[1]
    23: true, // DB580_Parameter.SRV_Sync[2].WorkPositonOK[1]
    24: true, // DB580_Parameter.SRV_Sync[4].WorkPositonOK[1]
    25: true, // DB680_Parameter.SRV_Sync[2].WorkPositonOK[1]
    26: false, // ⚠️ DB680_Parameter.SRV_Sync[4].WorkPositonOK[1] 未就绪
    27: true, // DB780_Parameter.SRV_Sync[2].WorkPositonOK[1]
    28: false, // ⚠️ DB780_Parameter.SRV_Sync[4].WorkPositonOK[1] 未就绪
  } as Record<string, boolean>)
  ElMessage.success(t('energyFlow.loadedTransition4'))
}

// 运行诊断
async function runDiagnosis() {
  if (variableList.value.length === 0) {
    ElMessage.warning(t('energyFlow.needOneVar'))
    return
  }

  const hasEmptyFields = variableList.value.some(v => !v.uid.trim())
  if (hasEmptyFields) {
    ElMessage.warning(t('energyFlow.fillAllUid'))
    return
  }

  isRunning.value = true
  diagnosticResult.value = null
  rawResponse.value = ''
  singleDiagnosisTyping.value = false
  autoTestResults.value = []

  debugInfo.value.lastRequest = null
  debugInfo.value.lastResponse = null
  debugInfo.value.lastError = null

  const startTime = Date.now()

  await nextTick()
  scrollToLatestResult()
  await new Promise(resolve => setTimeout(resolve, 200))

  try {
    const liveSignals: any = {}
    variableList.value.forEach((v) => {
      if (v.uid.trim()) {
        liveSignals[v.uid] = v.value
      }
    })

    const requestData = {
      conditionDTO: {
        Id: logicUnitId.value,
      },
      liveSignals,
    }

    debugInfo.value.lastRequest = requestData
    debugInfo.value.apiUrl = '/DiagnosticStrategyFactory/DiagnoseEnergyFlow'

    const response = await api.post('/DiagnosticStrategyFactory/DiagnoseEnergyFlow', requestData) as any

    duration.value = Date.now() - startTime
    debugInfo.value.lastResponse = response

    if (response && response.isSuccess) {
      // 解析后端返回的 JSON 结果
      const backResult = response.backResult
      let parsedResult: any = null

      try {
        if (typeof backResult === 'string') {
          parsedResult = JSON.parse(backResult)
        }
        else {
          parsedResult = backResult
        }
      }
      catch {
        // 解析失败，直接作为原始文本处理
        parsedResult = { raw: backResult }
      }

      diagnosticResult.value = parsedResult

      // 后端返回统一格式，Summary 已是 Markdown 格式
      if (parsedResult && parsedResult.summary) {
        const stats = parsedResult.statistics
        rawResponse.value = `## 📊 ${t('energyFlow.reportTitle')}

**${t('energyFlow.statusLabel')}**: ${getStatusLabel(parsedResult.status)} | **${t('energyFlow.timeLabel')}**: ${new Date().toLocaleString()}

### 📈 ${t('energyFlow.statsSection')}

| ${t('energyFlow.metricLabel')} | ${t('energyFlow.valueLabel')} |
|------|------|
| ${t('energyFlow.totalIssues')} | ${stats?.totalIssues || 0} |
| 🔴 ${t('energyFlow.criticalCauses')} | ${stats?.criticalCount || 0} |
| 🟡 ${t('energyFlow.indirectCauses')} | ${stats?.warningCount || 0} |
| ${t('energyFlow.analyzedVars')} | ${stats?.analyzedVariables || 0} |

### 🔍 ${t('energyFlow.summarySection')}

${parsedResult.summary}`
      }
      else if (parsedResult && parsedResult.message) {
        rawResponse.value = `## 📊 ${t('energyFlow.reportTitle')}\n\n${parsedResult.message}`
      }
      else {
        rawResponse.value = typeof backResult === 'string' ? backResult : JSON.stringify(backResult, null, 2)
      }

      singleDiagnosisTyping.value = true

      await nextTick()
      scrollToLatestResult()

      await new Promise(resolve => setTimeout(resolve, 600))
      singleDiagnosisTyping.value = false

      ElMessage.success(t('energyFlow.diagnosisDoneMs', { ms: duration.value }))
    }
    else {
      debugInfo.value.lastError = response
      ElMessage.error(t('energyFlow.diagnosisFailed', { msg: response.errorMessage || t('energyFlow.unknownError') }))
    }
  }
  catch (error: any) {
    debugInfo.value.lastError = error
    console.error('诊断失败:', error)
    ElMessage.error(t('energyFlow.diagnosisFailed', { msg: error.message || t('energyFlow.networkError') }))
  }
  finally {
    isRunning.value = false
  }
}

// 获取状态标签
function getStatusLabel(status: string): string {
  const statusMap: Record<string, string> = {
    normal: t('energyFlow.statusNormal'),
    warning_only: t('energyFlow.statusWarningOnly'),
    diagnosed: t('energyFlow.statusDiagnosed'),
    partial: t('energyFlow.statusPartial'),
    error: t('energyFlow.statusErrorLabel'),
  }
  return statusMap[status] || status || t('energyFlow.statusUnknown')
}

// 导出结果
function exportResult() {
  if (!diagnosticResult.value && !rawResponse.value) {
    ElMessage.warning(t('energyFlow.noExportResult'))
    return
  }

  const data = {
    diagnosticResult: diagnosticResult.value,
    rawResponse: rawResponse.value,
    timestamp: new Date().toISOString(),
  }

  const dataStr = JSON.stringify(data, null, 2)
  const dataBlob = new Blob([dataStr], { type: 'application/json' })
  const url = URL.createObjectURL(dataBlob)
  const link = document.createElement('a')
  link.href = url
  link.download = `energy-flow-diagnosis-${Date.now()}.json`
  link.click()
  URL.revokeObjectURL(url)
  ElMessage.success(t('energyFlow.exportResultDone'))
}

// 复制结果
function copyResult() {
  if (!rawResponse.value) {
    ElMessage.warning(t('energyFlow.noCopyResult'))
    return
  }

  navigator.clipboard.writeText(rawResponse.value).then(() => {
    ElMessage.success(t('energyFlow.copyDone'))
  }).catch((err) => {
    console.error('复制失败:', err)
    ElMessage.error(t('energyFlow.copyFailed'))
  })
}

// 构建场景数据
function buildScenarioData(scenarioFunc: () => void): any {
  const originalList = JSON.parse(JSON.stringify(variableList.value))
  scenarioFunc()

  const liveSignals: any = {}
  variableList.value.forEach((v) => {
    if (v.uid.trim()) {
      liveSignals[v.uid] = v.value
    }
  })

  variableList.value = originalList
  return liveSignals
}

// 自动测试
async function runAutoTest() {
  autoTestRunning.value = true
  autoTestResults.value = []
  autoTestProgress.value = 0

  diagnosticResult.value = null
  rawResponse.value = ''
  singleDiagnosisTyping.value = false

  debugInfo.value.lastRequest = null
  debugInfo.value.lastResponse = null
  debugInfo.value.lastError = null

  const scenarios = logicUnitType.value === 'NETWORK'
    ? [
        { name: 'energyFlow.scenario1Network', loadFunc: loadSampleData },
        { name: 'energyFlow.scenario2Network', loadFunc: loadScenario2 },
        { name: 'energyFlow.scenario3Network', loadFunc: loadScenario3 },
        { name: 'energyFlow.scenario4Network', loadFunc: loadScenario4 },
      ]
    : [
        { name: 'energyFlow.scenario1Transition', loadFunc: loadTransitionScenario1 },
        { name: 'energyFlow.scenario2Transition', loadFunc: loadTransitionScenario2 },
        { name: 'energyFlow.scenario3Transition', loadFunc: loadTransitionScenario3 },
        { name: 'energyFlow.scenario4Transition', loadFunc: loadTransitionScenario4 },
      ]

  autoTestTotal.value = scenarios.length
  await nextTick()

  try {
    for (let i = 0; i < scenarios.length; i++) {
      const scenario = scenarios[i]
      autoTestProgress.value = i

      autoTestResults.value.push({
        scenarioName: scenario.name,
        success: null,
        loading: true,
      })

      await nextTick()
      scrollToLatestResult()
      await new Promise(resolve => setTimeout(resolve, 200))

      try {
        const startTime = Date.now()
        const liveSignals = buildScenarioData(scenario.loadFunc)
        const requestData = {
          conditionDTO: { Id: logicUnitId.value },
          liveSignals,
        }

        const response = await api.post('/DiagnosticStrategyFactory/DiagnoseEnergyFlow', requestData) as any
        const testDuration = Date.now() - startTime

        if (response && response.isSuccess) {
          // 解析后端返回的 JSON 结果
          const backResult = response.backResult
          let parsedResult: any = null

          try {
            if (typeof backResult === 'string') {
              parsedResult = JSON.parse(backResult)
            }
            else {
              parsedResult = backResult
            }
          }
          catch {
            parsedResult = { raw: backResult }
          }

          // 生成 Markdown 报告
          let renderedMarkdown = ''
          if (parsedResult && parsedResult.summary) {
            const stats = parsedResult.statistics
            renderedMarkdown = `## 📊 ${t('energyFlow.reportTitle')} - ${t(scenario.name)}

**${t('energyFlow.statusLabel')}**: ${getStatusLabel(parsedResult.status)} | **${t('energyFlow.durationLabel')}**: ${testDuration}ms

### 📈 ${t('energyFlow.statsSection')}

| ${t('energyFlow.metricLabel')} | ${t('energyFlow.valueLabel')} |
|------|------|
| ${t('energyFlow.totalIssues')} | ${stats?.totalIssues || 0} |
| 🔴 ${t('energyFlow.criticalCauses')} | ${stats?.criticalCount || 0} |
| 🟡 ${t('energyFlow.indirectCauses')} | ${stats?.warningCount || 0} |
| ${t('energyFlow.analyzedVars')} | ${stats?.analyzedVariables || 0} |

### 🔍 ${t('energyFlow.summarySection')}

${parsedResult.summary}`
          }
          else if (parsedResult && parsedResult.message) {
            renderedMarkdown = `## 📊 ${t('energyFlow.reportTitle')} - ${t(scenario.name)}\n\n${parsedResult.message}`
          }
          else {
            renderedMarkdown = typeof backResult === 'string' ? backResult : JSON.stringify(backResult, null, 2)
          }

          autoTestResults.value[i] = {
            scenarioName: scenario.name,
            success: true,
            result: parsedResult,
            rawResponse: renderedMarkdown,
            duration: testDuration,
            liveSignals,
            typing: true,
          }

          await nextTick()
          scrollToLatestResult()

          await new Promise(resolve => setTimeout(resolve, 600))
          if (autoTestResults.value[i]) {
            autoTestResults.value[i].typing = false
          }

          await new Promise(resolve => setTimeout(resolve, 300))
        }
        else {
          autoTestResults.value[i] = {
            scenarioName: scenario.name,
            success: false,
            error: response?.errorMessage || t('energyFlow.diagnosisFailedShort'),
          }
        }
      }
      catch (error: any) {
        autoTestResults.value[i] = {
          scenarioName: scenario.name,
          success: false,
          error: error.message || t('energyFlow.requestFailed'),
        }
      }

      await nextTick()
      scrollToLatestResult()
      autoTestProgress.value = i + 1
    }

    ElMessage.success(t('energyFlow.autoTestDone'))
  }
  catch (error: any) {
    ElMessage.error(t('energyFlow.autoTestFailed', { msg: error.message }))
  }
  finally {
    autoTestRunning.value = false
  }
}

// 滚动到最新结果
function scrollToLatestResult() {
  const container = document.querySelector('.results-container')
  if (container) {
    const lastItem = container.lastElementChild
    if (lastItem) {
      lastItem.scrollIntoView({ behavior: 'smooth', block: 'nearest' })
    }
  }
}

// 处理场景选择
function handleScenarioCommand(command: string) {
  if (command === 'switchType') {
    logicUnitType.value = logicUnitType.value === 'NETWORK' ? 'TRANSITION' : 'NETWORK'

    diagnosticResult.value = null
    rawResponse.value = ''
    autoTestResults.value = []
    debugInfo.value.lastRequest = null
    debugInfo.value.lastResponse = null
    debugInfo.value.lastError = null

    // 重新初始化分组并加载对应类型的第一个场景
    _initGroups()
    if (logicUnitType.value === 'NETWORK') {
      loadSampleData()
    }
    else {
      loadTransitionScenario1()
    }

    ElMessage.success(t('energyFlow.switchedType', { type: logicUnitType.value }))
    return
  }

  if (logicUnitType.value === 'NETWORK') {
    switch (command) {
      case 'scenario1': loadSampleData()
        break
      case 'scenario2': loadScenario2()
        break
      case 'scenario3': loadScenario3()
        break
      case 'scenario4': loadScenario4()
        break
    }
  }
  else {
    switch (command) {
      case 'scenario1': loadTransitionScenario1()
        break
      case 'scenario2': loadTransitionScenario2()
        break
      case 'scenario3': loadTransitionScenario3()
        break
      case 'scenario4': loadTransitionScenario4()
        break
    }
  }
}
</script>

<template>
  <div class="energy-flow-page">
    <!-- 顶部统计面板 -->
    <div class="stats-panel">
      <div class="stats-row">
        <div class="stat-item">
          <div class="stat-icon stat-icon--cyan">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M4 6h16M4 12h16M4 18h16" />
            </svg>
          </div>
          <div class="stat-content">
            <span class="stat-value">{{ stats.total }}</span>
            <span class="stat-label">{{ $t('energyFlow.statTotalVars') }}</span>
          </div>
        </div>

        <div class="stat-item">
          <div class="stat-icon stat-icon--green">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <polyline points="20 6 9 17 4 12" />
            </svg>
          </div>
          <div class="stat-content">
            <span class="stat-value">{{ stats.trueCount }}</span>
            <span class="stat-label">{{ $t('energyFlow.statTrueCount') }}</span>
          </div>
        </div>

        <div class="stat-item">
          <div class="stat-icon stat-icon--red">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <line x1="18" y1="6" x2="6" y2="18" />
              <line x1="6" y1="6" x2="18" y2="18" />
            </svg>
          </div>
          <div class="stat-content">
            <span class="stat-value">{{ stats.falseCount }}</span>
            <span class="stat-label">{{ $t('energyFlow.statFalseCount') }}</span>
          </div>
        </div>

        <div class="stat-item stat-item--accent">
          <div class="stat-icon stat-icon--purple">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <circle cx="12" cy="12" r="10" />
              <polyline points="12 6 12 12 16 14" />
            </svg>
          </div>
          <div class="stat-content">
            <span class="stat-value">{{ duration }}ms</span>
            <span class="stat-label">{{ $t('energyFlow.statDuration') }}</span>
          </div>
        </div>

        <div v-if="autoTestResults.length > 0" class="stat-item">
          <div class="stat-icon stat-icon--gold">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M22 11.08V12a10 10 0 1 1-5.93-9.14" />
              <polyline points="22 4 12 14.01 9 11.01" />
            </svg>
          </div>
          <div class="stat-content">
            <span class="stat-value">{{ stats.testPassed }}/{{ stats.testTotal }}</span>
            <span class="stat-label">{{ $t('energyFlow.statTestPassed') }}</span>
          </div>
        </div>

        <div v-if="autoTestRunning" class="stat-item stat-item--progress">
          <div class="progress-info">
            <span class="progress-label">{{ $t('energyFlow.autoTestProgressLabel') }}</span>
            <span class="progress-value">{{ autoTestProgress }}/{{ autoTestTotal }}</span>
          </div>
          <el-progress
            :percentage="Math.round((autoTestProgress / autoTestTotal) * 100)"
            :show-text="false"
            :stroke-width="6"
            color="#00D9FF"
          />
        </div>
      </div>
    </div>

    <!-- 页面标题 -->
    <PageHeader
      :title="$t('energyFlow.pageTitle')"
      :content="$t('energyFlow.pageIntro')"
    >
      <div class="header-actions">
        <!-- 类型切换 -->
        <div class="type-toggle" @click="handleScenarioCommand('switchType')">
          <div class="toggle-track" :class="{ active: logicUnitType === 'TRANSITION' }">
            <div class="toggle-thumb">
              <span class="toggle-icon">
                <svg v-if="logicUnitType === 'NETWORK'" viewBox="0 0 24 24" fill="currentColor" width="14" height="14">
                  <path d="M4 6h16v2H4zm0 5h16v2H4zm0 5h16v2H4z" />
                </svg>
                <svg v-else viewBox="0 0 24 24" fill="currentColor" width="14" height="14">
                  <path d="M12 2L2 7l10 5 10-5-10-5zM2 17l10 5 10-5M2 12l10 5 10-5" />
                </svg>
              </span>
            </div>
          </div>
          <span class="toggle-label">{{ logicUnitType }}</span>
        </div>

        <ElDropdown @command="handleScenarioCommand">
          <div class="dropdown-trigger">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="18" height="18">
              <polygon points="13 2 3 14 12 14 11 22 21 10 12 10 13 2" />
            </svg>
            <span>{{ $t('energyFlow.loadScenario') }}</span>
            <svg class="arrow" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="14" height="14">
              <polyline points="6 9 12 15 18 9" />
            </svg>
          </div>
          <template #dropdown>
            <ElDropdownMenu class="cyber-dropdown">
              <template v-if="logicUnitType === 'NETWORK'">
                <ElDropdownItem command="scenario1">
                  <span class="dropdown-icon">✅</span> {{ $t('energyFlow.scenario1Network') }}
                </ElDropdownItem>
                <ElDropdownItem command="scenario2">
                  <span class="dropdown-icon">⚠️</span> {{ $t('energyFlow.scenario2Network') }}
                </ElDropdownItem>
                <ElDropdownItem command="scenario3">
                  <span class="dropdown-icon">🔧</span> {{ $t('energyFlow.scenario3Network') }}
                </ElDropdownItem>
                <ElDropdownItem command="scenario4">
                  <span class="dropdown-icon">⏸️</span> {{ $t('energyFlow.scenario4Network') }}
                </ElDropdownItem>
              </template>
              <template v-else>
                <ElDropdownItem command="scenario1">
                  <span class="dropdown-icon">✅</span> {{ $t('energyFlow.scenario1Transition') }}
                </ElDropdownItem>
                <ElDropdownItem command="scenario2">
                  <span class="dropdown-icon">⚠️</span> {{ $t('energyFlow.scenario2Transition') }}
                </ElDropdownItem>
                <ElDropdownItem command="scenario3">
                  <span class="dropdown-icon">⚠️</span> {{ $t('energyFlow.scenario3Transition') }}
                </ElDropdownItem>
                <ElDropdownItem command="scenario4">
                  <span class="dropdown-icon">🔧</span> {{ $t('energyFlow.scenario4Transition') }}
                </ElDropdownItem>
              </template>
            </ElDropdownMenu>
          </template>
        </ElDropdown>

        <ElDropdown>
          <div class="dropdown-trigger">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="18" height="18">
              <circle cx="12" cy="12" r="3" />
              <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1 0 2.83 2 2 0 0 1-2.83 0l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-2 2 2 2 0 0 1-2-2v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83 0 2 2 0 0 1 0-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1-2-2 2 2 0 0 1 2-2h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 0-2.83 2 2 0 0 1 2.83 0l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 2-2 2 2 0 0 1 2 2v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 0 2 2 0 0 1 0 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 2 2 2 2 0 0 1-2 2h-.09a1.65 1.65 0 0 0-1.51 1z" />
            </svg>
            <span>{{ $t('energyFlow.batchOps') }}</span>
            <svg class="arrow" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="14" height="14">
              <polyline points="6 9 12 15 18 9" />
            </svg>
          </div>
          <template #dropdown>
            <ElDropdownMenu class="cyber-dropdown">
              <ElDropdownItem @click="setAllTrue">
                <span class="dropdown-icon">✓</span> {{ $t('energyFlow.setAllTrue') }}
              </ElDropdownItem>
              <ElDropdownItem @click="setAllFalse">
                <span class="dropdown-icon">✗</span> {{ $t('energyFlow.setAllFalse') }}
              </ElDropdownItem>
              <ElDropdownItem @click="toggleAllValues">
                <span class="dropdown-icon">⇄</span> {{ $t('energyFlow.toggleAll') }}
              </ElDropdownItem>
              <ElDropdownItem divided @click="randomizeValues">
                <span class="dropdown-icon">🎲</span> {{ $t('energyFlow.randomize') }}
              </ElDropdownItem>
              <ElDropdownItem @click="injectRandomErrors()">
                <span class="dropdown-icon">💥</span> {{ $t('energyFlow.injectErrors') }}
              </ElDropdownItem>
              <ElDropdownItem divided @click="importFromJSON">
                <span class="dropdown-icon">📥</span> {{ $t('energyFlow.importJson') }}
              </ElDropdownItem>
              <ElDropdownItem @click="exportToJSON">
                <span class="dropdown-icon">📤</span> {{ $t('energyFlow.exportJson') }}
              </ElDropdownItem>
            </ElDropdownMenu>
          </template>
        </ElDropdown>

        <ElButton
          type="success"
          :loading="autoTestRunning"
          class="cyber-btn cyber-btn--success"
          @click="runAutoTest"
        >
          <template #icon>
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="18" height="18">
              <polygon points="5 3 19 12 5 21 5 3" />
            </svg>
          </template>
          {{ $t('energyFlow.autoTestBtn') }}
        </ElButton>

        <ElButton class="cyber-btn cyber-btn--default" @click="resetForm">
          <template #icon>
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="18" height="18">
              <polyline points="1 4 1 10 7 10" />
              <path d="M3.51 15a9 9 0 1 0 2.13-9.36L1 10" />
            </svg>
          </template>
          {{ $t('common.reset') }}
        </ElButton>
      </div>
    </PageHeader>

    <!-- 主要内容区 -->
    <PageMain style="display: flex; flex: 1; overflow: hidden;">
      <ElRow :gutter="20" class="main-content">
        <!-- 左侧：变量控制台 -->
        <ElCol :xs="24" :lg="10" style="display: flex; flex-direction: column; min-height: 0;">
          <div class="console-card variable-console" style="flex: 1; overflow: hidden;">
            <div class="console-header">
              <div class="console-title">
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="22" height="22">
                  <rect x="4" y="4" width="16" height="16" rx="2" />
                  <rect x="9" y="9" width="6" height="6" />
                  <path d="M9 1v3M15 1v3M9 20v3M15 20v3M20 9h3M20 14h3M1 9h3M1 14h3" />
                </svg>
                <span>{{ $t('energyFlow.variableConsole') }}</span>
              </div>
              <div class="console-actions">
                <span class="logic-id">ID: {{ logicUnitId }}</span>
                <ElButton type="primary" size="small" :icon="Plus" circle @click="addVariable" />
              </div>
            </div>

            <!-- 变量列表 -->
            <div class="variable-container">
              <ElScrollbar max-height="100%">
                <div class="variable-groups">
                  <div
                    v-for="group in groupedVariables"
                    :key="group.id"
                    class="variable-group"
                  >
                    <div class="group-header" @click="toggleGroup(group.id)">
                      <svg
                        class="group-arrow"
                        :class="{ collapsed: group.collapsed }"
                        viewBox="0 0 24 24"
                        fill="none"
                        stroke="currentColor"
                        stroke-width="2"
                        width="16"
                        height="16"
                      >
                        <polyline points="9 18 15 12 9 6" />
                      </svg>
                      <span class="group-name">{{ $t(group.name) }}</span>
                      <span class="group-count">{{ group.variables.length }}</span>
                    </div>

                    <div v-show="!group.collapsed" class="group-items">
                      <div
                        v-for="(variable, index) in group.variables"
                        :key="index"
                        class="variable-row"
                        :class="{ 'is-false': variable.value === false }"
                      >
                        <div class="variable-info">
                          <span class="variable-uid">{{ variable.uid }}</span>
                          <span class="variable-status" :class="variable.value ? 'status-true' : 'status-false'">
                            {{ variable.value ? 'ON' : 'OFF' }}
                          </span>
                        </div>
                        <ElSwitch
                          v-model="variable.value"
                          active-text="T"
                          inactive-text="F"
                          class="variable-switch"
                        />
                      </div>
                    </div>
                  </div>

                  <!-- 未分组变量 -->
                  <div class="variable-group">
                    <div class="group-header">
                      <svg class="group-arrow" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="16" height="16">
                        <rect x="3" y="3" width="7" height="7" />
                        <rect x="14" y="3" width="7" height="7" />
                        <rect x="14" y="14" width="7" height="7" />
                        <rect x="3" y="14" width="7" height="7" />
                      </svg>
                      <span class="group-name">{{ $t('energyFlow.otherVariables') }}</span>
                      <span class="group-count">{{ ungroupedVariables.length }}</span>
                    </div>
                    <div class="group-items">
                      <div
                        v-for="(variable, index) in ungroupedVariables"
                        :key="index"
                        class="variable-row"
                        :class="{ 'is-false': variable.value === false }"
                      >
                        <div class="variable-info">
                          <span class="variable-uid">{{ variable.uid }}</span>
                          <span class="variable-status" :class="variable.value ? 'status-true' : 'status-false'">
                            {{ variable.value ? 'ON' : 'OFF' }}
                          </span>
                        </div>
                        <ElSwitch
                          v-model="variable.value"
                          active-text="T"
                          inactive-text="F"
                          class="variable-switch"
                        />
                        <ElButton
                          type="danger"
                          size="small"
                          :icon="Delete"
                          circle
                          class="delete-btn"
                          @click="removeVariable(variable)"
                        />
                      </div>
                    </div>
                  </div>
                </div>
              </ElScrollbar>
            </div>

            <!-- 操作按钮 -->
            <div class="console-footer">
              <ElButton
                type="primary"
                size="large"
                :loading="isRunning"
                class="diagnose-btn"
                @click="runDiagnosis"
              >
                <template #icon>
                  <svg v-if="!isRunning" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="20" height="20">
                    <polygon points="5 3 19 12 5 21 5 3" />
                  </svg>
                  <svg v-else class="animate-spin" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="20" height="20">
                    <path d="M21 12a9 9 0 1 1-6.219-8.56" />
                  </svg>
                </template>
                {{ isRunning ? $t('energyFlow.diagnosing') : $t('energyFlow.startDiagnosis') }}
              </ElButton>
            </div>
          </div>
        </ElCol>

        <!-- 右侧：诊断结果 -->
        <ElCol :xs="24" :lg="14" style="display: flex; flex-direction: column; min-height: 0;">
          <div class="console-card result-console" style="flex: 1; overflow: hidden;">
            <div class="console-header">
              <div class="console-title">
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="22" height="22">
                  <path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z" />
                  <polyline points="3.27 6.96 12 12.01 20.73 6.96" />
                  <line x1="12" y1="22.08" x2="12" y2="12" />
                </svg>
                <span>{{ $t('energyFlow.resultConsole') }}</span>
              </div>
              <div v-if="rawResponse" class="console-actions">
                <ElButton size="small" @click="copyResult">
                  <template #icon>
                    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="16" height="16">
                      <rect x="9" y="9" width="13" height="13" rx="2" />
                      <path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1" />
                    </svg>
                  </template>
                  {{ $t('energyFlow.copyBtn') }}
                </ElButton>
                <ElButton size="small" @click="exportResult">
                  <template #icon>
                    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="16" height="16">
                      <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
                      <polyline points="7 10 12 15 17 10" />
                      <line x1="12" y1="15" x2="12" y2="3" />
                    </svg>
                  </template>
                  {{ $t('common.export') }}
                </ElButton>
              </div>
            </div>

            <!-- 结果显示 -->
            <div class="result-container">
              <ElScrollbar max-height="100%">
                <div v-if="isRunning || rawResponse || autoTestResults.length > 0" class="results-container">
                  <!-- 单次诊断结果 -->
                  <div
                    v-if="isRunning || rawResponse"
                    class="result-card"
                    :class="{ loading: isRunning, typing: singleDiagnosisTyping }"
                  >
                    <div class="result-card-header">
                      <div class="result-status" :class="isRunning ? 'status-running' : 'status-complete'">
                        <svg v-if="isRunning" class="animate-spin" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="18" height="18">
                          <path d="M21 12a9 9 0 1 1-6.219-8.56" />
                        </svg>
                        <svg v-else viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="18" height="18">
                          <polyline points="20 6 9 17 4 12" />
                        </svg>
                        <span>{{ isRunning ? $t('energyFlow.analyzing') : $t('energyFlow.diagnosisComplete') }}</span>
                      </div>
                      <div class="result-meta">
                        <span class="result-time">{{ duration }}ms</span>
                      </div>
                    </div>

                    <div class="result-card-body">
                      <div v-if="isRunning" class="loading-state">
                        <div class="loading-grid">
                          <div v-for="i in 4" :key="i" class="loading-item">
                            <div class="loading-dot" />
                          </div>
                        </div>
                        <p>{{ $t('energyFlow.aiAnalyzing') }}</p>
                      </div>
                      <div v-else-if="rawResponse" class="diagnosis-content">
                        <div class="markdown-content" v-html="renderedRawResponse" />
                      </div>
                    </div>
                  </div>

                  <!-- 自动测试结果 -->
                  <template v-if="autoTestResults.length > 0">
                    <div
                      v-for="(testResult, index) in autoTestResults"
                      :key="index"
                      class="result-card"
                      :class="{ loading: testResult.loading, typing: testResult.typing }"
                    >
                      <div class="result-card-header">
                        <div
                          class="result-status" :class="{
                            'status-running': testResult.loading,
                            'status-complete': testResult.success === true,
                            'status-error': testResult.success === false,
                          }"
                        >
                          <svg v-if="testResult.loading" class="animate-spin" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="18" height="18">
                            <path d="M21 12a9 9 0 1 1-6.219-8.56" />
                          </svg>
                          <svg v-else-if="testResult.success === true" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="18" height="18">
                            <polyline points="20 6 9 17 4 12" />
                          </svg>
                          <svg v-else viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="18" height="18">
                            <line x1="18" y1="6" x2="6" y2="18" />
                            <line x1="6" y1="6" x2="18" y2="18" />
                          </svg>
                          <span>{{ $t(testResult.scenarioName) }}</span>
                        </div>
                        <div class="result-meta">
                          <span v-if="testResult.duration" class="result-time">{{ testResult.duration }}ms</span>
                          <span v-else-if="testResult.error" class="result-error">{{ testResult.error }}</span>
                        </div>
                      </div>

                      <div class="result-card-body">
                        <div v-if="testResult.loading" class="loading-state">
                          <div class="loading-grid">
                            <div v-for="i in 4" :key="i" class="loading-item">
                              <div class="loading-dot" />
                            </div>
                          </div>
                          <p>{{ $t('energyFlow.testing') }}</p>
                        </div>
                        <div v-else-if="testResult.success && testResult.rawResponse" class="diagnosis-content">
                          <div class="markdown-content" v-html="marked.parse(testResult.rawResponse)" />
                        </div>
                        <div v-else-if="!testResult.success" class="error-state">
                          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="48" height="48">
                            <circle cx="12" cy="12" r="10" />
                            <line x1="12" y1="8" x2="12" y2="12" />
                            <line x1="12" y1="16" x2="12.01" y2="16" />
                          </svg>
                          <p>{{ testResult.error || $t('energyFlow.testFailed') }}</p>
                        </div>
                      </div>
                    </div>
                  </template>
                </div>

                <!-- 空状态 -->
                <div v-else class="empty-state">
                  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1" width="64" height="64">
                    <path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z" />
                    <polyline points="3.27 6.96 12 12.01 20.73 6.96" />
                    <line x1="12" y1="22.08" x2="12" y2="12" />
                  </svg>
                  <h3>{{ $t('energyFlow.waitingResult') }}</h3>
                  <p>{{ $t('energyFlow.emptyHint') }}</p>
                </div>
              </ElScrollbar>
            </div>
          </div>
        </ElCol>
      </ElRow>
    </PageMain>
  </div>
</template>

<style scoped lang="scss">
// 赛博霓虹配色变量
$neon-cyan: #00D9FF;
$neon-purple: #BFA6F7;
$neon-pink: #FF00FF;
$neon-green: #00FF88;
$neon-red: #FF3366;
$neon-gold: #FFD700;

// 浅色主题适配白色框架
$bg-light: #D4D9E2;
$bg-white: #FFFFFF;
$bg-card-light: rgba(255, 255, 255, 1);

$border-light: rgba(0, 217, 255, 0.5);
$text-dark: #111827;
$text-secondary-light: #374151;

.energy-flow-page {
  min-height: 100%;
  background: #FFFFFF;
}

// 顶部统计面板
.stats-panel {
  backdrop-filter: blur(10px);
  border-bottom: 1px solid $border-light;
  padding: 16px 24px;
  margin-bottom: 16px;

  .stats-row {
    display: flex;
    flex-wrap: wrap;
    gap: 20px;
    align-items: center;
  }

  .stat-item {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 12px 20px;
    background: #F3F4F6;
    border: 1px solid $border-light;
    border-radius: 12px;
    transition: all 0.3s ease;

    &:hover {
      border-color: $neon-cyan;
      box-shadow: 0 0 20px rgba(0, 217, 255, 0.15);
    }

    &--accent {
      border-color: rgba($neon-purple, 0.6);
      background: linear-gradient(135deg, rgba($neon-purple, 0.12), transparent);
    }

    &--progress {
      flex: 1;
      min-width: 200px;
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
  }

  .stat-icon {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 40px;
    height: 40px;
    border-radius: 10px;

    svg {
      width: 20px;
      height: 20px;
    }

    &--cyan {
      background: rgba($neon-cyan, 0.15);
      color: $neon-cyan;
      border: 1px solid rgba($neon-cyan, 0.3);
    }

    &--green {
      background: rgba($neon-green, 0.15);
      color: $neon-green;
      border: 1px solid rgba($neon-green, 0.3);
    }

    &--red {
      background: rgba($neon-red, 0.15);
      color: $neon-red;
      border: 1px solid rgba($neon-red, 0.3);
    }

    &--purple {
      background: rgba($neon-purple, 0.15);
      color: $neon-purple;
      border: 1px solid rgba($neon-purple, 0.3);
    }

    &--gold {
      background: rgba($neon-gold, 0.15);
      color: $neon-gold;
      border: 1px solid rgba($neon-gold, 0.3);
    }
  }

  .stat-content {
    display: flex;
    flex-direction: column;
    gap: 2px;
  }

  .stat-value {
    font-size: 20px;
    font-weight: 700;
    color: $text-dark;
    font-family: 'SF Mono', 'Consolas', monospace;
  }

  .stat-label {
    font-size: 12px;
    color: $text-secondary-light;
    text-transform: uppercase;
    letter-spacing: 0.5px;
  }

  .progress-info {
    display: flex;
    justify-content: space-between;
    align-items: center;
  }

  .progress-label {
    font-size: 12px;
    color: $text-secondary-light;
    text-transform: uppercase;
  }

  .progress-value {
    font-size: 14px;
    font-weight: 600;
    color: $neon-cyan;
    font-family: 'SF Mono', 'Consolas', monospace;
  }
}

// 页面标题区域
.header-actions {
  display: flex;
  align-items: center;
  gap: 16px;
  flex-wrap: wrap;
  margin-left: 16px;
}

// 类型切换
.type-toggle {
  display: flex;
  align-items: center;
  gap: 10px;
  cursor: pointer;
  padding: 8px 16px;
  border: 1px solid $border-light;
  border-radius: 12px;
  transition: all 0.3s ease;

  &:hover {
    border-color: $neon-purple;
    background: rgba($neon-purple, 0.1);
  }
}

.toggle-track {
  position: relative;
  width: 44px;
  height: 24px;
  background: rgba($neon-red, 0.5);
  border-radius: 12px;
  transition: all 0.3s ease;

  &.active {
    background: rgba($neon-green, 0.5);
  }
}

.toggle-thumb {
  position: absolute;
  top: 2px;
  left: 2px;
  width: 20px;
  height: 20px;
  background: $bg-white;
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: all 0.3s ease;

  .active & {
    left: 22px;
  }
}

.toggle-icon {
  display: flex;
  color: $text-dark;
}

.toggle-label {
  font-size: 14px;
  font-weight: 600;
  color: $text-dark;
  font-family: 'SF Mono', 'Consolas', monospace;
}

// 下拉触发按钮
.dropdown-trigger {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 16px;
  border: 1px solid $border-light;
  border-radius: 12px;
  color: $text-dark;
  font-size: 14px;
  cursor: pointer;
  transition: all 0.3s ease;

  &:hover {
    border-color: $neon-cyan;
    background: rgba($neon-cyan, 0.1);
  }

  .arrow {
    margin-left: 4px;
    transition: transform 0.2s ease;
  }
}

.dropdown-icon {
  margin-right: 8px;
}

// 赛博按钮
.cyber-btn {
  border-radius: 12px !important;
  font-weight: 600 !important;
  transition: all 0.3s ease !important;

  &--success {
    background: linear-gradient(135deg, #00B373, #008558) !important;
    border: 1px solid #00B373 !important;
    color: #FFFFFF !important;

    &:hover {
      box-shadow: 0 0 20px rgba($neon-green, 0.4) !important;
      transform: translateY(-2px);
    }
  }

  &--default {
    border: 1px solid $border-light !important;
    color: $text-dark !important;

    &:hover {
      border-color: $neon-cyan;
      color: $neon-cyan;
    }
  }
}

// 主内容区
.main-content {
  padding: 0 4px;
  margin: 0px !important;
  display: flex;
  flex-direction: row;
  align-items: stretch;
  flex: 1;
  overflow: hidden;
}

// 控制台卡片
.console-card {
  background: #F9FAFB;
  border: 1px solid $border-light;
  border-radius: 16px;
  overflow: hidden;
  display: flex;
  flex-direction: column;
  min-height: 0;

  &:hover {
    border-color: rgba($neon-cyan, 0.6);
    box-shadow: 0 0 30px rgba(0, 217, 255, 0.15);
  }
}

.console-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 16px 20px;
  background: linear-gradient(135deg, rgba($neon-cyan, 0.15), rgba($neon-cyan, 0.05));
  border-bottom: 1px solid $border-light;
}

.console-title {
  display: flex;
  align-items: center;
  gap: 10px;
  font-size: 16px;
  font-weight: 600;
  color: $text-dark;

  svg {
    color: $neon-cyan;
  }
}

.console-actions {
  display: flex;
  align-items: center;
  gap: 12px;
}

.logic-id {
  font-size: 12px;
  font-family: 'SF Mono', 'Consolas', monospace;
  color: $neon-purple;
  padding: 4px 10px;
  background: rgba($neon-purple, 0.1);
  border: 1px solid rgba($neon-purple, 0.3);
  border-radius: 6px;
}

// 变量控制台
.variable-console {
  overflow: hidden;
  min-height: 0;

  .variable-container {
    flex: 1;
    overflow-y: auto;
    padding: 12px;
  }

  .variable-groups {
    display: flex;
    flex-direction: column;
    gap: 12px;
  }

  .variable-group {
    background: #F9FAFB;
    border: 1px solid rgba($neon-cyan, 0.45);
    border-radius: 12px;
    overflow: hidden;
  }

  .group-header {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 12px 16px;
    background: rgba($neon-cyan, 0.1);
    border-bottom: 1px solid rgba($neon-cyan, 0.35);
    cursor: pointer;
    transition: all 0.2s ease;

    &:hover {
      background: rgba($neon-cyan, 0.15);
    }
  }

  .group-arrow {
    color: $neon-cyan;
    transition: transform 0.3s ease;

    &.collapsed {
      transform: rotate(-90deg);
    }
  }

  .group-name {
    flex: 1;
    font-size: 13px;
    font-weight: 600;
    color: $text-dark;
  }

  .group-count {
    font-size: 12px;
    font-family: 'SF Mono', 'Consolas', monospace;
    color: $neon-purple;
    background: rgba($neon-purple, 0.2);
    padding: 2px 8px;
    border-radius: 10px;
  }

  .group-items {
    padding: 8px;
  }

  .variable-row {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 10px 12px;
    margin-bottom: 4px;
    background: #F3F4F6;
    border: 1px solid rgba($neon-cyan, 0.2);
    border-radius: 8px;
    transition: all 0.3s ease;

    &:last-child {
      margin-bottom: 0;
    }

    &:hover {
      background: #FFFFFF;
      border-color: rgba($neon-cyan, 0.5);
    }

    &.is-false {
      background: rgba($neon-red, 0.12);
      border-color: rgba($neon-red, 0.4);

      .variable-uid {
        color: $neon-red;
      }
    }
  }

  .variable-info {
    flex: 1;
    display: flex;
    align-items: center;
    gap: 10px;
  }

  .variable-uid {
    font-size: 14px;
    font-weight: 600;
    font-family: 'SF Mono', 'Consolas', monospace;
    color: $neon-cyan;
  }

  .variable-status {
    font-size: 10px;
    font-weight: 700;
    padding: 2px 8px;
    border-radius: 4px;
    text-transform: uppercase;

    &.status-true {
      background: rgba($neon-green, 0.15);
      color: $neon-green;
      border: 1px solid rgba($neon-green, 0.3);
    }

    &.status-false {
      background: rgba($neon-red, 0.15);
      color: $neon-red;
      border: 1px solid rgba($neon-red, 0.3);
    }
  }

  .variable-switch {
    --el-switch-on-color: #00FF88;
    --el-switch-off-color: #FF3366;
  }

  .delete-btn {
    opacity: 0;
    transition: opacity 0.2s ease;
  }

  .variable-row:hover .delete-btn {
    opacity: 1;
  }

  .console-footer {
    padding: 16px 20px;
    border-top: 1px solid $border-light;
    flex-shrink: 0;
  }

  .diagnose-btn {
    width: 100%;
    height: 48px;
    font-size: 16px;
    font-weight: 600;
    border-radius: 12px !important;
    background: linear-gradient(135deg, rgba($neon-cyan, 0.25), rgba($neon-purple, 0.25)) !important;
    border: 1px solid rgba($neon-cyan, 0.6) !important;
    color: $neon-cyan !important;
    transition: all 0.3s ease !important;

    &:hover:not(:disabled) {
      box-shadow: 0 0 30px rgba($neon-cyan, 0.4) !important;
      transform: translateY(-2px);
    }

    &:disabled {
      opacity: 0.6;
    }
  }
}

// 结果控制台
.result-console {
  overflow: hidden;
  min-height: 0;

  .result-container {
    flex: 1;
    overflow-y: auto;
    padding: 12px;
  }

  .results-container {
    display: flex;
    flex-direction: column;
    gap: 16px;
  }

  .result-card {
    background: #F9FAFB;
    border: 1px solid $border-light;
    border-radius: 12px;
    overflow: hidden;
    animation: fadeInUp 0.4s ease-out;

    &.typing {
      animation: fadeInUp 0.4s ease-out, glowPulse 0.6s ease-in-out;
    }

    &.loading {
      opacity: 0.7;
      border-style: dashed;
    }
  }

  .result-card-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 12px 16px;
    background: #E5E7EB;
    border-bottom: 1px solid rgba($neon-cyan, 0.2);
  }

  .result-status {
    display: flex;
    align-items: center;
    gap: 8px;
    font-size: 14px;
    font-weight: 600;
    color: $text-dark;

    &.status-running {
      color: $neon-cyan;
    }

    &.status-complete {
      color: $neon-green;
    }

    &.status-error {
      color: $neon-red;
    }
  }

  .result-meta {
    display: flex;
    align-items: center;
    gap: 12px;
  }

  .result-time {
    font-size: 12px;
    font-family: 'SF Mono', 'Consolas', monospace;
    color: $neon-purple;
    padding: 4px 10px;
    background: rgba($neon-purple, 0.1);
    border-radius: 6px;
  }

  .result-error {
    font-size: 12px;
    color: $neon-red;
  }

  .result-card-body {
    padding: 16px;
  }

  .loading-state {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    padding: 40px;
    gap: 16px;
    color: $text-secondary-light;

    p {
      margin: 0;
      font-size: 14px;
    }
  }

  .loading-grid {
    display: grid;
    grid-template-columns: repeat(4, 1fr);
    gap: 12px;
  }

  .loading-item {
    display: flex;
    justify-content: center;
  }

  .loading-dot {
    width: 8px;
    height: 8px;
    background: $neon-cyan;
    border-radius: 50%;
    animation: dotPulse 1.5s ease-in-out infinite;

    &:nth-child(2) { animation-delay: 0.2s; }
    &:nth-child(3) { animation-delay: 0.4s; }
    &:nth-child(4) { animation-delay: 0.6s; }
  }

  .diagnosis-content {
    .markdown-content {
      margin: 0;
      padding: 0;
      background: transparent;
      color: $text-secondary-light;
      font-family: 'Consolas', 'Monaco', monospace;
      font-size: 13px;
      line-height: 1.7;

      :deep(pre) {
        margin: 0;
        padding: 0;
        background: transparent;
      }

      :deep(code) {
        font-family: inherit;
      }

      :deep(p) {
        margin-bottom: 0.8em;
        &:last-child { margin-bottom: 0; }
      }

      :deep(h1), :deep(h2), :deep(h3), :deep(h4) {
        color: $neon-cyan;
        margin-top: 1.2em;
        margin-bottom: 0.6em;
      }

      :deep(a) {
        color: $neon-purple;
        &:hover { text-decoration: underline; }
      }

      :deep(blockquote) {
        margin: 1em 0;
        padding-left: 1em;
        border-left: 3px solid $neon-purple;
        color: $neon-purple;
      }

      :deep(table) {
        width: 100%;
        border-collapse: collapse;
        margin: 1em 0;
      }

      :deep(th), :deep(td) {
        padding: 8px 12px;
        border: 1px solid rgba($neon-cyan, 0.3);
      }

      :deep(th) {
        background: rgba($neon-cyan, 0.15);
        color: $neon-cyan;
      }

      :deep(.mermaid) {
        background: #E5E7EB;
        padding: 16px;
        border-radius: 8px;
        margin: 1em 0;
      }
    }
  }

  .error-state {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    padding: 40px;
    color: $neon-red;

    svg {
      margin-bottom: 16px;
      opacity: 0.6;
    }

    p {
      margin: 0;
      font-size: 14px;
    }
  }

  .empty-state {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    height: 100%;
    padding: 60px;
    color: $text-secondary-light;
    text-align: center;

    svg {
      margin-bottom: 24px;
      opacity: 0.3;
    }

    h3 {
      margin: 0 0 12px;
      font-size: 18px;
      color: $text-dark;
    }

    p {
      margin: 0;
      font-size: 14px;
    }
  }
}

// 下拉菜单样式
.cyber-dropdown {
  border: 1px solid $border-light !important;
  backdrop-filter: blur(10px);

  .el-dropdown-menu__item {
    color: $text-dark;
    padding: 10px 16px;

    &:hover {
      background: rgba($neon-cyan, 0.15);
      color: $neon-cyan;
    }
  }
}

// 动画
@keyframes fadeInUp {
  from {
    opacity: 0;
    transform: translateY(20px);
  }
  to {
    opacity: 1;
    transform: translateY(0);
  }
}

@keyframes glowPulse {
  0%, 100% {
    box-shadow: 0 0 10px rgba($neon-cyan, 0.2);
  }
  50% {
    box-shadow: 0 0 25px rgba($neon-cyan, 0.4);
  }
}

@keyframes dotPulse {
  0%, 100% {
    opacity: 0.3;
    transform: scale(0.8);
  }
  50% {
    opacity: 1;
    transform: scale(1.2);
  }
}

.animate-spin {
  animation: spin 1s linear infinite;
}

@keyframes spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}

// 响应式
@media (max-width: 1200px) {
  .console-card {
    height: auto;
    min-height: 500px;
  }

  .stats-panel .stat-item {
    flex: 1;
    min-width: 140px;
  }
}

@media (max-width: 768px) {
  .header-actions {
    gap: 8px;
  }

  .type-toggle {
    padding: 6px 12px;
  }

  .dropdown-trigger span {
    display: none;
  }
}
</style>
