<script setup lang="ts">
import type { LineUpGraphCatalogLookup } from '@/api/modules/LineUp/catalog'
import type { LineUpMonitorBindingSummary, LineUpMonitorEvent } from '@/api/modules/LineUp/monitor'
import type { EntityId } from '@/api/modules/LineUp/types'
import { ElMessage } from 'element-plus'
import { computed, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import lineUpGraphCatalogApi from '@/api/modules/LineUp/catalog'
import historyApi from '@/api/modules/LineUp/history'
import lineUpMonitorApi from '@/api/modules/LineUp/monitor'
import { useI18n } from 'vue-i18n'
import messages from './history.lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}

const route = useRoute()
const router = useRouter()
const lookups = ref<LineUpGraphCatalogLookup>({ projects: [], lines: [], processes: [], stations: [], subStations: [], devices: [] })
const bindings = ref<LineUpMonitorBindingSummary[]>([])
const events = ref<LineUpMonitorEvent[]>([])
const lineId = ref<EntityId>(String(route.query.lineId ?? ''))
const deviceId = ref<EntityId>(String(route.query.deviceId ?? ''))
const stationId = ref<EntityId>(String(route.query.stationId ?? ''))
const subStationId = ref<EntityId>(String(route.query.subStationId ?? ''))
const bindingId = ref<EntityId>(String(route.query.bindingId ?? ''))
const sequenceSourceKey = ref(String(route.query.sequenceSourceKey ?? ''))
const eventType = ref('')
const ownerKeyword = ref('')
const nextCursor = ref('')
const hasMore = ref(false)
const loading = ref(false)

const filteredEvents = computed(() => events.value.filter(item =>
  (!eventType.value || item.eventType === eventType.value)
  && (!ownerKeyword.value || item.ownerSourceKey.toLowerCase().includes(ownerKeyword.value.toLowerCase())),
))
const eventTypes = computed(() => [...new Set(events.value.map(item => item.eventType))].sort())
const availableDevices = computed(() => lookups.value.devices.filter(item => item.lineId === lineId.value))
const availableStations = computed(() => lookups.value.stations.filter(item => item.lineId === lineId.value))
const availableSubStations = computed(() => lookups.value.subStations.filter(item => (
  item.lineId === lineId.value && item.stationId === stationId.value
)))
const sequenceOptions = computed(() => bindings.value.flatMap(binding => binding.sequences.map(sequence => ({
  bindingId: binding.bindingId,
  sequenceSourceKey: sequence.sequenceSourceKey,
  label: `${binding.graphSourceName} · ${sequence.sequenceDisplayName}`,
  value: `${binding.bindingId}\n${sequence.sequenceSourceKey}`,
}))))
const selectedSequence = computed({
  get: () => bindingId.value && sequenceSourceKey.value ? `${bindingId.value}\n${sequenceSourceKey.value}` : '',
  set: (value: string) => {
    const separator = value.indexOf('\n')
    bindingId.value = separator >= 0 ? value.slice(0, separator) : ''
    sequenceSourceKey.value = separator >= 0 ? value.slice(separator + 1) : ''
  },
})

async function loadBindings() {
  bindings.value = subStationId.value
    ? await lineUpMonitorApi.getBindingsBySubStation(subStationId.value)
    : lineId.value && deviceId.value && stationId.value
      ? await lineUpMonitorApi.getBindings(lineId.value, deviceId.value, stationId.value)
      : []
  if (!sequenceOptions.value.some(item => item.bindingId === bindingId.value && item.sequenceSourceKey === sequenceSourceKey.value)) {
    bindingId.value = sequenceOptions.value[0]?.bindingId ?? ''
    sequenceSourceKey.value = sequenceOptions.value[0]?.sequenceSourceKey ?? ''
  }
  await loadEvents(true)
}

async function initialize() {
  loading.value = true
  try {
    lookups.value = await lineUpGraphCatalogApi.getLookups({})
    lineId.value = lookups.value.lines.some(item => item.id === lineId.value) ? lineId.value : lookups.value.lines[0]?.id ?? ''
    deviceId.value = availableDevices.value.some(item => item.id === deviceId.value) ? deviceId.value : availableDevices.value[0]?.id ?? ''
    stationId.value = availableStations.value.some(item => item.id === stationId.value) ? stationId.value : availableStations.value[0]?.id ?? ''
    subStationId.value = availableSubStations.value.some(item => item.id === subStationId.value)
      ? subStationId.value
      : availableSubStations.value[0]?.id ?? ''
    await loadBindings()
  }
  catch {
    ElMessage.error(t('lineupHistory_form.errLookupsLoadFailed'))
  }
  finally {
    loading.value = false
  }
}

async function loadEvents(reset = false) {
  if (!bindingId.value || !sequenceSourceKey.value) {
    events.value = []
    return
  }
  loading.value = true
  try {
    const page = await historyApi.getEvents(bindingId.value, sequenceSourceKey.value, reset ? undefined : nextCursor.value || undefined, 100)
    events.value = reset ? page.items : [...events.value, ...page.items]
    nextCursor.value = page.nextBeforeEventId
    hasMore.value = page.hasMore
  }
  catch {
    ElMessage.error(t('lineupHistory_form.errEventsLoadFailed'))
  }
  finally {
    loading.value = false
  }
}

async function handleDeviceChange(value: EntityId) {
  deviceId.value = value
  await loadBindings()
}

async function handleLineChange(value: EntityId) {
  lineId.value = value
  deviceId.value = availableDevices.value[0]?.id ?? ''
  stationId.value = availableStations.value[0]?.id ?? ''
  subStationId.value = availableSubStations.value[0]?.id ?? ''
  await loadBindings()
}

async function handleStationChange(value: EntityId) {
  stationId.value = value
  subStationId.value = availableSubStations.value[0]?.id ?? ''
  await loadBindings()
}

async function handleSubStationChange(value: EntityId) {
  subStationId.value = value
  await loadBindings()
}

onMounted(() => void initialize())
</script>

<template>
  <main v-loading="loading" class="lineup-history">
    <header>
      <div><span>LINEUP / EVENT HISTORY</span><h2>{{ $t('lineupHistory_form.pageTitle') }}</h2><p>{{ $t('lineupHistory_form.pageIntro') }}</p></div>
      <el-button @click="router.push({ path: '/lineup/monitor', query: { subStationId, bindingId, sequenceSourceKey } })">
        {{ $t('lineupHistory_form.backToMonitor') }}
      </el-button>
    </header>
    <section class="history-filters">
      <label><span>{{ $t('lineupHistory_form.fieldLine') }}</span><el-select :model-value="lineId" filterable @change="handleLineChange"><el-option v-for="line in lookups.lines" :key="line.id" :value="line.id" :label="`${line.code || '—'} · ${line.name || $t('lineupHistory_form.unnamedLine')}`" /></el-select></label>
      <label><span>{{ $t('lineupHistory_form.fieldPlcDevice') }}</span><el-select :model-value="deviceId" filterable @change="handleDeviceChange"><el-option v-for="device in availableDevices" :key="device.id" :value="device.id" :label="device.name || $t('lineupHistory_form.unnamedDevice')" /></el-select></label>
      <label><span>{{ $t('lineupHistory_form.fieldStation') }}</span><el-select :model-value="stationId" filterable @change="handleStationChange"><el-option v-for="station in availableStations" :key="station.id" :value="station.id" :label="`${station.code || '—'} · ${station.name || $t('lineupHistory_form.unnamedStation')}`" /></el-select></label>
      <label><span>{{ $t('lineupHistory_form.fieldSubStation') }}</span><el-select :model-value="subStationId" filterable @change="handleSubStationChange"><el-option v-for="subStation in availableSubStations" :key="subStation.id" :value="subStation.id" :label="`${subStation.code || '—'} · ${subStation.name || $t('lineupHistory_form.unnamedSubStation')}`" /></el-select></label>
      <label><span>GRAPH / Sequence</span><el-select v-model="selectedSequence" filterable @change="loadEvents(true)"><el-option v-for="item in sequenceOptions" :key="item.value" :value="item.value" :label="item.label" /></el-select></label>
      <label><span>{{ $t('lineupHistory_form.fieldEventType') }}</span><el-select v-model="eventType" clearable><el-option v-for="item in eventTypes" :key="item" :value="item" :label="item" /></el-select></label>
      <label><span>{{ $t('lineupHistory_form.fieldOwner') }}</span><el-input v-model="ownerKeyword" clearable placeholder="SourceKey" /></label>
    </section>
    <section class="history-list">
      <article v-for="item in filteredEvents" :key="item.id">
        <div class="event-time">
          <time>{{ new Date(item.occurredAtUtc).toLocaleString() }}</time><small>#{{ item.gatewaySequence }}.{{ item.eventOrdinal }}</small>
        </div>
        <div><strong>{{ item.eventType }}</strong><span>{{ item.ownerSourceKey || $t('lineupHistory_form.runtimeConnection') }}</span></div>
        <div><span>{{ item.previousState || '—' }}</span><b>→</b><span>{{ item.currentState || '—' }}</span></div>
        <el-tag size="small" :type="item.quality === 'Good' ? 'success' : item.quality ? 'danger' : 'info'">
          {{ item.quality || 'Event' }}
        </el-tag>
      </article>
      <el-empty v-if="!filteredEvents.length" :description="$t('lineupHistory_form.emptyNoEvents')" />
      <el-button v-if="hasMore" :loading="loading" @click="loadEvents(false)">
        {{ $t('lineupHistory_form.loadEarlier') }}
      </el-button>
    </section>
  </main>
</template>

<style scoped>
.lineup-history {
  display: grid;
  gap: 14px;
  min-width: 0;
  padding: 16px;
  background: var(--el-fill-color-lighter);
}

.lineup-history > header,
.history-filters,
.history-list {
  background: var(--el-bg-color);
  border: 1px solid var(--el-border-color-light);
  border-radius: 8px;
}

.lineup-history > header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 16px 18px;
}

