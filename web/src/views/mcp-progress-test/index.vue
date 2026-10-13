<script setup lang="ts">
import type { McpDiagnosisToolName } from '../../utils/mcp/mcpDiagnosisClient'
import type { McpProgressTab } from './viewModel'
import { Close, CopyDocument, Delete, VideoPlay } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { computed, onBeforeUnmount, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  createMcpClientToken,
  MCP_PROTOCOL_VERSION,
  streamMcpDiagnosis,
} from '../../utils/mcp/mcpDiagnosisClient'
import messages from './components/lang'
import { DEFAULT_MCP_DIAGNOSIS_PRESET, MCP_DIAGNOSIS_PRESETS } from './presets'
import {
  appendMcpEnvelope,
  applyFinalEnvelope,
  beginResultStream,
  buildMcpEndpoint,
  createMcpProgressState,
  createProgressDisplay,
  DEFAULT_MCP_PROGRESS_TAB,
  parseArgumentsJson,
  presentMcpPageError,
  RESULT_STREAM_EMPTY_TEXT,
  stopResultStream,
} from './viewModel'

defineOptions({
  name: 'McpProgressTest',
})

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

type PresetKey = keyof typeof MCP_DIAGNOSIS_PRESETS

const presetOptions: { key: PresetKey, label: string }[] = [
  { key: 'energyFlow', label: t('mcpProgressTest.presetEnergyFlow') },
  { key: 'lineWithConstraintNoModel', label: t('mcpProgressTest.presetStopNoModel') },
  { key: 'lineWithTraditionalModel', label: t('mcpProgressTest.presetStopTraditional') },
  { key: 'lineWithAgent', label: t('mcpProgressTest.presetStopAgent') },
]

const toolOptions: { value: McpDiagnosisToolName, label: string }[] = [
  { value: 'diagnose_energy_flow', label: 'diagnose_energy_flow' },
  { value: 'diagnose_line_stoppage', label: 'diagnose_line_stoppage' },
]

const userStore = useUserStore()
const selectedPreset = ref<PresetKey>(DEFAULT_MCP_DIAGNOSIS_PRESET)
const selectedTool = ref<McpDiagnosisToolName>(MCP_DIAGNOSIS_PRESETS[selectedPreset.value].toolName)
const argumentsJson = ref(JSON.stringify(MCP_DIAGNOSIS_PRESETS[selectedPreset.value].arguments, null, 2))
const progressState = reactive(createMcpProgressState({
  toolName: selectedTool.value,
  progressToken: '',
}))
const activeTab = ref<McpProgressTab>(DEFAULT_MCP_PROGRESS_TAB)
const pageError = ref('')
const startedAt = ref(0)
const elapsedMs = ref(0)
let elapsedTimer: ReturnType<typeof setInterval> | undefined
let activeController: AbortController | undefined

const endpoint = buildMcpEndpoint(
  import.meta.env.VITE_APP_API_BASEURL || '',
  import.meta.env.DEV && import.meta.env.VITE_OPEN_PROXY,
)

const progressDisplay = computed(() => createProgressDisplay(progressState))
const elapsedLabel = computed(() => `${(elapsedMs.value / 1000).toFixed(1)} s`)
const finalResultText = computed(() => progressState.finalResult === undefined
  ? ''
  : JSON.stringify(progressState.finalResult, null, 2))
const statusPresentation = computed(() => {
  switch (progressState.status) {
    case 'running':
      return { label: t('mcpProgressTest.statusRunning'), type: 'primary' as const }
    case 'success':
      return { label: t('mcpProgressTest.statusSuccess'), type: 'success' as const }
    case 'business_error':
      return { label: t('mcpProgressTest.statusBusinessError'), type: 'warning' as const }
    case 'error':
      return { label: t('mcpProgressTest.statusError'), type: 'danger' as const }
    case 'cancelled':
      return { label: t('mcpProgressTest.statusCancelled'), type: 'info' as const }
    default:
      return { label: t('mcpProgressTest.statusPending'), type: 'info' as const }
  }
})

function confidenceLabel(confidence: number | undefined) {
  if (confidence === undefined || !Number.isFinite(confidence)) {
    return '—'
  }
  const normalized = confidence <= 1 ? confidence * 100 : confidence
  return `${Math.min(100, Math.max(0, normalized)).toFixed(0)}%`
}

