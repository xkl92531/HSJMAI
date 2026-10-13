<script setup lang="ts">
import type { LineUpBusinessItem, LineUpBusinessKind, LineUpBusinessWrite } from '@/api/modules/LineUp/businessConfig'
import type { LineUpMasterItem, LineUpMasterLevel } from '@/api/modules/LineUp/masterData'
import type { EntityId } from '@/api/modules/LineUp/types'
import { ElButton, ElMessage, ElMessageBox, ElTag } from 'element-plus'
import { h, onMounted, reactive, ref } from 'vue'
import masterApi from '@/api/modules/LineUp/masterData'
import { createLineUpBusinessConfigState } from './useLineUpBusinessConfig'
import { useI18n } from 'vue-i18n'
import messages from './components/lang'

const state = createLineUpBusinessConfigState()

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}
const hierarchy = reactive<Record<LineUpMasterLevel, LineUpMasterItem[]>>({ ProductionLine: [], Process: [], WorkStation: [], SubStation: [] })
const selected = reactive<Record<LineUpMasterLevel, EntityId>>({ ProductionLine: '', Process: '', WorkStation: '', SubStation: '' })
const activePanels = ref<LineUpBusinessKind[]>(['Action', 'Condition', 'Solution'])
const dialogVisible = ref(false)
const editingKind = ref<LineUpBusinessKind>('Action')
const editingId = ref('')
const form = reactive<LineUpBusinessWrite>({ parentId: '', subStationId: '', code: '', name: '', value: '', isEnabled: true })

const kinds = computed<Array<{ key: LineUpBusinessKind, title: string, hint: string }>>(() => [
  { key: 'Action', title: t('lineupBusinessConfig_form.kindAction'), hint: t('lineupBusinessConfig_form.hintAction') },
  { key: 'Condition', title: t('lineupBusinessConfig_form.kindCondition'), hint: t('lineupBusinessConfig_form.hintCondition') },
  { key: 'Solution', title: t('lineupBusinessConfig_form.kindSolution'), hint: t('lineupBusinessConfig_form.hintSolution') },
])

async function loadHierarchy(level: LineUpMasterLevel, parentId?: EntityId) {
  hierarchy[level] = (await masterApi.query(level, parentId, { page: 1, pageSize: 200 })).items
}

function clearAfter(level: LineUpMasterLevel) {
  const order: LineUpMasterLevel[] = ['ProductionLine', 'Process', 'WorkStation', 'SubStation']
  for (const key of order.slice(order.indexOf(level) + 1)) {
    selected[key] = ''
    hierarchy[key] = []
  }
}

async function selectHierarchy(level: LineUpMasterLevel, id: EntityId) {
  selected[level] = id
  clearAfter(level)
  if (level === 'ProductionLine') {
    await loadHierarchy('Process', id)
  }
  if (level === 'Process') {
    await loadHierarchy('WorkStation', id)
  }
  if (level === 'WorkStation') {
    await loadHierarchy('SubStation', id)
  }
  if (level === 'SubStation') {
    state.selectSubStation(id)
    await state.load('Action')
  }
}

async function selectAction(item: LineUpBusinessItem) {
  state.selectAction(item.id)
  await Promise.all([state.load('Condition'), state.load('Solution')])
}

function canAdd(kind: LineUpBusinessKind) {
  return kind === 'Action' ? Boolean(selected.SubStation) : Boolean(state.selectedActionId.value)
}

function openEditor(kind: LineUpBusinessKind, item?: LineUpBusinessItem) {
  editingKind.value = kind
  editingId.value = item?.id || ''
  form.parentId = kind === 'Action' ? selected.SubStation : state.selectedActionId.value
  form.subStationId = selected.SubStation
  form.code = item?.code || ''
  form.name = item?.name || ''
  form.value = item?.value || ''
  form.isEnabled = item?.isEnabled ?? true
  dialogVisible.value = true
}

async function save() {
  try {
    await state.save(editingKind.value, { ...form, id: editingId.value || undefined })
    dialogVisible.value = false
    ElMessage.success(t('common.saveSuccess'))
  }
  catch {
    ElMessage.error(t('lineupBusinessConfig_form.saveFailed'))
  }
}

