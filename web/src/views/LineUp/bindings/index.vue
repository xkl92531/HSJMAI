<script setup lang="ts">
import type { LineUpMonitorBindingSummary } from '@/api/modules/LineUp/monitor'
import { ElMessage, ElMessageBox } from 'element-plus'
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import LineUpMonitorTree from '@/views/LineUp/components/LineUpMonitorTree.vue'
import { createLineUpBindingWorkspace } from './useLineUpBindingWorkspace'
import { useI18n } from 'vue-i18n'
import messages from './components/lang'

const router = useRouter()

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}
const workspace = createLineUpBindingWorkspace()
const treeCollapsed = ref(false)
const selectedPathText = computed(() => workspace.selectedPath.value.map(node => (
  node.code || node.name
)).join(' / '))
const errorText = computed(() => ({
  LineUpBindingPrepareRequired: t('lineupBindings_form.errPrepareRequired'),
  LineUpBindingValidationRequired: t('lineupBindings_form.errValidationRequired'),
  LineUpMonitorRequestFailed: t('lineupBindings_form.errRequestFailed'),
  LineUpBindingActionFailed: t('lineupBindings_form.errActionFailed'),
}[workspace.errorCode.value] ?? (workspace.errorCode.value ? t('lineupBindings_form.errIncomplete') : '')))

function graphLink() {
  return {
    path: '/lineup/graphs',
    query: { subStationId: workspace.selectedSubStationId.value ?? undefined },
  }
}

function monitorLink(binding: LineUpMonitorBindingSummary) {
  return {
    path: '/lineup/monitor',
    query: {
      subStationId: workspace.selectedSubStationId.value,
      bindingId: binding.bindingId,
      sequenceSourceKey: binding.sequences[0]?.sequenceSourceKey,
    },
  }
}

async function run(action: () => Promise<void>, success: string) {
  try {
    await action()
    ElMessage.success(success)
  }
  catch {
    if (errorText.value) {
      ElMessage.error(errorText.value)
    }
  }
}

async function confirmMonitoring(binding: LineUpMonitorBindingSummary, enabled: boolean) {
  await ElMessageBox.confirm(
    enabled ? t('lineupBindings_form.confirmEnableMonitoring') : t('lineupBindings_form.confirmDisableMonitoring'),
    enabled ? t('lineupBindings_form.enableMonitoring') : t('lineupBindings_form.disableMonitoring'),
    { type: enabled ? 'warning' : 'info' },
  )
  await run(
    () => workspace.setMonitoring(binding.bindingId, enabled),
    enabled ? t('lineupBindings_form.monitoringEnabled') : t('lineupBindings_form.monitoringDisabled'),
  )
}

onMounted(() => workspace.refreshNavigation())
</script>