function applyPreset() {
  const preset = MCP_DIAGNOSIS_PRESETS[selectedPreset.value]
  selectedTool.value = preset.toolName
  argumentsJson.value = JSON.stringify(preset.arguments, null, 2)
}

function applyTool() {
  const matching = presetOptions.find(({ key }) => MCP_DIAGNOSIS_PRESETS[key].toolName === selectedTool.value)
  if (matching) {
    selectedPreset.value = matching.key
    applyPreset()
  }
}

function resetOutput(progressToken = '') {
  Object.assign(progressState, createMcpProgressState({
    toolName: selectedTool.value,
    progressToken,
  }))
  pageError.value = ''
  elapsedMs.value = 0
  activeTab.value = DEFAULT_MCP_PROGRESS_TAB
}

function stopElapsedTimer() {
  if (elapsedTimer !== undefined) {
    clearInterval(elapsedTimer)
    elapsedTimer = undefined
  }
  if (startedAt.value > 0) {
    elapsedMs.value = Date.now() - startedAt.value
  }
}

function startElapsedTimer() {
  startedAt.value = Date.now()
  elapsedMs.value = 0
  elapsedTimer = setInterval(() => {
    elapsedMs.value = Date.now() - startedAt.value
  }, 200)
}

async function startDiagnosis() {
  if (progressState.status === 'running') {
    return
  }

  const parsedArguments = parseArgumentsJson(argumentsJson.value)
  if (!parsedArguments.ok) {
    pageError.value = parsedArguments.message
    progressState.status = 'error'
    return
  }

  try {
    const progressToken = createMcpClientToken()
    resetOutput(progressToken)
    beginResultStream(progressState)
    progressState.status = 'running'
    activeController = new AbortController()
    startElapsedTimer()

    const finalEnvelope = await streamMcpDiagnosis({
      endpoint,
      token: userStore.token,
      toolName: selectedTool.value,
      arguments: parsedArguments.value,
      progressToken,
      signal: activeController.signal,
      onEnvelope: item => appendMcpEnvelope(progressState, item, {
        expectedProgressToken: progressToken,
      }),
    })
    applyFinalEnvelope(progressState, finalEnvelope)

    if (progressState.businessFailure) {
      progressState.status = 'business_error'
      pageError.value = progressState.businessFailure.message
    }
    else {
      progressState.status = 'success'
    }
  }
  catch (error) {
    const presentation = presentMcpPageError(error)
    progressState.status = presentation.code === 'aborted' ? 'cancelled' : 'error'
    pageError.value = presentation.message
  }
  finally {
    stopResultStream(progressState)
    activeController = undefined
    stopElapsedTimer()
  }
}

function cancelDiagnosis() {
  stopResultStream(progressState)
  activeController?.abort()
}

function clearOutput() {
  if (progressState.status !== 'running') {
    resetOutput()
  }
}

async function copyResult() {
  if (!finalResultText.value) {
    ElMessage.warning(t('mcpProgressTest.noResultToCopy'))
    return
  }

  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(finalResultText.value)
    }
    else {
      const textarea = document.createElement('textarea')
      try {
        textarea.value = finalResultText.value
        textarea.setAttribute('readonly', '')
        textarea.style.position = 'fixed'
        textarea.style.opacity = '0'
        document.body.appendChild(textarea)
        textarea.select()
        if (!document.execCommand('copy')) {
          throw new Error('copy unavailable')
        }
      }
      finally {
        textarea.remove()
      }
    }
    ElMessage.success(t('mcpProgressTest.resultCopied'))
  }
  catch {
    ElMessage.error(t('mcpProgressTest.copyFailedManual'))
  }
}

onBeforeUnmount(() => {
  activeController?.abort()
  stopElapsedTimer()
})
</script>

