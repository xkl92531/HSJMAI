<route lang="yaml">
meta:
  title: 产线停机诊断 / Line Stop Diagnosis
</route>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import { useRoute } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { ElMessage } from 'element-plus'
import * as signalR from '@microsoft/signalr'
import StopCauseDiagnosisAPI from '@/api/modules/PLC/stopCauseDiagnosis'
import { InfoFilled, MagicStick, Setting } from '@element-plus/icons-vue'
import messages from './line-diagnosis.lang'
import { alarmKindKey, alarmLocationText, alarmSignalsText, alarmTagType, totalActiveAlarms } from './lineDiagnosisAlarms'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

// ── 路由参数 ──
const route = useRoute()
const projectId = computed(() => route.query.projectId as string || '')

// 诊断输出语言跟随应用全局语言（顶栏切换器），不再页内独立切换
// 后端语言码：zh-CN / zh-TW / en-US / ja-JP
const settingsStore = useSettingsStore()
const outputLang = computed<string>(() => {
  const map: Record<string, string> = {
    'zh-cn': 'zh-CN',
    'zh-tw': 'zh-TW',
    'en': 'en-US',
    'ja': 'ja-JP',
  }
  return map[settingsStore.lang] ?? 'zh-CN'
})

// ── 状态 ──
const loading = ref(false)
const phase = ref<'idle' | 'scanning' | 'llm' | 'done' | 'error'>('idle')
const completedCount = ref(0)
const totalCount = ref(0)
const errorMessage = ref('')
const useAgentDiagnosis = ref(false)
const enablePerStationLlm = ref(false)
const includeConstraintAnalysis = ref(true)
const includeDeviceFocusCheck = ref(false)
const defaultDeviceFocusAnchorTypes = ['DeviceHoldState']
const deviceFocusTypeOptions = computed(() => [
  { label: t('line_diagnosis.focusHoldLabel'), value: 'DeviceHoldState', description: t('line_diagnosis.focusHoldDesc') },
  { label: t('line_diagnosis.focusPermitLabel'), value: 'DevicePermitState', description: t('line_diagnosis.focusPermitDesc') },
  { label: t('line_diagnosis.focusMotionLabel'), value: 'DeviceMotionFeedback', description: t('line_diagnosis.focusMotionDesc') },
  { label: t('line_diagnosis.focusAlarmLabel'), value: 'DeviceAlarmState', description: t('line_diagnosis.focusAlarmDesc') },
])
const deviceFocusAnchorTypes = ref<string[]>([...defaultDeviceFocusAnchorTypes])
const settingsDialogVisible = ref(false)
const lastRunOptions = ref({
  includeConstraintAnalysis: true,
  includeDeviceFocusCheck: false,
  deviceFocusAnchorTypes: [] as string[],
})

// 工位数据（实时填充）
const stationMap = ref<Record<string, any>>({})
const stationOrder = ref<string[]>([])
const llmReport = ref<any>(null)
const diagnosisResult = ref<any>(null)

// Agent 推理步骤（实时填充）
interface AgentStep {
  iteration: number
  phase: string
  decision: string
  content: string
  toolName?: string
  toolArgs?: string
  durationMs: number
  success: boolean
  errorMessage?: string
}
const agentSteps = ref<AgentStep[]>([])
const agentStepsExpanded = ref(false)

// Agent 推理阶段 label / icon
function agentPhaseIcon(phase: string): string {
  switch (phase) {
    case 'Thought': return '💭'
    case 'Action': return '🔧'
    case 'Observation': return '👁️'
    case 'Reflection': return '🤔'
    default: return '📌'
  }
}

function agentPhaseLabel(phase: string): string {
  switch (phase) {
    case 'Thought': return t('line_diagnosis.phaseThought')
    case 'Action': return t('line_diagnosis.phaseAction')
    case 'Observation': return t('line_diagnosis.phaseObservation')
    case 'Reflection': return t('line_diagnosis.phaseReflection')
    default: return phase
  }
}

// 进度百分比
const progressPercent = computed(() => {
  if (totalCount.value === 0) return 0
  if (phase.value === 'llm') return 95
  if (phase.value === 'done') return 100
  return Math.round((completedCount.value / totalCount.value) * 90)
})

const progressStatus = computed(() => {
  if (phase.value === 'error') return 'exception'
  if (phase.value === 'done') return 'success'
  return undefined
})

const phaseLabel = computed(() => {
  switch (phase.value) {
    case 'scanning': return t('line_diagnosis.scanning', { done: completedCount.value, total: totalCount.value })
    case 'llm': return useAgentDiagnosis.value ? t('line_diagnosis.llmAgent') : t('line_diagnosis.llmAi')
    case 'done': return t('line_diagnosis.done', { elapsed: diagnosisResult.value?.elapsedMs ?? 0 })
    case 'error': return t('line_diagnosis.error', { msg: errorMessage.value })
    default: return ''
  }
})

// ── 工位状态辅助函数 ──
function stationStatusColor(status: string, role?: string): string {
  if (role === 'RootCause') return '#F56C6C'
  if (role === 'Impacted') return '#909399'
  switch (status) {
    case 'AllTrue': return '#67C23A'
    case 'Running': return '#67C23A'
    case 'SomeFalse': return '#E6A23C'
    case 'NoAst': return '#C0C4CC'
    case 'Skipped': return '#909399'
    case 'Error': return '#F56C6C'
    case 'Pending': return '#409EFF'
    default: return '#C0C4CC'
  }
}

function stationStatusIcon(status: string): string {
  switch (status) {
    case 'AllTrue': return '✅'
    case 'Running': return '🟢'
    case 'SomeFalse': return '⚠️'
    case 'NoAst': return '⬜'
    case 'Skipped': return '⏭️'
    case 'Error': return '❌'
    case 'Pending': return '🔄'
    default: return '⬜'
  }
}

function stationStatusLabel(status: string, role?: string): string {
  if (role === 'RootCause') return t('line_diagnosis.statusRootCause')
  if (role === 'Impacted') return t('line_diagnosis.statusImpacted')
  switch (status) {
    case 'AllTrue': return t('common.normal')
    case 'Running': return t('line_diagnosis.statusRunning')
    case 'SomeFalse': return t('line_diagnosis.statusSomeFalse')
    case 'NoAst': return t('line_diagnosis.statusNoAst')
    case 'Skipped': return t('line_diagnosis.statusSkipped')
    case 'Error': return t('line_diagnosis.errorLabel')
    case 'Pending': return t('line_diagnosis.statusPending')
    default: return t('line_diagnosis.statusWaiting')
  }
}

function formatDiagnosisStrategy(strategy: string): string {
  switch (strategy) {
    case 'StallPointBased': return t('line_diagnosis.strategyStallPointBased')
    case 'SuccessorChainBased': return t('line_diagnosis.strategySuccessorChainBased')
    case 'SkippedNoActiveGraph': return t('line_diagnosis.strategySkippedNoActiveGraph')
    case 'SkippedNoSuccessorCandidate': return t('line_diagnosis.strategySkippedNoSuccessor')
    case 'SkippedFreshActiveGraph': return t('line_diagnosis.strategySkippedFreshGraph')
    case 'GraphDirectEval+FreshGraphFilter': return t('line_diagnosis.strategyGraphDirectEval')
    case 'ActivityDetected': return t('line_diagnosis.strategyActivityDetected')
    default: return strategy
  }
}