<template>
  <div class="binding-page">
    <LineUpMonitorTree
      :nodes="workspace.navigationNodes.value"
      :selected-sub-station-id="workspace.selectedSubStationId.value"
      :loading="workspace.navigationLoading.value"
      :collapsed="treeCollapsed"
      @select-sub-station="workspace.selectSubStation"
      @search="workspace.refreshNavigation"
      @refresh="workspace.refreshNavigation()"
      @toggle-collapsed="treeCollapsed = !treeCollapsed"
    />
    <main class="binding-page__main">
      <header class="binding-page__header">
        <div>
          <small>{{ $t('lineupBindings_form.currentProductionPosition') }}</small>
          <h2>{{ selectedPathText || $t('lineupBindings_form.selectStationFirst') }}</h2>
        </div>
        <div class="binding-page__header-actions">
          <el-button :loading="workspace.bindingLoading.value" @click="workspace.refresh()">
            {{ $t('lineupBindings_form.refresh') }}
          </el-button>
          <el-button :disabled="!workspace.selectedSubStationId.value" @click="router.push(graphLink())">
            {{ $t('lineupBindings_form.graphCatalog') }}
          </el-button>
        </div>
      </header>

      <el-alert v-if="errorText" :title="errorText" type="error" :closable="false" />
      <el-empty
        v-if="!workspace.selectedSubStationId.value"
        :description="$t('lineupBindings_form.selectStationEmpty')"
      />
      <el-empty
        v-else-if="!workspace.bindingLoading.value && workspace.bindings.value.length === 0"
        :description="$t('lineupBindings_form.noBindingsEmpty')"
      />
      <section v-else v-loading="workspace.bindingLoading.value" class="binding-page__cards">
        <article v-for="binding in workspace.bindings.value" :key="binding.bindingId" class="binding-card">
          <div class="binding-card__summary">
            <div>
              <small>GRAPH</small>
              <h3>{{ binding.graphSourceName }}</h3>
            </div>
            <el-tag :type="binding.monitoringEnabled ? 'success' : 'info'">
              {{ binding.monitoringEnabled ? $t('lineupBindings_form.monitoringActive') : binding.bindingStatus }}
            </el-tag>
          </div>
          <dl>
            <div><dt>{{ $t('lineupBindings_form.publishedRevision') }}</dt><dd>{{ binding.publishedRevisionId }}</dd></div>
            <div><dt>Sequence</dt><dd>{{ binding.sequences.length }}</dd></div>
            <div><dt>{{ $t('lineupBindings_form.runtimeBinding') }}</dt><dd>{{ binding.runtimeBindingReady ? $t('lineupBindings_form.generated') : $t('lineupBindings_form.pendingGeneration') }}</dd></div>
          </dl>
          <div v-if="workspace.preparations.value[binding.bindingId]" class="binding-card__issues">
            {{ $t('lineupBindings_form.prepareIssues', { count: workspace.preparations.value[binding.bindingId].issues.length }) }}
          </div>
          <div v-if="workspace.validations.value[binding.bindingId]" class="binding-card__issues">
            {{ $t('lineupBindings_form.validationResult', { good: workspace.validations.value[binding.bindingId].goodCount, total: workspace.validations.value[binding.bindingId].totalCount }) }}
          </div>
          <footer>
            <el-button
              :loading="workspace.actionBusy.value[binding.bindingId]"
              @click="run(() => workspace.prepare(binding.bindingId), t('lineupBindings_form.runtimeBindingGenerated'))"
            >
              {{ $t('lineupBindings_form.generateBinding') }}
            </el-button>
            <el-button
              :disabled="!workspace.canValidate(binding.bindingId)"
              :loading="workspace.actionBusy.value[binding.bindingId]"
              @click="run(() => workspace.validate(binding.bindingId), t('lineupBindings_form.validationCompleted'))"
            >
              {{ $t('lineupBindings_form.onlineValidation') }}
            </el-button>
            <el-button
              v-if="!binding.monitoringEnabled"
              type="primary"
              :disabled="!workspace.canEnable(binding.bindingId)"
              @click="confirmMonitoring(binding, true)"
            >
              {{ $t('lineupBindings_form.enableMonitoring') }}
            </el-button>
            <el-button v-else type="warning" @click="confirmMonitoring(binding, false)">
              {{ $t('lineupBindings_form.disableMonitoring') }}
            </el-button>
            <el-button text type="primary" @click="router.push(monitorLink(binding))">
              {{ $t('lineupBindings_form.enterMonitor') }}
            </el-button>
          </footer>
        </article>
      </section>
    </main>
  </div>
</template>

<style scoped lang="scss">
.binding-page {
  display: flex;
  height: 100%;
  min-height: 0;
  overflow: hidden;
  background: var(--el-bg-color-page);
}

.binding-page__main {
  flex: 1;
  min-width: 0;
  padding: 18px;
  overflow: auto;
}

.binding-page__header,
.binding-page__header-actions,
.binding-card__summary,
.binding-card footer,
.binding-card dl,
.binding-card dl div {
  display: flex;
  align-items: center;
}

.binding-page__header,
.binding-card__summary {
  justify-content: space-between;
}

.binding-page__header {
  margin-bottom: 16px;
}

.binding-page__header h2,
.binding-card h3 {
  margin: 3px 0 0;
}

.binding-page__header-actions,
.binding-card footer {
  gap: 8px;
}

.binding-page__cards {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(360px, 1fr));
  gap: 14px;
  margin-top: 14px;
}

.binding-card {
  padding: 18px;
  background: var(--el-bg-color);
  border: 1px solid var(--el-border-color-light);
  border-radius: 8px;
}

.binding-card dl {
  gap: 20px;
  padding: 12px 0;
  margin: 12px 0;
  border-top: 1px solid var(--el-border-color-lighter);
  border-bottom: 1px solid var(--el-border-color-lighter);
}

.binding-card dl div {
  flex-direction: column;
  align-items: flex-start;
}

.binding-card dt,
.binding-page small {
  color: var(--el-text-color-secondary);
}

.binding-card dd {
  margin: 3px 0 0;
}

.binding-card__issues {
  margin: 8px 0;
  color: var(--el-text-color-regular);
}

.binding-card footer {
  flex-wrap: wrap;
  margin-top: 14px;
}
</style>