<template>
  <FaPageMain class="mcp-page" main-class="mcp-page__main">
    <template #title>
      <div class="page-heading">
        <div>
          <h1>{{ $t('mcpProgressTest.pageTitle') }}</h1>
          <p>{{ $t('mcpProgressTest.pageDesc') }}</p>
        </div>
        <div class="safe-chips" :aria-label="$t('mcpProgressTest.safeConnectionInfo')">
          <el-tag effect="plain">
            {{ endpoint }}
          </el-tag>
          <el-tag effect="plain" type="info">
            MCP {{ MCP_PROTOCOL_VERSION }}
          </el-tag>
          <el-tag effect="plain" type="success">
            {{ selectedTool }}
          </el-tag>
        </div>
      </div>
    </template>

    <div class="workspace-grid">
      <section class="panel request-panel">
        <div class="panel-title">
          <span>{{ $t('mcpProgressTest.realRequest') }}</span>
          <span class="panel-caption">{{ $t('mcpProgressTest.jwtCaption') }}</span>
        </div>

        <el-form label-position="top">
          <el-form-item :label="$t('mcpProgressTest.diagnosisTool')">
            <el-select v-model="selectedTool" :disabled="progressState.status === 'running'" @change="applyTool">
              <el-option v-for="tool in toolOptions" :key="tool.value" :label="tool.label" :value="tool.value" />
            </el-select>
          </el-form-item>
          <el-form-item :label="$t('mcpProgressTest.requestPreset')">
            <el-select v-model="selectedPreset" :disabled="progressState.status === 'running'" @change="applyPreset">
              <el-option v-for="preset in presetOptions" :key="preset.key" :label="preset.label" :value="preset.key" />
            </el-select>
          </el-form-item>
          <el-form-item label="Arguments JSON">
            <el-input
              v-model="argumentsJson"
              class="json-editor"
              type="textarea"
              :rows="18"
              resize="vertical"
              spellcheck="false"
              :disabled="progressState.status === 'running'"
            />
          </el-form-item>
        </el-form>

        <div class="actions">
          <el-button type="primary" :icon="VideoPlay" :loading="progressState.status === 'running'" :disabled="progressState.status === 'running'" @click="startDiagnosis">
            Start
          </el-button>
          <el-button type="danger" plain :icon="Close" :disabled="progressState.status !== 'running'" @click="cancelDiagnosis">
            Cancel
          </el-button>
          <el-button :icon="Delete" :disabled="progressState.status === 'running'" @click="clearOutput">
            Clear
          </el-button>
          <el-button :icon="CopyDocument" :disabled="!finalResultText" @click="copyResult">
            Copy Result
          </el-button>
        </div>
      </section>

      <section class="panel output-panel">
        <div class="metrics">
          <div class="metric">
            <span>{{ $t('mcpProgressTest.statusLabel') }}</span>
            <el-tag :type="statusPresentation.type" effect="light">
              {{ statusPresentation.label }}
            </el-tag>
          </div>
          <div class="metric">
            <span>{{ $t('mcpProgressTest.elapsed') }}</span>
            <strong>{{ elapsedLabel }}</strong>
          </div>
          <div class="metric">
            <span>Progress Token</span>
            <strong>{{ progressState.progressTokenSuffix ? `…${progressState.progressTokenSuffix}` : '—' }}</strong>
          </div>
        </div>

        <div class="progress-card">
          <div class="progress-copy">
            <span>{{ $t('mcpProgressTest.serverProgress') }}</span>
            <strong>{{ progressDisplay.valueLabel }}</strong>
          </div>
          <el-progress
            v-if="progressDisplay.percentage !== null"
            :percentage="progressDisplay.percentage"
            :stroke-width="12"
            :status="progressState.status === 'success' ? 'success' : undefined"
          />
          <p v-else-if="progressState.hasProgress" class="progress-unavailable">
            {{ $t('mcpProgressTest.noTotalPercent') }}
          </p>
          <p v-else class="progress-unavailable">
            {{ $t('mcpProgressTest.noProgressYet') }}
          </p>
        </div>

        <el-alert v-if="pageError" class="error-alert" :title="pageError" type="error" :closable="false" show-icon />

        <el-tabs v-model="activeTab" class="result-tabs">
          <el-tab-pane name="stream">
            <template #label>
              {{ $t('mcpProgressTest.tabStream') }} <span class="tab-count">{{ progressState.resultChunks.length }}</span>
            </template>
            <div v-if="progressState.resultChunks.length" class="result-stream" aria-live="polite">
              <article v-for="chunk in progressState.resultChunks" :key="chunk.sequence" class="result-chunk">
                <div class="result-chunk__meta">
                  <el-tag size="small" type="success" effect="dark">
                    {{ chunk.eventLabel || (chunk.eventType ? $t('mcpProgressTest.pageEvent') : $t('mcpProgressTest.resultChunk')) }}
                  </el-tag>
                  <el-tag v-if="chunk.eventType" size="small" type="primary" effect="plain">
                    {{ chunk.eventType }}
                  </el-tag>
                  <strong v-if="chunk.stationCode">{{ chunk.stationCode }}</strong>
                  <span>#{{ chunk.sequence }}</span>
                  <span>token …{{ chunk.progressTokenSuffix }}</span>
                </div>
                <div v-if="chunk.llmReport" class="ai-diagnosis">
                  <div class="ai-diagnosis__heading">
                    <strong>{{ $t('mcpProgressTest.aiConclusion') }}</strong>
                    <div class="ai-diagnosis__tags">
                      <el-tag v-if="chunk.llmReport.rootCauseStation" type="danger" effect="light">
                        {{ $t('mcpProgressTest.rootCauseStation') }}{{ chunk.llmReport.rootCauseStation }}
                      </el-tag>
                      <el-tag type="info" effect="plain">
                        {{ $t('mcpProgressTest.confidence') }}{{ confidenceLabel(chunk.llmReport.confidence) }}
                      </el-tag>
                    </div>
                  </div>

                  <p class="ai-diagnosis__conclusion">
                    {{ chunk.llmReport.rootCauseDescription || chunk.content }}
                  </p>

                  <div v-if="chunk.llmReport.secondaryCauses.length" class="ai-diagnosis__section secondary-causes">
                    <strong>{{ $t('mcpProgressTest.secondaryCauses') }}</strong>
                    <div v-for="(cause, causeIndex) in chunk.llmReport.secondaryCauses" :key="`${cause.stationCode || 'cause'}-${causeIndex}`" class="secondary-cause">
                      <el-tag v-if="cause.stationCode" size="small" type="warning" effect="light">
                        {{ cause.stationCode }}
                      </el-tag>
                      <span>{{ cause.description || '—' }}</span>
                    </div>
                  </div>

                  <div v-if="chunk.llmReport.impactChain" class="ai-diagnosis__section impact-chain">
                    <strong>{{ $t('mcpProgressTest.impactChain') }}</strong>
                    <span>{{ chunk.llmReport.impactChain }}</span>
                  </div>

                  <div v-if="chunk.llmReport.suggestedActions.length" class="ai-diagnosis__section suggested-actions">
                    <strong>{{ $t('mcpProgressTest.suggestedActions') }}</strong>
                    <ol>
                      <li v-for="(action, actionIndex) in chunk.llmReport.suggestedActions" :key="actionIndex">
                        {{ action }}
                      </li>
                    </ol>
                  </div>

                  <details v-if="chunk.llmReport.reasoning" class="ai-diagnosis__details">
                    <summary>{{ $t('mcpProgressTest.reasoning') }}</summary>
                    <p>{{ chunk.llmReport.reasoning }}</p>
                  </details>
                </div>
                <div v-else class="result-chunk__content">
                  {{ chunk.content }}
                </div>
                <details v-if="chunk.rawContent" class="raw-event-details">
                  <summary>{{ $t('mcpProgressTest.rawEventJson', { type: chunk.eventType || 'progress' }) }}</summary>
                  <pre><code>{{ chunk.rawContent }}</code></pre>
                </details>
              </article>
            </div>
            <el-empty v-else :description="RESULT_STREAM_EMPTY_TEXT" :image-size="72" />
          </el-tab-pane>

          <el-tab-pane name="timeline">
            <template #label>
              {{ $t('mcpProgressTest.tabTimeline') }} <span class="tab-count">{{ progressState.timeline.length }}</span>
            </template>
            <div v-if="progressState.timeline.length" class="timeline">
              <article v-for="item in progressState.timeline" :key="item.sequence" class="timeline-item">
                <div class="timeline-marker">
                  {{ item.sequence }}
                </div>
                <div class="timeline-body">
                  <div class="timeline-meta">
                    <strong v-if="item.percentage !== null">{{ item.percentage.toFixed(1) }}%</strong>
                    <strong v-else>progress {{ item.progress }}</strong>
                    <el-tag v-if="item.nestedIo" size="small" type="warning" effect="light">
                      {{ $t('mcpProgressTest.nestedIoTag') }}
                    </el-tag>
                    <span>token …{{ item.progressTokenSuffix || '—' }}</span>
                  </div>
                  <p>{{ item.message || '—' }}</p>
                </div>
              </article>
            </div>
            <el-empty v-else :description="$t('mcpProgressTest.noProgressYet')" :image-size="72" />
          </el-tab-pane>

          <el-tab-pane name="raw">
            <template #label>
              Raw SSE / JSON-RPC <span class="tab-count">{{ progressState.rawBlocks.length }}</span>
            </template>
            <div v-if="progressState.rawBlocks.length" class="raw-list">
              <pre v-for="(block, index) in progressState.rawBlocks" :key="index"><code>{{ block }}</code></pre>
            </div>
            <el-empty v-else :description="$t('mcpProgressTest.noEnvelope')" :image-size="72" />
          </el-tab-pane>

          <el-tab-pane name="result">
            <template #label>
              {{ $t('mcpProgressTest.tabResult') }}
            </template>
            <pre v-if="finalResultText" class="final-result"><code>{{ finalResultText }}</code></pre>
            <el-empty v-else :description="$t('mcpProgressTest.noFinalResult')" :image-size="72" />
          </el-tab-pane>
        </el-tabs>
      </section>
    </div>
  </FaPageMain>