function formatTemplateMissReason(reason: string): string {
  switch (reason) {
    case 'UnknownSignature': return t('line_diagnosis.missUnknownSignature')
    case 'NoBundles': return t('line_diagnosis.missNoBundles')
    case 'AllExpectedOccurred': return t('line_diagnosis.missAllExpectedOccurred')
    case 'NoAstMapping': return t('line_diagnosis.missNoAstMapping')
    case 'NoExpectedData': return t('line_diagnosis.missNoExpectedData')
    case 'Error': return t('line_diagnosis.missError')
    default: return reason
  }
}

function getStationRole(stationCode: string): string | undefined {
  if (!llmReport.value?.stationAnalysis) return undefined
  const found = llmReport.value.stationAnalysis.find(
    (s: any) => s.stationCode === stationCode,
  )
  return found?.role
}

const stationSummaries = computed(() =>
  stationOrder.value.map(code => stationMap.value[code]).filter(Boolean),
)
const hasResult = computed(() => phase.value !== 'idle')
const abnormalCount = computed(() =>
  stationSummaries.value.filter(s => s.overallStatus === 'SomeFalse').length,
)
const runningCount = computed(() =>
  stationSummaries.value.filter(s => s.overallStatus === 'Running').length,
)
const enabledSettingCount = computed(() =>
  [
    useAgentDiagnosis.value,
    enablePerStationLlm.value,
    includeConstraintAnalysis.value,
    includeDeviceFocusCheck.value,
  ].filter(Boolean).length,
)
const effectiveDeviceFocusAnchorTypes = computed(() => {
  if (!includeDeviceFocusCheck.value) return []
  return deviceFocusAnchorTypes.value.length > 0
    ? deviceFocusAnchorTypes.value
    : defaultDeviceFocusAnchorTypes
})
const deviceFocusTypeSummary = computed(() => {
  if (!includeDeviceFocusCheck.value) return t('line_diagnosis.disabled')
  const selected = new Set(effectiveDeviceFocusAnchorTypes.value)
  return deviceFocusTypeOptions.value
    .filter(option => selected.has(option.value))
    .map(option => option.label)
    .join(t('line_diagnosis.listSeparator'))
})
const constraintReport = computed(() => diagnosisResult.value?.constraintReport || '')
const activeAlarmTotal = computed(() => totalActiveAlarms(stationSummaries.value))

function isDeviceFocusFinding(finding: any): boolean {
  const phase = finding?.diagnosisPhase || ''
  const ownership = finding?.faultOwnership || ''
  const displayName = finding?.displayName || ''
  return phase === 'DeviceFocusDirect'
    || phase.startsWith('DeviceFocus')
    || ownership === 'LocalInterlock'
    || displayName.includes('设备关注')
}

function deviceFocusFindings(station: any): any[] {
  return (station?.keyFindings || []).filter(isDeviceFocusFinding)
}

function regularFindings(station: any): any[] {
  return (station?.keyFindings || []).filter((finding: any) => !isDeviceFocusFinding(finding))
}

function stationHasAnyFinding(station: any): boolean {
  return (station?.keyFindings?.length || 0) > 0
}

function formatDiagnosisPhase(phase: string): string {
  switch (phase) {
    case 'GraphTransitionTrace': return t('line_diagnosis.phaseGraphTransition')
    case 'EnableMissing': return t('line_diagnosis.phaseEnableMissing')
    case 'RecentIoTagBased': return t('line_diagnosis.phaseRecentIoTag')
    case 'DeviceFocusDirect': return t('line_diagnosis.deviceFocus')
    default: return phase
  }
}

// ── SignalR ──
let connection: signalR.HubConnection | null = null
// 等待后端出口翻译跟随事件的安全计时器（非中文任务 Completed 后不立即断连）
let translationWaitTimer: ReturnType<typeof setTimeout> | undefined

// 按 "a.b[0].c" 路径写值（沿 reactive 代理链走，保证模板响应）
function setByPath(root: any, path: string, value: any): boolean {
  const segs = path.replace(/\[(\d+)\]/g, '.$1').split('.').filter(Boolean)
  let cur = root
  for (let i = 0; i < segs.length - 1; i++) {
    cur = cur?.[segs[i]]
    if (cur == null) return false
  }
  const last = segs[segs.length - 1]
  if (cur == null || !(last in cur)) return false
  cur[last] = value
  return true
}

// 应用出口翻译补丁（stationMap 里的对象与 result.stationSummaries 同引用，改一处两边生效）
function applyEgressPatches(patches: Array<{ path: string, value: string }> | undefined) {
  if (!patches?.length || !diagnosisResult.value) return
  for (const p of patches) {
    setByPath(diagnosisResult.value, p.path, p.value)
  }
}

function finishTranslationWait() {
  if (translationWaitTimer) {
    clearTimeout(translationWaitTimer)
    translationWaitTimer = undefined
  }
}
let currentTaskId = ''

function agentStepTagType(phase: string): 'warning' | 'info' | undefined {
  if (phase === 'Action') return 'warning'
  if (phase === 'Thought') return 'info'
  return undefined
}

function getSignalRBaseUrl() {
  return import.meta.env.DEV
    ? import.meta.env.VITE_APP_API_BASEURL
    : (window as any).apiConfig?.apiUrl || ''
}

function setupSignalRAsync(taskId: string): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    const apiUrl = getSignalRBaseUrl()
    // if (!apiUrl) {
    //   ElMessage.warning('无法获取 API 地址')
    //   reject(new Error('无法获取 API 地址'))
    //   return
    // }

    connection = new signalR.HubConnectionBuilder()
      .withUrl(`${apiUrl}/hubs/station-shadow-monitoring`, {
        withCredentials: false,
        skipNegotiation: false,
      })
      .withAutomaticReconnect([0, 2000, 5000, 10000])
      .configureLogging(signalR.LogLevel.Warning)
      .build()

  // ── 事件监听 ──
  connection.on('LineDiagnosisStarted', (payload: any) => {
    phase.value = 'scanning'
    totalCount.value = payload.totalStations || 0
    completedCount.value = 0
    const codes: string[] = payload.stationCodes || []
    stationOrder.value = codes
    const map: Record<string, any> = {}
    codes.forEach((code: string, idx: number) => {
      map[code] = { stationCode: code, stationOrder: idx + 1, overallStatus: 'Pending' }
    })
    stationMap.value = map
  })

  connection.on('LineDiagnosisStationDone', (payload: any) => {
    const station = payload.station
    if (station?.stationCode) {
      stationMap.value[station.stationCode] = station
    }
    completedCount.value = payload.completedCount || (completedCount.value + 1)
    totalCount.value = payload.totalCount || totalCount.value
  })

  connection.on('LineDiagnosisLlmStart', () => {
    phase.value = 'llm'
  })

  connection.on('LineDiagnosisAgentStep', (payload: any) => {
    agentSteps.value.push({
      iteration: payload.iteration,
      phase: payload.phase,
      decision: payload.decision,
      content: payload.content || '',
      toolName: payload.toolName,
      toolArgs: payload.toolArgs,
      durationMs: payload.durationMs,
      success: payload.success,
      errorMessage: payload.errorMessage,
    })
    // 新步骤到达时自动展开
    agentStepsExpanded.value = true
  })

  connection.on('LineDiagnosisCompleted', (payload: any) => {
    phase.value = 'done'
    loading.value = false
    diagnosisResult.value = payload.result
    llmReport.value = payload.result?.llmReport || null
    // 用最终的 ordered summaries 更新
    if (payload.result?.stationSummaries) {
      const codes: string[] = []
      const map: Record<string, any> = {}
      for (const s of payload.result.stationSummaries) {
        codes.push(s.stationCode)
        map[s.stationCode] = s
      }
      stationOrder.value = codes
      stationMap.value = map
    }
    ElMessage.success(t('line_diagnosis.diagnosisCompletedToast', { elapsed: payload.result?.elapsedMs ?? 0 }))
    if (outputLang.value === 'zh-CN') {
      cleanupSignalR()
    }
    else {
      // 等待出口翻译跟随事件（LineDiagnosisContentTranslated），150s 兜底强制断连
      translationWaitTimer = setTimeout(() => {
        translationWaitTimer = undefined
        cleanupSignalR()
      }, 150_000)
    }
  })

  // 出口翻译跟随：译文到达后原位替换（final=true 时收尾断连）
  connection.on('LineDiagnosisContentTranslated', (payload: any) => {
    applyEgressPatches(payload?.patches)
    if (payload?.final) {
      finishTranslationWait()
      cleanupSignalR()
    }
  })

  connection.on('LineDiagnosisFailed', (payload: any) => {
    phase.value = 'error'
    loading.value = false
    errorMessage.value = payload.errorMessage || t('line_diagnosis.unknownError')
    ElMessage.error(payload.errorMessage || t('line_diagnosis.diagnosisFailed'))
    finishTranslationWait()
    cleanupSignalR()
  })

  connection.onreconnected(async () => {
    if (currentTaskId) {
      await connection?.invoke('SubscribeLineDiagnosis', currentTaskId)
    }
  })

  connection.start().then(async () => {
    await connection?.invoke('SubscribeLineDiagnosis', taskId)
    resolve()
  }).catch((err) => {
    console.error('SignalR 连接失败:', err)
    ElMessage.error(t('line_diagnosis.connectionFailed'))
    loading.value = false
    phase.value = 'error'
    errorMessage.value = t('line_diagnosis.connectionFailed')
    reject(err)
  })
  }) // end Promise
}