.lineup-history header span {
  font-size: 11px;
  font-weight: 700;
  color: var(--el-color-primary);
  letter-spacing: 0.08em;
}

.lineup-history h2 {
  margin: 4px 0;
}

.lineup-history p {
  margin: 0;
  font-size: 13px;
  color: var(--el-text-color-secondary);
}

.history-filters {
  display: grid;
  grid-template-columns: repeat(5, minmax(150px, 1fr));
  gap: 12px;
  padding: 14px;
}

.history-filters label {
  display: grid;
  gap: 5px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.history-list {
  display: grid;
  gap: 0;
  padding: 8px 16px 16px;
}

.history-list article {
  display: grid;
  grid-template-columns: 170px minmax(180px, 1fr) minmax(180px, 1fr) auto;
  gap: 16px;
  align-items: center;
  padding: 12px 4px;
  border-bottom: 1px solid var(--el-border-color-lighter);
}

.history-list article > div {
  display: grid;
  gap: 3px;
  min-width: 0;
}

.history-list article span {
  overflow-wrap: anywhere;
}

.event-time time {
  font-size: 12px;
}

.event-time small {
  color: var(--el-text-color-secondary);
}

@media (width <= 1100px) {
  .history-filters {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .history-list article {
    grid-template-columns: 140px minmax(0, 1fr);
  }
}

@media (width <= 640px) {
  .history-filters {
    grid-template-columns: 1fr;
  }
}
</style>