</template>

<style scoped>
.mcp-page {
  min-height: calc(100vh - 8rem);
}

:deep(.mcp-page__main) {
  padding: 0;
}

.page-heading,
.safe-chips,
.panel-title,
.actions,
.metrics,
.progress-copy,
.timeline-meta {
  display: flex;
  align-items: center;
}

.page-heading {
  justify-content: space-between;
  gap: 1rem;
}

.page-heading h1 {
  margin: 0;
  font-size: 1.15rem;
  font-weight: 650;
}

.page-heading p {
  margin: 0.35rem 0 0;
  color: var(--el-text-color-secondary);
  font-size: 0.85rem;
}

.safe-chips {
  flex-wrap: wrap;
  justify-content: flex-end;
  gap: 0.5rem;
}

.workspace-grid {
  display: grid;
  grid-template-columns: minmax(20rem, 0.85fr) minmax(28rem, 1.45fr);
  min-height: calc(100vh - 12.5rem);
}

.panel {
  min-width: 0;
  padding: 1.25rem;
}

.request-panel {
  border-right: 1px solid var(--el-border-color-lighter);
  background: color-mix(in srgb, var(--el-fill-color-lighter) 55%, transparent);
}

.panel-title {
  justify-content: space-between;
  gap: 0.75rem;
  margin-bottom: 1rem;
  font-weight: 650;
}

