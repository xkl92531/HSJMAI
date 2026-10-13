<script setup lang="ts">
import type {
  LineUpImportNode,
  LineUpImportSequence,
  LineUpIssue,
} from '@/api/modules/LineUp/types'
import { computed, ref, watch } from 'vue'
import { normalizeIssueSeverity } from '@/api/modules/LineUp/types'
import { useI18n } from 'vue-i18n'
import messages from './lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}

const props = defineProps<{
  issues: LineUpIssue[]
  sequence: LineUpImportSequence | null
  node: LineUpImportNode | null
  renamedValue: string
}>()

const emit = defineEmits<{
  rename: [sourceKey: string, displayName: string]
}>()

const severity = ref<'All' | 'Blocking' | 'Warning' | 'Info'>('All')
const displayName = ref('')

watch(
  () => [props.sequence?.sourceKey, props.node?.sourceKey, props.renamedValue] as const,
  () => {
    displayName.value = props.renamedValue || props.node?.displayName || props.sequence?.displayName || ''
  },
  { immediate: true },
)

const filteredIssues = computed(() => props.issues.filter((issue) => {
  if (severity.value !== 'All' && normalizeIssueSeverity(issue.severity) !== severity.value) {
    return false
  }
  if (!props.sequence) {
    return true
  }
  if (!issue.sourceKey) {
    return true
  }
  return issue.sourceKey === props.sequence.sourceKey || issue.sourceKey === props.node?.sourceKey
}))

const selectedItem = computed(() => props.node ?? props.sequence)

function saveDisplayName() {
  if (!selectedItem.value) {
    return
  }
  emit('rename', selectedItem.value.sourceKey, displayName.value)
}

function severityLabel(issue: LineUpIssue) {
  const value = normalizeIssueSeverity(issue.severity)
  if (value === 'Blocking') {
    return t('lineupWorkspace_form.severityBlocking')
  }
  if (value === 'Warning') {
    return t('lineupWorkspace_form.severityWarning')
  }
  return t('lineupWorkspace_form.severityInfo')
}
</script>

<template>
  <section class="issue-panel">
    <div class="property-section">
      <div class="section-title">
        <span>{{ $t('lineupWorkspace_form.propertiesTitle') }}</span>
        <small>{{ node ? node.nodeKind : sequence ? 'Sequence' : $t('lineupWorkspace_form.notSelected') }}</small>
      </div>

      <template v-if="selectedItem">
        <label class="field-label" for="lineup-display-name">{{ $t('lineupWorkspace_form.fieldDisplayName') }}</label>
        <div class="rename-row">
          <el-input id="lineup-display-name" v-model="displayName" @keyup.enter="saveDisplayName" />
          <el-button @click="saveDisplayName">
            {{ $t('lineupWorkspace_form.apply') }}
          </el-button>
        </div>
        <dl class="property-grid">
          <dt>{{ $t('lineupWorkspace_form.originalName') }}</dt>
          <dd>{{ selectedItem.sourceName || '—' }}</dd>
          <dt>SourceKey</dt>
          <dd class="mono" :title="selectedItem.sourceKey">
            {{ selectedItem.sourceKey }}
          </dd>
          <template v-if="node">
            <dt>{{ $t('lineupWorkspace_form.numberLabel') }}</dt>
            <dd>{{ node.nodeKind === 'Step' ? 'S' : 'T' }}{{ node.number }}</dd>
            <dt>{{ $t('lineupWorkspace_form.commentLabel') }}</dt>
            <dd>{{ node.comment || '—' }}</dd>
          </template>
        </dl>
        <p class="identity-note">
          {{ $t('lineupWorkspace_form.renameNote') }}
        </p>
      </template>
      <el-empty v-else :image-size="42" :description="$t('lineupWorkspace_form.emptyPickNode')" />
    </div>

    <div class="issues-section">
      <div class="section-title">
        <span>{{ $t('lineupWorkspace_form.issuesTitle') }}</span>
        <el-select v-model="severity" size="small" style="width: 92px;">
          <el-option :label="$t('lineupWorkspace_form.filterAll')" value="All" />
          <el-option :label="$t('lineupWorkspace_form.severityBlocking')" value="Blocking" />
          <el-option :label="$t('lineupWorkspace_form.severityWarning')" value="Warning" />
          <el-option :label="$t('lineupWorkspace_form.severityInfo')" value="Info" />
        </el-select>
      </div>

      <div class="issue-list">
        <article
          v-for="(issue, index) in filteredIssues"
          :key="`${issue.issueType}:${issue.sourceKey}:${index}`"
          class="issue-item"
          :data-severity="normalizeIssueSeverity(issue.severity)"
        >
          <div>
            <strong>{{ severityLabel(issue) }} · {{ issue.issueType }}</strong>
            <span v-if="issue.sourceKey" class="mono" :title="issue.sourceKey">{{ issue.sourceKey }}</span>
          </div>
          <p>{{ issue.message }}</p>
        </article>
        <el-empty v-if="filteredIssues.length === 0" :image-size="42" :description="$t('lineupWorkspace_form.emptyNoIssues')" />
      </div>
    </div>
  </section>
</template>

<style scoped>
.issue-panel {
  display: grid;
  grid-template-rows: minmax(230px, auto) minmax(220px, 1fr);
  min-width: 0;
  height: 100%;
  min-height: 0;
  overflow: hidden;
}

.property-section,
.issues-section {
  min-width: 0;
  padding: 14px;
  overflow: hidden;
}

.property-section {
  border-bottom: 1px solid var(--el-border-color-lighter);
}

.issues-section {
  display: grid;
  grid-template-rows: auto minmax(0, 1fr);
}

.section-title {
  display: flex;
  gap: 10px;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 12px;
  font-size: 14px;
  font-weight: 700;
}

.section-title small {
  font-weight: 500;
  color: var(--el-text-color-secondary);
}

.field-label {
  display: block;
  margin-bottom: 5px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.rename-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 6px;
}

.property-grid {
  display: grid;
  grid-template-columns: 64px minmax(0, 1fr);
  gap: 8px;
  margin: 14px 0 0;
  font-size: 12px;
}

.property-grid dt {
  color: var(--el-text-color-secondary);
}

.property-grid dd {
  min-width: 0;
  margin: 0;
  overflow: hidden;
  text-overflow: ellipsis;
}

.mono {
  overflow: hidden;
  text-overflow: ellipsis;
  font-family: Consolas, monospace;
  font-size: 10px;
  white-space: nowrap;
}

.identity-note {
  margin: 12px 0 0;
  font-size: 11px;
  line-height: 1.55;
  color: var(--el-text-color-secondary);
}

.issue-list {
  min-height: 0;
  padding-right: 4px;
  overflow: auto;
}

.issue-item {
  padding: 9px 10px;
  margin-bottom: 8px;
  background: var(--el-fill-color-lighter);
  border-left: 3px solid var(--el-color-info);
  border-radius: 4px;
}

.issue-item[data-severity="Warning"] {
  border-left-color: var(--el-color-warning);
}

.issue-item[data-severity="Blocking"] {
  border-left-color: var(--el-color-danger);
}

.issue-item > div {
  display: flex;
  gap: 8px;
  justify-content: space-between;
  min-width: 0;
}

.issue-item strong {
  font-size: 11px;
}

.issue-item span {
  max-width: 45%;
  color: var(--el-text-color-secondary);
}

.issue-item p {
  margin: 5px 0 0;
  font-size: 12px;
  line-height: 1.5;
}
</style>
