<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import messages from './section.lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}

const route = useRoute()
const router = useRouter()

const sections = computed(() => ({
  LineUpHistory: {
    eyebrow: 'LINEUP / HISTORY',
    title: t('lineupSection_form.historyTitle'),
    description: t('lineupSection_form.historyDescription'),
    items: [
      t('lineupSection_form.historyItemRuns'),
      t('lineupSection_form.historyItemSteps'),
      t('lineupSection_form.historyItemExceptions'),
      t('lineupSection_form.historyItemTrends'),
    ],
    phase: t('lineupSection_form.phaseHistoryMigrating'),
  },
  LineUpBusinessConfig: {
    eyebrow: 'LINEUP / BUSINESS CONFIG',
    title: t('lineupSection_form.businessConfigTitle'),
    description: t('lineupSection_form.businessConfigDescription'),
    items: [
      t('lineupSection_form.businessConfigItemBinding'),
      t('lineupSection_form.businessConfigItemStep'),
      t('lineupSection_form.businessConfigItemRules'),
      t('lineupSection_form.businessConfigItemSolutions'),
    ],
    phase: t('lineupSection_form.phaseConfigMigrating'),
  },
}))

const current = computed(() => sections.value[route.name as keyof ReturnType<typeof sections.value>] ?? sections.value.LineUpHistory)
</script>

<template>
  <main class="lineup-section">
    <header>
      <div>
        <span>{{ current.eyebrow }}</span>
        <h2>{{ current.title }}</h2>
        <p>{{ current.description }}</p>
      </div>
      <el-tag type="warning" effect="plain">
        {{ current.phase }}
      </el-tag>
    </header>

    <section class="scope-card">
      <h3>{{ $t('lineupSection_form.scopeTitle') }}</h3>
      <div class="scope-grid">
        <div v-for="item in current.items" :key="item">
          <i class="i-ant-design:check-circle-outlined" />
          <span>{{ item }}</span>
        </div>
      </div>
      <el-alert
        :title="$t('lineupSection_form.menuIndependenceAlert')"
        type="info"
        :closable="false"
        show-icon
      />
      <el-button type="primary" @click="router.push('/lineup/import')">
        {{ $t('lineupSection_form.gotoImport') }}
      </el-button>
    </section>
  </main>
</template>

<style scoped>
.lineup-section {
  display: grid;
  gap: 16px;
  min-height: calc(100vh - 116px);
  padding: 16px;
  background: var(--el-fill-color-lighter);
}

.lineup-section header,
.scope-card {
  background: var(--el-bg-color);
  border: 1px solid var(--el-border-color-light);
  border-radius: 8px;
}

.lineup-section header {
  display: flex;
  gap: 24px;
  align-items: center;
  justify-content: space-between;
  padding: 18px;
}

.lineup-section header span {
  font-size: 11px;
  font-weight: 700;
  color: var(--el-color-primary);
  letter-spacing: 0.1em;
}

.lineup-section h2 {
  margin: 4px 0;
}

.lineup-section p {
  margin: 0;
  color: var(--el-text-color-secondary);
}

.scope-card {
  align-self: start;
  padding: 18px;
}

.scope-card h3 {
  margin: 0 0 14px;
}

.scope-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  gap: 10px;
  margin-bottom: 18px;
}

.scope-grid div {
  display: flex;
  gap: 8px;
  align-items: center;
  padding: 12px;
  background: var(--el-fill-color-light);
  border-radius: 6px;
}

.scope-grid i {
  color: var(--el-color-success);
}

.scope-card .el-button {
  margin-top: 18px;
}
</style>