async function remove(kind: LineUpBusinessKind, item: LineUpBusinessItem) {
  await ElMessageBox.confirm(t('lineupBusinessConfig_form.confirmDelete', { name: item.name || item.code }), t('lineupBusinessConfig_form.deleteConfirmTitle'), { type: 'warning' })
  try {
    await state.remove(kind, item.id)
    ElMessage.success(t('lineupBusinessConfig_form.deletedTip'))
  }
  catch {
    ElMessage.error(t('lineupBusinessConfig_form.deleteFailed'))
  }
}

function columns(kind: LineUpBusinessKind) {
  return [
    { key: 'code', dataKey: 'code', title: kind === 'Condition' ? t('lineupBusinessConfig_form.condCodeCol') : t('lineupBusinessConfig_form.codeCol'), width: 180 },
    { key: 'name', dataKey: 'name', title: t('lineupBusinessConfig_form.nameDescCol'), width: 260 },
    { key: 'value', dataKey: 'value', title: kind === 'Condition' ? t('lineupBusinessConfig_form.condValueCol') : t('lineupBusinessConfig_form.extraCol'), width: 260 },
    {
      key: 'status',
      title: t('lineupBusinessConfig_form.status'),
      width: 90,
      cellRenderer: ({ rowData }: { rowData: LineUpBusinessItem }) => h(ElTag, { type: rowData.isEnabled ? 'success' : 'info', effect: 'plain' }, () => rowData.isEnabled ? t('lineupBusinessConfig_form.enable') : t('lineupBusinessConfig_form.disable')),
    },
    {
      key: 'actions',
      title: t('common.operate'),
      width: 220,
      cellRenderer: ({ rowData }: { rowData: LineUpBusinessItem }) => h('div', { class: 'row-actions' }, [
        kind === 'Action' ? h(ElButton, { link: true, type: 'primary', onClick: () => selectAction(rowData) }, () => t('lineupBusinessConfig_form.viewConditions')) : null,
        h(ElButton, { link: true, onClick: () => openEditor(kind, rowData) }, () => t('common.edit')),
        h(ElButton, { link: true, type: 'danger', onClick: () => remove(kind, rowData) }, () => t('common.delete')),
      ]),
    },
  ]
}

onMounted(async () => {
  try {
    await loadHierarchy('ProductionLine')
  }
  catch {
    ElMessage.error(t('lineupBusinessConfig_form.hierarchyLoadFailed'))
  }
})
</script>