.panel-caption {
  color: var(--el-text-color-secondary);
  font-size: 0.75rem;
  font-weight: 400;
}

.json-editor :deep(textarea),
.raw-list pre,
.final-result {
  font-family: "Cascadia Code", "SFMono-Regular", Consolas, monospace;
  font-size: 0.78rem;
  line-height: 1.55;
}

.actions {
  flex-wrap: wrap;
  gap: 0.5rem;
}

.actions :deep(.el-button + .el-button) {
  margin-left: 0;
}

.metrics {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 0.75rem;
}

.metric {
  display: flex;
  min-height: 4.5rem;
  flex-direction: column;
  align-items: flex-start;
  justify-content: center;
  gap: 0.45rem;
  padding: 0.85rem 1rem;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 0.75rem;
  background: var(--el-fill-color-extra-light);
}

.metric > span:first-child {
  color: var(--el-text-color-secondary);
  font-size: 0.75rem;
}

.progress-card {
  margin-top: 1rem;
  padding: 1rem;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 0.75rem;
}

.progress-copy {
  justify-content: space-between;
  margin-bottom: 0.7rem;
  font-size: 0.85rem;
}

.progress-unavailable {
  margin: 0;
  color: var(--el-text-color-secondary);
  font-size: 0.8rem;
}

.error-alert {
  margin-top: 1rem;
}

.result-tabs {
  margin-top: 1rem;
}

.tab-count {
  display: inline-flex;
  min-width: 1.25rem;
  height: 1.25rem;
  align-items: center;
  justify-content: center;
  margin-left: 0.35rem;
  padding: 0 0.35rem;
  border-radius: 999px;
  background: var(--el-fill-color);
  font-size: 0.7rem;
}

.timeline {
  max-height: 32rem;
  overflow: auto;
  padding: 0.25rem;
}

.result-stream {
  display: grid;
  max-height: 34rem;
  gap: 0.75rem;
  overflow: auto;
  padding: 0.25rem;
}

.result-chunk {
  overflow: hidden;
  border: 1px solid var(--el-color-success-light-5);
  border-radius: 0.75rem;
  background: color-mix(in srgb, var(--el-color-success-light-9) 72%, var(--el-bg-color));
}

.result-chunk__meta {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.55rem;
  padding: 0.65rem 0.8rem;
  border-bottom: 1px solid var(--el-color-success-light-7);
  color: var(--el-text-color-secondary);
  font-family: "Cascadia Code", Consolas, monospace;
  font-size: 0.72rem;
}

