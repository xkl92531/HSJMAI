<script setup lang="ts">
import type { LineUpImportAction } from '@/api/modules/LineUp/types'
import { useI18n } from 'vue-i18n'
import messages from './lang'
import { computed } from 'vue'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}

const emptyMessage = computed(() => props.detailUnavailable ? t('lineupShared.detailUnavailableTip') : t('lineupShared.noActionsDefined'))

const props = withDefaults(defineProps<{
  actions?: LineUpImportAction[]
  title?: string
  detailUnavailable?: boolean
}>(), {
  actions: () => [],
  title: '',
  detailUnavailable: false,
})

function displayValue(value: string | null) {
  return value || '—'
}

function displayInterlock(value: boolean | null) {
  if (value === null) {
    return '—'
  }
  return value ? 'TRUE' : 'FALSE'
}
</script>

<template>
  <section class="tia-action-table" aria-label="TIA GRAPH actions">
    <h4 v-if="title" class="action-group-title">
      {{ title }}
    </h4>
    <div class="action-table-scroll">
      <table v-if="actions.length">
        <thead>
          <tr>
            <th>Interlock</th>
            <th>Event</th>
            <th>Qualifier</th>
            <th>Action</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="item in actions" :key="item.sourceKey">
            <td>{{ displayInterlock(item.interlock) }}</td>
            <td>{{ displayValue(item.event) }}</td>
            <td>{{ displayValue(item.qualifier) }}</td>
            <td class="action-text">
              {{ item.actionText || '—' }}
            </td>
          </tr>
        </tbody>
      </table>
      <p v-else class="empty-message">
        {{ emptyMessage }}
      </p>
    </div>
  </section>
</template>

<style scoped>
.tia-action-table {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
  min-height: 0;
}

.action-group-title {
  margin: 0 0 8px;
  font-size: 12px;
  font-weight: 600;
  color: #303133;
  overflow-wrap: anywhere;
}

.action-table-scroll {
  flex: 1;
  min-height: 0;
  overflow: auto;
  background: #fff;
  border: 1px solid #aeb4bd;
}

table {
  width: 100%;
  min-width: 520px;
  font-family: "Segoe UI", Arial, sans-serif;
  font-size: 12px;
  border-spacing: 0;
  border-collapse: collapse;
}

th,
td {
  padding: 6px 8px;
  vertical-align: top;
  text-align: left;
  border-right: 1px solid #c9cdd3;
  border-bottom: 1px solid #c9cdd3;
}

th {
  position: sticky;
  top: 0;
  z-index: 1;
  color: #1f2937;
  background: #e8ebef;
}

th:first-child,
td:first-child {
  width: 82px;
}

th:nth-child(2),
td:nth-child(2),
th:nth-child(3),
td:nth-child(3) {
  width: 88px;
}

th:last-child,
td:last-child {
  border-right: 0;
}

tbody tr:last-child td {
  border-bottom: 0;
}

.action-text {
  min-width: 240px;
  overflow-wrap: anywhere;
  white-space: pre-wrap;
}

.empty-message {
  padding: 22px 16px;
  margin: 0;
  font-size: 12px;
  color: #6b7280;
  text-align: center;
}
</style>