<template>
  <main class="business-page">
    <header class="page-header">
      <div>
        <span>LINEUP / BUSINESS CONFIG</span>
        <h2>{{ $t('lineupBusinessConfig_form.pageTitle') }}</h2>
        <p>{{ $t('lineupBusinessConfig_form.pageIntro') }}</p>
      </div>
      <ElTag type="success" effect="plain">
        {{ $t('lineupBusinessConfig_form.liveDbTag') }}
      </ElTag>
    </header>

    <section class="hierarchy-selector">
      <label v-for="level in (['ProductionLine', 'Process', 'WorkStation', 'SubStation'] as LineUpMasterLevel[])" :key="level">
        <span>{{ { ProductionLine: $t('lineupBusinessConfig_form.line'), Process: $t('lineupBusinessConfig_form.process'), WorkStation: $t('lineupBusinessConfig_form.workStation'), SubStation: $t('lineupBusinessConfig_form.subStation') }[level] }}</span>
        <el-select :model-value="selected[level]" filterable :placeholder="$t('common.pleaseSelect')" @change="selectHierarchy(level, $event)">
          <el-option v-for="item in hierarchy[level]" :key="item.id" :label="`${item.code || '—'} · ${item.name || '—'}`" :value="item.id" />
        </el-select>
      </label>
    </section>

    <el-alert
      v-if="!selected.SubStation"
      :title="$t('lineupBusinessConfig_form.selectFirstAlert')"
      type="info"
      :closable="false"
      show-icon
    />

    <el-collapse v-model="activePanels" class="config-panels">
      <el-collapse-item v-for="kind in kinds" :key="kind.key" :name="kind.key">
        <template #title>
          <div class="panel-title">
            <strong>{{ kind.title }}</strong>
            <span>{{ kind.hint }}</span>
            <ElTag effect="plain">
              {{ state.records.value[kind.key].length }}
            </ElTag>
          </div>
        </template>
        <section class="record-panel">
          <div class="panel-actions">
            <ElButton type="primary" :disabled="!canAdd(kind.key)" @click="openEditor(kind.key)">
              {{ $t('lineupBusinessConfig_form.addKind', { kind: kind.title }) }}
            </ElButton>
            <ElButton :disabled="!canAdd(kind.key)" :loading="state.loading.value" @click="state.load(kind.key)">
              {{ $t('lineupBusinessConfig_form.refresh') }}
            </ElButton>
          </div>
          <ElTableV2
            v-if="state.records.value[kind.key].length"
            :columns="columns(kind.key)"
            :data="state.records.value[kind.key]"
            :width="1040"
            :height="260"
            fixed
          />
          <el-empty v-else :image-size="64" :description="$t('lineupBusinessConfig_form.noRecords')" />
        </section>
      </el-collapse-item>
    </el-collapse>

    <el-dialog v-model="dialogVisible" :title="$t(editingId ? 'lineupBusinessConfig_form.editKind' : 'lineupBusinessConfig_form.addKind', { kind: kinds.find(item => item.key === editingKind)?.title })" width="560px">
      <el-form label-position="top">
        <el-form-item :label="editingKind === 'Condition' ? $t('lineupBusinessConfig_form.condCodeCol') : $t('lineupBusinessConfig_form.codeCol')">
          <el-input v-model="form.code" maxlength="200" />
        </el-form-item>
        <el-form-item :label="$t('lineupBusinessConfig_form.nameDescCol')">
          <el-input v-model="form.name" maxlength="500" />
        </el-form-item>
        <el-form-item :label="editingKind === 'Condition' ? $t('lineupBusinessConfig_form.condValueCol') : $t('lineupBusinessConfig_form.extraCol')">
          <el-input v-model="form.value" type="textarea" :rows="3" maxlength="4000" />
        </el-form-item>
        <el-form-item :label="$t('lineupBusinessConfig_form.status')">
          <el-switch v-model="form.isEnabled" :active-text="$t('lineupBusinessConfig_form.enable')" :inactive-text="$t('lineupBusinessConfig_form.disable')" />
        </el-form-item>
      </el-form>
      <template #footer>
        <ElButton @click="dialogVisible = false">
          {{ $t('common.cancel') }}
        </ElButton>
        <ElButton type="primary" @click="save">
          {{ $t('common.save') }}
        </ElButton>
      </template>
    </el-dialog>
  </main>
</template>

<style scoped>
.business-page {
  display: grid;
  gap: 14px;
  min-width: 0;
  padding: 16px;
  background: var(--el-fill-color-lighter);
}

.page-header,
.hierarchy-selector,
.config-panels {
  background: var(--el-bg-color);
  border: 1px solid var(--el-border-color-light);
  border-radius: 8px;
}

.page-header {
  display: flex;
  gap: 20px;
  align-items: center;
  justify-content: space-between;
  padding: 18px;
}

.page-header span {
  font-size: 11px;
  font-weight: 700;
  color: var(--el-color-primary);
  letter-spacing: 0.1em;
}

.page-header h2 {
  margin: 4px 0;
}

.page-header p {
  margin: 0;
  color: var(--el-text-color-secondary);
}

.hierarchy-selector {
  display: grid;
  grid-template-columns: repeat(4, minmax(180px, 1fr));
  gap: 12px;
  padding: 14px 16px;
}

.hierarchy-selector label {
  display: grid;
  gap: 6px;
  min-width: 0;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.config-panels {
  padding: 0 16px;
}

.panel-title {
  display: grid;
  grid-template-columns: 150px minmax(0, 1fr) auto;
  gap: 12px;
  align-items: center;
  width: 100%;
  padding-right: 14px;
}

.panel-title span {
  overflow: hidden;
  text-overflow: ellipsis;
  color: var(--el-text-color-secondary);
  white-space: nowrap;
}

.record-panel {
  min-width: 0;
  padding: 4px 0 16px;
  overflow-x: auto;
}

.panel-actions {
  display: flex;
  gap: 8px;
  margin-bottom: 10px;
}

:deep(.row-actions) {
  display: flex;
  align-items: center;
}

@media (width <= 1000px) {
  .hierarchy-selector {
    grid-template-columns: repeat(2, minmax(180px, 1fr));
  }
}

@media (width <= 620px) {
  .hierarchy-selector {
    grid-template-columns: 1fr;
  }
}
</style>