.result-chunk__content {
  padding: 0.85rem;
  color: var(--el-text-color-primary);
  line-height: 1.65;
  overflow-wrap: anywhere;
  white-space: pre-wrap;
}

.ai-diagnosis {
  padding: 1rem;
  color: var(--el-text-color-primary);
}

.ai-diagnosis__heading,
.ai-diagnosis__tags,
.secondary-cause,
.impact-chain {
  display: flex;
  align-items: center;
}

.ai-diagnosis__heading {
  flex-wrap: wrap;
  justify-content: space-between;
  gap: 0.75rem;
}

.ai-diagnosis__tags {
  flex-wrap: wrap;
  gap: 0.5rem;
}

.ai-diagnosis__conclusion {
  margin: 0.9rem 0;
  line-height: 1.75;
}

.ai-diagnosis__section {
  margin-top: 0.75rem;
  padding: 0.75rem;
  border-radius: 0.45rem;
  background: color-mix(in srgb, var(--el-color-warning-light-9) 72%, var(--el-bg-color));
  line-height: 1.65;
}

.secondary-causes {
  display: grid;
  gap: 0.5rem;
}

.secondary-cause,
.impact-chain {
  gap: 0.65rem;
}

.suggested-actions ol {
  margin: 0.5rem 0 0;
  padding-left: 1.35rem;
}

.suggested-actions li + li {
  margin-top: 0.25rem;
}

.ai-diagnosis__details,
.raw-event-details {
  border-top: 1px solid var(--el-border-color-extra-light);
}

.ai-diagnosis__details {
  margin-top: 0.9rem;
  padding-top: 0.75rem;
}

.ai-diagnosis__details summary,
.raw-event-details summary {
  cursor: pointer;
  color: var(--el-text-color-secondary);
}

.ai-diagnosis__details p {
  margin: 0.65rem 0 0;
  line-height: 1.7;
  white-space: pre-wrap;
}

.raw-event-details {
  padding: 0.65rem 0.85rem;
  font-size: 0.75rem;
}

.raw-event-details pre {
  overflow: auto;
  max-height: 18rem;
  margin: 0.65rem 0 0;
  padding: 0.75rem;
  border-radius: 0.45rem;
  background: var(--el-fill-color-extra-light);
  font-family: "Cascadia Code", Consolas, monospace;
  line-height: 1.55;
  white-space: pre-wrap;
  word-break: break-word;
}

.timeline-item {
  display: grid;
  grid-template-columns: 2rem minmax(0, 1fr);
  gap: 0.75rem;
  padding: 0.7rem 0;
}

.timeline-marker {
  display: grid;
  width: 1.75rem;
  height: 1.75rem;
  place-items: center;
  border-radius: 50%;
  background: var(--el-color-primary-light-9);
  color: var(--el-color-primary);
  font-size: 0.72rem;
  font-weight: 700;
}

.timeline-body {
  padding-bottom: 0.75rem;
  border-bottom: 1px solid var(--el-border-color-extra-light);
}

.timeline-meta {
  flex-wrap: wrap;
  gap: 0.55rem;
}

.timeline-meta > span:last-child {
  color: var(--el-text-color-secondary);
  font-family: "Cascadia Code", Consolas, monospace;
  font-size: 0.72rem;
}

.timeline-body p {
  margin: 0.4rem 0 0;
  color: var(--el-text-color-regular);
  word-break: break-word;
}

.raw-list {
  display: grid;
  max-height: 34rem;
  gap: 0.75rem;
  overflow: auto;
}

.raw-list pre,
.final-result {
  overflow: auto;
  margin: 0;
  padding: 0.9rem;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 0.6rem;
  background: var(--el-fill-color-extra-light);
  white-space: pre-wrap;
  word-break: break-word;
}

.final-result {
  max-height: 34rem;
}

@media (max-width: 980px) {
  .workspace-grid {
    grid-template-columns: 1fr;
  }

  .request-panel {
    border-right: 0;
    border-bottom: 1px solid var(--el-border-color-lighter);
  }

  .page-heading {
    align-items: flex-start;
    flex-direction: column;
  }

  .safe-chips {
    justify-content: flex-start;
  }
}

@media (max-width: 580px) {
  .metrics {
    grid-template-columns: 1fr;
  }

  .panel {
    padding: 1rem;
  }

  .panel-title {
    align-items: flex-start;
    flex-direction: column;
  }
}
</style>