function cleanupSignalR() {
  finishTranslationWait()
  if (connection) {
    if (currentTaskId) {
      connection.invoke('UnsubscribeLineDiagnosis', currentTaskId).catch(() => {})
    }
    connection.stop().catch(() => {})
    connection = null
  }
  currentTaskId = ''
}

onBeforeUnmount(() => {
  cleanupSignalR()
})

// ── 一键排查 ──
async function runDiagnosis() {
  loading.value = true
  phase.value = 'idle'
  stationMap.value = {}
  stationOrder.value = []
  llmReport.value = null
  diagnosisResult.value = null
  agentSteps.value = []
  agentStepsExpanded.value = false
  lastRunOptions.value = {
    includeConstraintAnalysis: includeConstraintAnalysis.value,
    includeDeviceFocusCheck: includeDeviceFocusCheck.value,
    deviceFocusAnchorTypes: [...effectiveDeviceFocusAnchorTypes.value],
  }
  completedCount.value = 0
  totalCount.value = 0
  errorMessage.value = ''
  cleanupSignalR()

  // 先生成 taskId，先连 SignalR 订阅好再发 HTTP，避免 Hangfire 先跑完事件丢失
  const taskId = `line_diag_${Date.now()}_${Math.random().toString(36).slice(2, 8)}`
  currentTaskId = taskId

  try {
    // 1. 先建立 SignalR 连接并订阅
    await setupSignalRAsync(taskId)

    // 2. 连接就绪后再发 HTTP 请求
    phase.value = 'scanning'
    const payload: any = {
      maxParallelStations: 3,
      useLlmAnalysis: true,
      useAgentDiagnosis: useAgentDiagnosis.value,
      enablePerStationLlm: enablePerStationLlm.value,
      includeConstraintAnalysis: includeConstraintAnalysis.value,
      forceLlmSummaryWhenNoAbnormal: includeConstraintAnalysis.value,
      enableDeviceFocusCheck: includeDeviceFocusCheck.value,
      deviceFocusAnchorTypes: [...effectiveDeviceFocusAnchorTypes.value],
      outputLanguage: outputLang.value,
      agentMaxIterations: 12,
      taskId,
    }

    if (projectId.value) {
      payload.projectIds = [projectId.value]
    }

    const res: any = await StopCauseDiagnosisAPI.diagnoseLineStoppage(payload)

    if (!res.isSuccess) {
      ElMessage.error(res.errorMessage || t('line_diagnosis.startFailed'))
      loading.value = false
      phase.value = 'error'
      errorMessage.value = res.errorMessage || t('line_diagnosis.startFailed')
      cleanupSignalR()
    }
  }
  catch (err: any) {
    ElMessage.error(err.message || t('line_diagnosis.requestFailed'))
    loading.value = false
    phase.value = 'error'
    errorMessage.value = err.message || t('line_diagnosis.requestFailed')
    cleanupSignalR()
  }
}
</script>

<template>
  <div class="line-diagnosis-page">
    <!-- 顶部标题栏 -->
    <div class="page-header">
      <div class="header-left">
        <h2 class="page-title">
          ⚠️ {{ $t('line_diagnosis.title') }}
        </h2>
        <span v-if="phase === 'done' && diagnosisResult" class="header-meta">
          {{ $t('line_diagnosis.headerMeta', { stations: stationSummaries.length, running: runningCount, abnormal: abnormalCount, elapsed: diagnosisResult.elapsedMs }) }}
        </span>
        <el-tag v-if="phase === 'done' && activeAlarmTotal > 0" size="small" type="danger">
          {{ $t('line_diagnosis.totalAlarms', { count: activeAlarmTotal }) }}
        </el-tag>
        <el-tag v-if="phase === 'done' && diagnosisResult?.alarmReadError" size="small" type="warning">
          {{ diagnosisResult.alarmReadError }}
        </el-tag>
      </div>
      <div class="header-controls">
        <el-button
          size="large"
          :disabled="loading"
          class="settings-button"
          @click="settingsDialogVisible = true"
        >
          <el-icon><Setting /></el-icon>
          <span>{{ $t('line_diagnosis.settings') }}</span>
          <el-tag v-if="enabledSettingCount > 0" size="small" type="warning" effect="plain">
            {{ enabledSettingCount }}
          </el-tag>
        </el-button>
        <el-button
          type="primary"
          size="large"
          :loading="loading"
          @click="runDiagnosis"
        >
          {{ loading ? $t('line_diagnosis.runningRun') : $t('line_diagnosis.run') }}
        </el-button>
      </div>
    </div>

    <el-dialog
      v-model="settingsDialogVisible"
      :title="$t('line_diagnosis.settingsTitle')"
      width="460px"
      append-to-body
      class="line-settings-dialog"
    >
      <div class="settings-list">
        <div class="settings-row">
          <div class="settings-row-main">
            <span class="settings-row-title">{{ $t('line_diagnosis.agentMode') }}</span>
            <span class="settings-row-subtitle">{{ $t('line_diagnosis.agentModeHint') }}</span>
          </div>
          <el-switch
            v-model="useAgentDiagnosis"
            :disabled="loading"
            active-color="#409EFF"
          />
        </div>
        <div class="settings-row">
          <div class="settings-row-main">
            <span class="settings-row-title">{{ $t('line_diagnosis.stationAi') }}</span>
            <span class="settings-row-subtitle">{{ $t('line_diagnosis.stationAiHint') }}</span>
          </div>
          <el-switch
            v-model="enablePerStationLlm"
            :disabled="loading"
            active-color="#67C23A"
          />
        </div>
        <div class="settings-row">
          <div class="settings-row-main">
            <span class="settings-row-title">{{ $t('line_diagnosis.ioConstraint') }}</span>
            <span class="settings-row-subtitle">{{ $t('line_diagnosis.ioConstraintHint') }}</span>
          </div>
          <el-switch
            v-model="includeConstraintAnalysis"
            :disabled="loading"
            active-color="#E6A23C"
          />
        </div>
        <div class="settings-row settings-row--stack">
          <div class="settings-row-header">
            <div class="settings-row-main">
              <span class="settings-row-title">{{ $t('line_diagnosis.deviceSignal') }}</span>
              <span class="settings-row-subtitle">{{ deviceFocusTypeSummary }}</span>
            </div>
            <el-switch
              v-model="includeDeviceFocusCheck"
              :disabled="loading"
              active-color="#F56C6C"
            />
          </div>
          <el-checkbox-group
            v-model="deviceFocusAnchorTypes"
            class="device-focus-type-options"
            :disabled="loading || !includeDeviceFocusCheck"
          >
            <el-checkbox
              v-for="option in deviceFocusTypeOptions"
              :key="option.value"
              :label="option.value"
              class="device-focus-type-option"
            >
              <span class="device-focus-type-label">{{ option.label }}</span>
              <span class="device-focus-type-desc">{{ option.description }}</span>
            </el-checkbox>
          </el-checkbox-group>
        </div>
      </div>
      <template #footer>
        <el-button type="primary" @click="settingsDialogVisible = false">
          {{ $t('line_diagnosis.confirm') }}
        </el-button>
      </template>
    </el-dialog>

    <!-- 实时进度条 -->
    <div v-if="phase !== 'idle'" class="progress-section">
      <el-progress
        :percentage="progressPercent"
        :status="progressStatus"
        :stroke-width="20"
        text-inside
        :format="() => phaseLabel"
      />
    </div>

    <!-- 产线工位状态条 -->
    <div v-if="hasResult" class="station-bar-section">
      <h3 class="section-title">
        {{ $t('line_diagnosis.overview') }}
      </h3>
      <div class="station-bar">
        <div
          v-for="station in stationSummaries"
          :key="station.stationCode"
          class="station-chip"
          :class="{ 'station-chip--pending': station.overallStatus === 'Pending' }"
          :style="{ borderColor: stationStatusColor(station.overallStatus, getStationRole(station.stationCode)) }"
        >
          <div
            class="chip-indicator"
            :style="{ backgroundColor: stationStatusColor(station.overallStatus, getStationRole(station.stationCode)) }"
          />
          <div class="chip-content">
            <span class="chip-code">{{ station.stationCode }}</span>
            <span class="chip-label">{{ stationStatusLabel(station.overallStatus, getStationRole(station.stationCode)) }}</span>
          </div>
        </div>
      </div>
    </div>

    <!-- Agent 推理过程（LLM 阶段实时展示，结论出来前） -->
    <div v-if="phase === 'llm' && useAgentDiagnosis && !llmReport" class="agent-live-section">
      <h3 class="section-title">
        🧠 {{ $t('line_diagnosis.agentThinking') }}
      </h3>
      <div v-if="agentSteps.length > 0" class="agent-steps-timeline">
        <div
          v-for="(step, idx) in agentSteps"
          :key="idx"
          class="agent-step-item"
          :class="`agent-step--${step.phase.toLowerCase()}`"
        >
          <div class="agent-step-marker">
            <span class="agent-step-icon">{{ agentPhaseIcon(step.phase) }}</span>
            <span v-if="idx < agentSteps.length - 1" class="agent-step-line" />
          </div>
          <div class="agent-step-body">
            <div class="agent-step-head">
              <el-tag size="small" :type="agentStepTagType(step.phase)">
                {{ agentPhaseLabel(step.phase) }}
              </el-tag>
              <span class="agent-step-iter">{{ $t('line_diagnosis.iteration', { iteration: step.iteration }) }}</span>
              <span class="agent-step-duration">{{ step.durationMs }}ms</span>
              <el-tag v-if="step.toolName" size="small" type="warning">
                {{ step.toolName }}
              </el-tag>
              <el-tag v-if="!step.success && step.errorMessage" size="small" type="danger">
                {{ $t('line_diagnosis.errorLabel') }}
              </el-tag>
            </div>
            <div v-if="step.toolArgs" class="agent-step-tool-args">
              <code>{{ step.toolArgs }}</code>
            </div>
            <div v-if="step.content" class="agent-step-content">
              {{ step.content }}
            </div>
            <div v-if="step.errorMessage" class="agent-step-error">
              {{ step.errorMessage }}
            </div>
          </div>
        </div>
        <div class="agent-step-item agent-step--pending">
          <div class="agent-step-marker">
            <span class="agent-step-icon agent-step-icon--pulse">⏳</span>
          </div>
          <div class="agent-step-body">
            <span class="agent-waiting-text">{{ $t('line_diagnosis.waitNextStep') }}</span>
          </div>
        </div>
      </div>
      <div v-else class="agent-waiting">
        <span class="agent-waiting-dot" />
        {{ $t('line_diagnosis.agentInit') }}
      </div>
    </div>

    <!-- AI 诊断结论 -->
    <div v-if="llmReport?.success" class="llm-report-section">

      <!-- Agent 推理过程（嵌入结论区顶部） -->
      <div v-if="agentSteps.length > 0" class="agent-steps-section">
        <div class="agent-steps-header" @click="agentStepsExpanded = !agentStepsExpanded">
          <h4 class="agent-steps-title">
            🧠 {{ $t('line_diagnosis.agentStepsTitle', { count: agentSteps.length }) }}
          </h4>
          <el-icon class="agent-steps-arrow" :class="{ 'is-expanded': agentStepsExpanded }">
            <svg viewBox="0 0 1024 1024" width="14" height="14"><path d="M384 192l339.2 320L384 832" fill="currentColor"/></svg>
          </el-icon>
        </div>
        <transition name="agent-steps-slide">
          <div v-show="agentStepsExpanded" class="agent-steps-timeline">
            <div
              v-for="(step, idx) in agentSteps"
              :key="idx"
              class="agent-step-item"
              :class="`agent-step--${step.phase.toLowerCase()}`"
            >
              <div class="agent-step-marker">
                <span class="agent-step-icon">{{ agentPhaseIcon(step.phase) }}</span>
                <span v-if="idx < agentSteps.length - 1" class="agent-step-line" />
              </div>
              <div class="agent-step-body">
                <div class="agent-step-head">
                  <el-tag size="small" :type="agentStepTagType(step.phase)">
                    {{ agentPhaseLabel(step.phase) }}
                  </el-tag>
                  <span class="agent-step-iter">{{ $t('line_diagnosis.iteration', { iteration: step.iteration }) }}</span>
                  <span class="agent-step-duration">{{ step.durationMs }}ms</span>
                  <el-tag v-if="step.toolName" size="small" type="warning">
                    {{ step.toolName }}
                  </el-tag>
                  <el-tag v-if="!step.success && step.errorMessage" size="small" type="danger">
                    {{ $t('line_diagnosis.errorLabel') }}
                  </el-tag>
                </div>
                <div v-if="step.toolArgs" class="agent-step-tool-args">
                  <code>{{ step.toolArgs }}</code>
                </div>
                <div v-if="step.content" class="agent-step-content">
                  {{ step.content }}
                </div>
                <div v-if="step.errorMessage" class="agent-step-error">
                  {{ step.errorMessage }}
                </div>
              </div>
            </div>
          </div>
        </transition>
      </div>
      <!-- Agent 推理中等待提示 -->
      <div v-else-if="phase === 'llm' && useAgentDiagnosis" class="agent-waiting">
        <span class="agent-waiting-dot" />
        {{ $t('line_diagnosis.agentInit') }}
      </div>
      <h3 class="section-title">
        🔍 {{ $t('line_diagnosis.aiConclusion') }}
      </h3>
      <el-card shadow="hover" class="llm-card">
        <div class="llm-header">
          <el-tag type="danger" size="large">
            {{ $t('line_diagnosis.rootCauseStation') }}{{ llmReport.rootCauseStation }}
          </el-tag>
          <el-tag type="info">
            {{ $t('line_diagnosis.confidence') }}{{ (llmReport.confidence * 100).toFixed(0) }}%
          </el-tag>
        </div>
        <p class="llm-description">
          {{ llmReport.rootCauseDescription }}
        </p>
        <div v-if="llmReport.secondaryCauses?.length" class="llm-secondary-causes">
          <strong>{{ $t('line_diagnosis.secondaryCauses') }}</strong>
          <ul>
            <li v-for="(cause, i) in llmReport.secondaryCauses" :key="i">
              <el-tag type="warning" size="small" style="margin-right:6px">{{ cause.stationCode }}</el-tag>{{ cause.description }}
            </li>
          </ul>
        </div>
        <div v-if="llmReport.impactChain" class="llm-impact">
          <strong>{{ $t('line_diagnosis.impactChain') }}</strong>{{ llmReport.impactChain }}
        </div>
        <div v-if="llmReport.suggestedActions?.length" class="llm-actions">
          <strong>{{ $t('line_diagnosis.suggestedActions') }}</strong>
          <ul>
            <li v-for="(action, i) in llmReport.suggestedActions" :key="i">
              {{ action }}
            </li>
          </ul>
        </div>
        <el-collapse v-if="llmReport.reasoning" class="llm-reasoning-collapse">
          <el-collapse-item :title="$t('line_diagnosis.reasoning')">
            <p class="llm-reasoning">
              {{ llmReport.reasoning }}
            </p>
          </el-collapse-item>
        </el-collapse>
        <!-- Agent 模式额外信息 -->
        <div v-if="llmReport.agentIterations > 0" class="llm-agent-meta">
          <el-tag type="info" size="small">
            🤖 {{ $t('line_diagnosis.agentModeTag') }}
          </el-tag>
          <span class="agent-meta-text">{{ $t('line_diagnosis.reactIterations', { count: llmReport.agentIterations }) }}</span>
          <el-collapse v-if="llmReport.toolCallSummary" class="llm-reasoning-collapse" style="margin-top:6px">
            <el-collapse-item :title="$t('line_diagnosis.toolStats')">
              <pre class="llm-reasoning">{{ llmReport.toolCallSummary }}</pre>
            </el-collapse-item>
          </el-collapse>
        </div>
      </el-card>
    </div>

    <!-- AI 诊断失败/无结论时显示原始输出 -->
    <div v-else-if="llmReport && !llmReport.success && phase === 'done'" class="llm-report-section">
      <h3 class="section-title">
        🔍 {{ $t('line_diagnosis.aiConclusion') }}
      </h3>
      <el-card shadow="hover" class="llm-card">
        <el-tag type="warning" size="large">
          {{ $t('line_diagnosis.noValidConclusion') }}
        </el-tag>
        <p class="llm-description" style="margin-top: 12px;">
          {{ llmReport.rootCauseDescription || $t('line_diagnosis.unknownReason') }}
        </p>
        <el-collapse v-if="llmReport.rawResponse" class="llm-reasoning-collapse" style="margin-top: 8px;">
          <el-collapse-item :title="$t('line_diagnosis.rawOutput')">
            <pre class="llm-reasoning">{{ llmReport.rawResponse }}</pre>
          </el-collapse-item>
        </el-collapse>
        <div v-if="llmReport.agentIterations > 0" class="llm-agent-meta" style="margin-top: 8px;">
          <el-tag type="info" size="small">
            🤖 {{ $t('line_diagnosis.agentModeTag') }}
          </el-tag>
          <span class="agent-meta-text">{{ $t('line_diagnosis.reactIterations', { count: llmReport.agentIterations }) }}</span>
        </div>
      </el-card>
    </div>

    <!-- IO 约束分析报告 -->
    <div
      v-if="phase === 'done' && (lastRunOptions.includeConstraintAnalysis || constraintReport)"
      class="constraint-report-section"
    >
      <h3 class="section-title">
        📐 {{ $t('line_diagnosis.ioAnalysis') }}
      </h3>
      <el-card shadow="never" class="constraint-card">
        <template v-if="constraintReport">
          <div class="constraint-report-head">
            <el-tag type="warning" size="small">
              {{ $t('line_diagnosis.completed') }}
            </el-tag>
            <span class="constraint-report-meta">{{ $t('line_diagnosis.reportLength', { length: constraintReport.length }) }}</span>
          </div>
          <el-collapse class="constraint-report-collapse">
            <el-collapse-item :title="$t('line_diagnosis.viewReport')" name="constraint-report">
              <pre class="constraint-report-content">{{ constraintReport }}</pre>
            </el-collapse-item>
          </el-collapse>
        </template>
        <div v-else class="constraint-empty">
          {{ $t('line_diagnosis.constraintEmpty') }}
        </div>
      </el-card>
    </div>

    <!-- 各工位详情 -->
    <div v-if="hasResult" class="station-details-section">
      <h3 class="section-title">
        📋 {{ $t('line_diagnosis.stationDetails') }}
      </h3>
      <el-collapse>
        <el-collapse-item
          v-for="station in stationSummaries"
          :key="station.stationCode"
          :name="station.stationCode"
        >
          <template #title>
            <div class="station-collapse-title">
              <span class="station-icon">{{ stationStatusIcon(station.overallStatus) }}</span>
              <span class="station-code-label">{{ station.stationCode }}</span>
              <el-tag
                :type="station.overallStatus === 'AllTrue' || station.overallStatus === 'Running' ? 'success' : station.overallStatus === 'SomeFalse' ? 'warning' : 'info'"
                size="small"
              >
                {{ stationStatusLabel(station.overallStatus, getStationRole(station.stationCode)) }}
              </el-tag>
              <el-tag v-if="deviceFocusFindings(station).length" size="small" type="danger" effect="plain">
                {{ $t('line_diagnosis.deviceFocus') }} {{ deviceFocusFindings(station).length }}
              </el-tag>
              <el-tag v-if="station.activeAlarms?.length" size="small" type="danger">
                {{ $t('line_diagnosis.valveAlarmTitle') }} {{ station.activeAlarms.length }}
              </el-tag>
              <span v-if="station.overallStatus === 'Running'" class="station-anchor-count">{{ $t('line_diagnosis.signalActive') }}</span>
              <span v-else-if="station.overallStatus !== 'Pending'" class="station-anchor-count">
                {{ $t('line_diagnosis.anchorsEvaluated', { evaluated: station.evaluatedCount ?? 0, total: station.anchorCount ?? 0 }) }}
                <template v-if="(station.falseAnchorCount ?? 0) > 0">
                  · {{ $t('line_diagnosis.falseCount', { count: station.falseAnchorCount }) }}
                </template>
              </span>
              <span v-else class="station-anchor-count">{{ $t('line_diagnosis.pending') }}</span>
              <span v-if="station.elapsedMs" class="station-timing">{{ station.elapsedMs }}ms</span>
            </div>
          </template>

          <div v-if="station.errorMessage" class="station-error">
            {{ station.errorMessage }}
          </div>

          <!-- 诊断策略 + 耗时 -->
          <div v-if="station.diagnosisStrategy" class="station-strategy">
            <el-tag size="small" type="info">
              {{ $t('line_diagnosis.strategy') }}{{ formatDiagnosisStrategy(station.diagnosisStrategy) }}
            </el-tag>
            <el-tag v-if="station.elapsedMs" size="small" type="info">
              {{ $t('line_diagnosis.elapsed') }}{{ station.elapsedMs }}ms
            </el-tag>
            <el-tag v-if="station.timingBreakdown" size="small" type="info" class="timing-tag">
              {{ station.timingBreakdown }}
            </el-tag>
          </div>
          <!-- 模板匹配信息 -->
          <div v-if="station.templateMatchInfo" class="template-match-info">
            <el-icon><InfoFilled /></el-icon>
            {{ station.templateMatchInfo }}
            <el-tag v-if="station.templateMatchScore > 0" size="small"
              :type="station.templateMatchScore >= 70 ? 'success' : station.templateMatchScore >= 40 ? 'warning' : 'info'"
              style="margin-left: 8px;">
              {{ $t('line_diagnosis.matchScore') }}{{ station.templateMatchScore }}/100
            </el-tag>
          </div>
          <div v-else-if="station.templateMissReason" class="template-miss-info">
            <el-icon><InfoFilled /></el-icon>
            <span>{{ $t('line_diagnosis.templateMiss') }}{{ formatTemplateMissReason(station.templateMissReason) }}</span>
          </div>

          <!-- 工位独立 LLM 诊断摘要 -->
          <div v-if="station.llmSummary" class="station-llm-summary">
            <div class="llm-summary-header">
              <el-icon><MagicStick /></el-icon>
              <span>{{ $t('line_diagnosis.stationAiSummary') }}</span>
            </div>
            <div class="llm-summary-content">{{ station.llmSummary }}</div>
          </div>

          <div
            v-if="station.activeAlarms?.length || station.alarmReadFailedCount"
            class="device-focus-panel"
          >
            <div class="device-focus-header">
              <span>{{ $t('line_diagnosis.valveAlarmTitle') }}</span>
              <el-tag v-if="station.activeAlarms?.length" size="small" type="danger" effect="plain">
                {{ $t('line_diagnosis.hitCount', { count: station.activeAlarms.length }) }}
              </el-tag>
            </div>
            <div v-if="station.activeAlarms?.length" class="findings-list">
              <div
                v-for="(alarm, idx) in station.activeAlarms"
                :key="`alarm-${idx}`"
                class="finding-item finding-item--device"
              >
                <div class="finding-header">
                  <el-tag :type="alarmTagType(alarm.kind)" size="small">
                    {{ $t(`line_diagnosis.${alarmKindKey(alarm.kind)}`) }}
                  </el-tag>
                  <span class="finding-var">{{ alarm.text || alarm.alarmVariable }}</span>
                </div>
                <div class="finding-detail">
                  {{ alarmLocationText(alarm) }}
                </div>
                <div v-if="alarmSignalsText(alarm)" class="finding-path">
                  {{ $t('line_diagnosis.alarmSignals') }}{{ alarmSignalsText(alarm, $t('line_diagnosis.alarmUnread')) }}
                </div>
              </div>
            </div>
            <div v-if="station.alarmReadFailedCount" class="finding-detail">
              {{ $t('line_diagnosis.alarmReadFailed', { count: station.alarmReadFailedCount }) }}
            </div>
          </div>

          <div
            v-if="lastRunOptions.includeDeviceFocusCheck || deviceFocusFindings(station).length"
            class="device-focus-panel"
          >
            <div class="device-focus-header">
              <span>{{ $t('line_diagnosis.devicePanelTitle') }}</span>
              <el-tag v-if="deviceFocusFindings(station).length" size="small" type="danger" effect="plain">
                {{ $t('line_diagnosis.hitCount', { count: deviceFocusFindings(station).length }) }}
              </el-tag>
              <el-tag v-else size="small" type="success" effect="plain">
                {{ $t('line_diagnosis.noHit') }}
              </el-tag>
            </div>
            <div v-if="deviceFocusFindings(station).length" class="findings-list">
              <div
                v-for="(finding, idx) in deviceFocusFindings(station)"
                :key="`device-${idx}`"
                class="finding-item finding-item--device"
              >
                <div class="finding-header">
                  <span class="finding-var">{{ finding.displayName || finding.variableName }}</span>
                  <el-tag :type="finding.evaluatedValue === false ? 'danger' : 'warning'" size="small">
                    {{ finding.evaluatedValue ?? 'N/A' }}
                  </el-tag>
                  <el-tag v-if="finding.diagnosisPhase" size="small" type="danger" effect="plain">
                    {{ formatDiagnosisPhase(finding.diagnosisPhase) }}
                  </el-tag>
                  <el-tag v-if="finding.faultOwnership" size="small" type="warning">
                    {{ finding.faultOwnership }}
                  </el-tag>
                </div>
                <div v-if="finding.variableName !== finding.displayName" class="finding-varname">
                  {{ finding.variableName }}
                </div>
                <div class="finding-detail">
                  <template v-if="finding.ioTagAddress">{{ $t('line_diagnosis.point') }}{{ finding.ioTagAddress }}</template>
                  <template v-if="finding.ioTagComment"> · {{ $t('line_diagnosis.comment') }}{{ finding.ioTagComment }}</template>
                  <template v-if="finding.lastTrueAt"> · {{ $t('line_diagnosis.lastTrue') }}{{ finding.lastTrueAt }}</template>
                </div>
                <div v-if="finding.failurePathSummary" class="finding-path">
                  {{ $t('line_diagnosis.diagnosisBasis') }}{{ finding.failurePathSummary }}
                </div>
                <div v-if="finding.signalHistoryAnalysis" class="finding-history">
                  {{ $t('line_diagnosis.signalHistory') }}{{ finding.signalHistoryAnalysis }}
                </div>
              </div>
            </div>
            <div v-else class="device-focus-empty">
              {{ $t('line_diagnosis.deviceFocusEmpty') }}
            </div>
          </div>

          <div v-if="regularFindings(station).length" class="findings-list">
            <div
              v-for="(finding, idx) in regularFindings(station)"
              :key="idx"
              class="finding-item"
            >
              <div class="finding-header">
                <span class="finding-var">{{ finding.displayName || finding.variableName }}</span>
                <el-tag :type="finding.evaluatedValue === false ? 'danger' : 'info'" size="small">
                  {{ finding.evaluatedValue ?? 'N/A' }}
                </el-tag>
                <el-tag size="small">
                  {{ finding.evaluationStatus }}
                </el-tag>
                <el-tag v-if="finding.diagnosisQuality" size="small" :type="finding.diagnosisQuality === 'Diagnosed' ? 'success' : 'warning'">
                  {{ finding.diagnosisQuality }}
                </el-tag>
                <el-tag v-if="finding.faultOwnership" size="small" :type="finding.faultOwnership.startsWith('InternalFault') ? 'danger' : 'warning'">
                  {{ finding.faultOwnership }}
                </el-tag>
                <el-tag v-if="finding.diagnosisPhase" size="small" effect="plain">
                  {{ formatDiagnosisPhase(finding.diagnosisPhase) }}
                </el-tag>
              </div>
              <div v-if="finding.variableName !== finding.displayName" class="finding-varname">
                {{ finding.variableName }}
              </div>
              <div v-if="finding.blockName" class="finding-detail">
                {{ $t('line_diagnosis.logicBlock') }}{{ finding.blockName }}
                <template v-if="finding.traceDepth > 0"> · {{ $t('line_diagnosis.traceDepth', { depth: finding.traceDepth }) }}</template>
                <template v-if="finding.lastTrueAt"> · {{ $t('line_diagnosis.lastTrue') }}{{ finding.lastTrueAt }}</template>
                <template v-if="finding.ioTagAddress"> · {{ $t('line_diagnosis.point') }}{{ finding.ioTagAddress }}</template>
                <template v-if="finding.ioTagComment"> · {{ $t('line_diagnosis.comment') }}{{ finding.ioTagComment }}</template>
              </div>
              <div v-if="finding.failurePathSummary" class="finding-path">
                {{ $t('line_diagnosis.failurePath') }}{{ finding.failurePathSummary }}
              </div>
              <div v-if="finding.signalHistoryAnalysis" class="finding-history">
                {{ $t('line_diagnosis.signalHistory') }}{{ finding.signalHistoryAnalysis }}
              </div>
            </div>
          </div>

          <div v-if="!stationHasAnyFinding(station) && station.overallStatus === 'Running'" class="station-ok">
            🟢 {{ $t('line_diagnosis.runningOk') }}
          </div>
          <div v-else-if="!stationHasAnyFinding(station) && station.overallStatus === 'AllTrue'" class="station-ok">
            {{ $t('line_diagnosis.allAnchorsOk') }}
          </div>
          <div v-else-if="!stationHasAnyFinding(station)" class="station-no-data">
            {{ $t('line_diagnosis.noData') }}
          </div>
        </el-collapse-item>
      </el-collapse>
    </div>

    <!-- 空状态 -->
    <div v-if="!hasResult && !loading" class="empty-state">
      <div class="empty-icon">
        🏭
      </div>
      <p>{{ $t('line_diagnosis.emptyStart') }}</p>
      <p class="empty-hint">
        {{ $t('line_diagnosis.emptyHint') }}
      </p>
    </div>
  </div>
</template>

<style scoped>
.line-diagnosis-page {
  padding: 24px;
  max-width: 1200px;
  margin: 0 auto;
}

.page-header {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  justify-content: space-between;
  gap: 16px;
  margin-bottom: 24px;
}

.header-controls {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-shrink: 0;
}

.language-switch {
  flex-shrink: 0;
}

.language-switch :deep(.el-radio-button__inner) {
  min-width: 52px;
}

.settings-button {
  display: flex;
  align-items: center;
  gap: 6px;
}

.settings-list {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.settings-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  min-height: 56px;
  padding: 10px 4px;
  border-bottom: 1px solid #ebeef5;
}

.settings-row--stack {
  align-items: stretch;
  flex-direction: column;
  gap: 10px;
}

.settings-row-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.settings-row:last-child {
  border-bottom: 0;
}

.settings-row-main {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.settings-row-title {
  font-size: 14px;
  font-weight: 600;
  color: #303133;
}

.settings-row-subtitle {
  font-size: 12px;
  color: #909399;
}

.device-focus-type-options {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 8px;
}

.device-focus-type-option {
  height: auto;
  margin-right: 0;
  padding: 8px 10px;
  border: 1px solid #ebeef5;
  border-radius: 6px;
  background: #fafafa;
}

.device-focus-type-option :deep(.el-checkbox__label) {
  display: flex;
  flex-direction: column;
  gap: 2px;
  line-height: 1.3;
}

.device-focus-type-label {
  color: #303133;
  font-size: 13px;
  font-weight: 600;
}

.device-focus-type-desc {
  color: #909399;
  font-size: 12px;
}

.llm-agent-meta {
  margin-top: 12px;
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.agent-meta-text {
  font-size: 13px;
  color: #606266;
}

.header-left {
  display: flex;
  flex: 1 1 420px;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px 16px;
  min-width: 0;
}

.page-title {
  margin: 0;
  font-size: 22px;
  white-space: nowrap;
  flex-shrink: 0;
}

.header-meta {
  color: #909399;
  font-size: 14px;
  white-space: nowrap;
}

/* 分阶段耗时、报警读取原因可能很长：允许标签内换行，不挤压标题 */
.header-left .el-tag {
  height: auto;
  min-height: 24px;
  max-width: 100%;
  white-space: normal;
  overflow-wrap: anywhere;
  line-height: 18px;
  padding-top: 2px;
  padding-bottom: 2px;
}

.section-title {
  font-size: 16px;
  margin: 24px 0 12px;
  color: #303133;
}

/* ── 工位状态条 ── */
.station-bar {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}

.station-chip {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 14px;
  border: 2px solid #dcdfe6;
  border-radius: 8px;
  background: #fff;
  min-width: 90px;
  transition: all 0.3s ease;
}

.station-chip--pending {
  opacity: 0.6;
  animation: pulse-pending 1.5s infinite;
}

@keyframes pulse-pending {
  0%, 100% { opacity: 0.6; }
  50% { opacity: 1; }
}

/* ── 进度条 ── */
.progress-section {
  margin-bottom: 20px;
}

.chip-indicator {
  width: 10px;
  height: 10px;
  border-radius: 50%;
  flex-shrink: 0;
}

.chip-content {
  display: flex;
  flex-direction: column;
}

.chip-code {
  font-weight: 600;
  font-size: 13px;
  color: #303133;
}

.chip-label {
  font-size: 11px;
  color: #909399;
}

/* ── LLM 报告 ── */
.llm-card {
  margin-bottom: 16px;
}

.llm-header {
  display: flex;
  gap: 12px;
  margin-bottom: 12px;
}

.llm-description {
  font-size: 15px;
  line-height: 1.6;
  color: #303133;
  margin: 8px 0;
}

.llm-impact {
  margin: 8px 0;
  padding: 8px 12px;
  background: #fdf6ec;
  border-radius: 4px;
  font-size: 14px;
}

.llm-secondary-causes {
  margin: 8px 0;
  padding: 8px 12px;
  background: #fef9f0;
  border-left: 3px solid #e6a23c;
  border-radius: 4px;
  font-size: 14px;
}

.llm-secondary-causes ul {
  margin: 6px 0 0 0;
  padding: 0;
  list-style: none;
}

.llm-secondary-causes li {
  line-height: 1.8;
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 4px;
}

.llm-actions {
  margin: 8px 0;
}

.llm-actions ul {
  margin: 4px 0 0 20px;
  padding: 0;
}

.llm-actions li {
  font-size: 14px;
  line-height: 1.8;
}

.llm-reasoning-collapse {
  margin-top: 12px;
}

.llm-reasoning {
  white-space: pre-wrap;
  font-size: 13px;
  color: #606266;
  line-height: 1.6;
}

/* ── 工位详情 ── */
.station-collapse-title {
  display: flex;
  align-items: center;
  gap: 10px;
}

.station-icon {
  font-size: 16px;
}

.station-code-label {
  font-weight: 600;
  font-size: 14px;
}

.station-anchor-count {
  font-size: 12px;
  color: #909399;
  margin-left: auto;
  padding-right: 12px;
}

.station-timing {
  font-size: 11px;
  color: #c0c4cc;
  font-family: 'Menlo', 'Consolas', monospace;
  padding-right: 8px;
  white-space: nowrap;
}

.station-llm-summary {
  margin: 8px 12px;
  padding: 10px 14px;
  background: linear-gradient(135deg, #f0f9eb 0%, #e8f5e9 100%);
  border-left: 3px solid #67C23A;
  border-radius: 6px;
}
.llm-summary-header {
  display: flex;
  align-items: center;
  gap: 6px;
  font-weight: 600;
  color: #67C23A;
  font-size: 13px;
  margin-bottom: 4px;
}
.llm-summary-content {
  font-size: 13px;
  color: #333;
  line-height: 1.6;
  white-space: pre-wrap;
}

.constraint-report-section {
  margin-top: 24px;
}

.constraint-card {
  border-radius: 6px;
}

.constraint-report-head {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 8px;
}

.constraint-report-meta {
  font-size: 12px;
  color: #909399;
}

.constraint-report-collapse {
  border-top: 1px solid #ebeef5;
}

.constraint-report-content {
  margin: 0;
  max-height: 360px;
  overflow: auto;
  padding: 10px 12px;
  background: #f8fafc;
  border-radius: 4px;
  color: #303133;
  font-size: 12px;
  line-height: 1.7;
  white-space: pre-wrap;
  word-break: break-word;
}

.constraint-empty {
  padding: 10px 12px;
  background: #f5f7fa;
  border-left: 3px solid #909399;
  border-radius: 4px;
  color: #606266;
  font-size: 13px;
}

.device-focus-panel {
  margin: 8px 0 10px;
  padding: 10px 12px;
  background: #fff7f7;
  border: 1px solid #fde2e2;
  border-radius: 6px;
}

.device-focus-header {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 6px;
  color: #c45656;
  font-size: 13px;
  font-weight: 600;
}

.device-focus-empty {
  color: #909399;
  font-size: 12px;
}

.findings-list {
  padding: 8px 0;
}

.finding-item {
  padding: 10px 12px;
  margin: 4px 0;
  background: #fafafa;
  border-radius: 6px;
  border-left: 3px solid #E6A23C;
}

.finding-item--device {
  background: #fff;
  border-left-color: #F56C6C;
}

.finding-header {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.finding-var {
  font-weight: 500;
  font-size: 14px;
}

.finding-varname {
  font-size: 12px;
  color: #909399;
  font-family: monospace;
  margin-top: 4px;
}

.finding-path {
  font-size: 12px;
  color: #606266;
  margin-top: 4px;
  font-family: monospace;
}

.finding-detail {
  font-size: 12px;
  color: #909399;
  margin-top: 4px;
}

.finding-history {
  font-size: 12px;
  color: #303133;
  margin-top: 6px;
  padding: 6px 10px;
  background: #f5f7fa;
  border-left: 3px solid #409EFF;
  border-radius: 2px;
  line-height: 1.6;
  white-space: pre-wrap;
}

.station-strategy {
  margin-bottom: 8px;
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
  align-items: center;
}

.station-strategy .timing-tag {
  font-family: 'Menlo', 'Consolas', monospace;
  font-size: 11px;
}

.template-match-info {
  margin-bottom: 8px;
  padding: 6px 10px;
  background: #ecf5ff;
  border-left: 3px solid #409eff;
  border-radius: 3px;
  font-size: 12px;
  color: #409eff;
  display: flex;
  align-items: center;
  gap: 4px;
  flex-wrap: wrap;
}

.template-miss-info {
  margin-bottom: 8px;
  padding: 6px 10px;
  background: #fdf6ec;
  border-left: 3px solid #e6a23c;
  border-radius: 3px;
  font-size: 12px;
  color: #e6a23c;
  display: flex;
  align-items: center;
  gap: 4px;
}

.station-ok {
  padding: 12px;
  color: #67C23A;
  font-size: 14px;
}

.station-error {
  padding: 8px 12px;
  background: #fef0f0;
  border-radius: 4px;
  color: #F56C6C;
  font-size: 13px;
}

.station-no-data {
  padding: 12px;
  color: #C0C4CC;
  font-size: 14px;
}

/* ── 空状态 ── */
.empty-state {
  text-align: center;
  padding: 80px 0;
}

.empty-icon {
  font-size: 64px;
  margin-bottom: 16px;
}

.empty-state p {
  font-size: 16px;
  color: #606266;
  margin: 4px 0;
}

.empty-hint {
  font-size: 13px !important;
  color: #909399 !important;
}

/* ── Agent 推理步骤 ── */
.agent-live-section {
  margin-bottom: 24px;
}

.agent-steps-section {
  margin-bottom: 16px;
}

.agent-steps-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  cursor: pointer;
  padding: 8px 0;
  user-select: none;
}

.agent-steps-title {
  margin: 0;
  font-size: 14px;
  color: #606266;
}

.agent-steps-arrow {
  transition: transform 0.25s;
  color: #909399;
}

.agent-steps-arrow.is-expanded {
  transform: rotate(90deg);
}

.agent-steps-slide-enter-active,
.agent-steps-slide-leave-active {
  transition: all 0.25s ease;
  overflow: hidden;
}

.agent-steps-slide-enter-from,
.agent-steps-slide-leave-to {
  max-height: 0;
  opacity: 0;
}

.agent-steps-timeline {
  padding: 4px 0 4px 8px;
  max-height: 400px;
  overflow-y: auto;
}

.agent-step-item {
  display: flex;
  gap: 12px;
  position: relative;
  min-height: 32px;
}

.agent-step-marker {
  display: flex;
  flex-direction: column;
  align-items: center;
  flex-shrink: 0;
  width: 28px;
}

.agent-step-icon {
  font-size: 16px;
  z-index: 1;
}

.agent-step-icon--pulse {
  animation: pulse-pending 1.5s infinite;
}

.agent-step-line {
  flex: 1;
  width: 2px;
  background: #e4e7ed;
  margin: 2px 0;
  min-height: 8px;
}

.agent-step-body {
  flex: 1;
  padding-bottom: 12px;
}

.agent-step-head {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.agent-step-iter {
  font-size: 11px;
  color: #909399;
}

.agent-step-duration {
  font-size: 11px;
  color: #c0c4cc;
  font-family: 'Menlo', 'Consolas', monospace;
}

.agent-step-tool-args {
  margin-top: 4px;
  padding: 4px 8px;
  background: #f5f7fa;
  border-radius: 4px;
  font-size: 11px;
  color: #909399;
  font-family: 'Menlo', 'Consolas', monospace;
  word-break: break-all;
  max-height: 60px;
  overflow: hidden;
}

.agent-step-content {
  margin-top: 4px;
  font-size: 13px;
  color: #606266;
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-word;
  max-height: 120px;
  overflow-y: auto;
}

.agent-step-error {
  margin-top: 4px;
  font-size: 12px;
  color: #f56c6c;
}

.agent-step--thought .agent-step-content {
  border-left: 3px solid #409eff;
  padding-left: 8px;
}

.agent-step--action .agent-step-content {
  border-left: 3px solid #e6a23c;
  padding-left: 8px;
}

.agent-step--reflection .agent-step-content {
  border-left: 3px solid #67c23a;
  padding-left: 8px;
}

.agent-waiting {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 12px 0;
  color: #909399;
  font-size: 14px;
}

.agent-waiting-dot {
  display: inline-block;
  width: 8px;
  height: 8px;
  background: #409eff;
  border-radius: 50%;
  animation: pulse-pending 1.5s infinite;
}

.agent-waiting-text {
  color: #c0c4cc;
  font-size: 13px;
  font-style: italic;
}
</style>
