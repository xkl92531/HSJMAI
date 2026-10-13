<script setup lang="ts">
import type { LineUpDeviceItem } from '@/api/modules/LineUp/deviceData'
import type { LineUpMasterItem, LineUpMasterLevel } from '@/api/modules/LineUp/masterData'
import type { EntityId } from '@/api/modules/LineUp/types'
import { ElMessage, ElMessageBox } from 'element-plus'
import { computed, onMounted, reactive, ref } from 'vue'
import deviceApi from '@/api/modules/LineUp/deviceData'
import masterApi from '@/api/modules/LineUp/masterData'
import { useI18n } from 'vue-i18n'
import messages from './components/lang'

defineOptions({
  name: 'LineUpMasterData',
})

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}

const levelOrder: LineUpMasterLevel[] = ['ProductionLine', 'Process', 'WorkStation', 'SubStation']
const levelLabels = computed<Record<LineUpMasterLevel, string>>(() => ({
  ProductionLine: t('lineupMaster_form.line'),
  Process: t('lineupMaster_form.process'),
  WorkStation: t('lineupMaster_form.workStation'),
  SubStation: t('lineupMaster_form.subStation'),
}))

// ---------- 层级维护（产线/工序/工站/子工站） ----------
const hierarchy = reactive<Record<LineUpMasterLevel, LineUpMasterItem[]>>({
  ProductionLine: [],
  Process: [],
  WorkStation: [],
  SubStation: [],
})
const selected = reactive<Record<LineUpMasterLevel, EntityId>>({
  ProductionLine: '',
  Process: '',
  WorkStation: '',
  SubStation: '',
})
const table = reactive({ loading: false, items: [] as LineUpMasterItem[] })

// 当前表格展示的层级：已选择的最深层级的下一级（未选 = 产线列表）
const currentLevel = computed<LineUpMasterLevel>(() => {
  for (let i = levelOrder.length - 1; i >= 0; i--) {
    if (selected[levelOrder[i]]) {
      return i === levelOrder.length - 1 ? 'SubStation' : levelOrder[i + 1]
    }
  }
  return 'ProductionLine'
})
const currentParentId = computed<EntityId | undefined>(() => {
  const idx = levelOrder.indexOf(currentLevel.value)
  return idx > 0 ? selected[levelOrder[idx - 1]] || undefined : undefined
})
const currentLevelLabel = computed(() => levelLabels.value[currentLevel.value])

// 子工站是最深层：选中它表格层级不变（仍是子工站列表），需要按选中项过滤定位，
// 否则下拉选择后页面无任何可见反馈
const displayTableItems = computed(() => {
  if (currentLevel.value === 'SubStation' && selected.SubStation) {
    return table.items.filter(x => x.id === selected.SubStation)
  }
  return table.items
})

async function loadLevelOptions(level: LineUpMasterLevel, parentId?: EntityId) {
  hierarchy[level] = (await masterApi.query(level, parentId, { page: 1, pageSize: 200 })).items
}

async function loadTable() {
  table.loading = true
  try {
    const page = await masterApi.query(currentLevel.value, currentParentId.value, { page: 1, pageSize: 200 })
    table.items = page.items
  }
  catch {
    ElMessage.error(t('lineupMaster_form.loadListFailed'))
  }
  finally {
    table.loading = false
  }
}

async function selectHierarchy(level: LineUpMasterLevel, id: EntityId) {
  selected[level] = id
  const order = levelOrder
  for (const key of order.slice(order.indexOf(level) + 1)) {
    selected[key] = ''
    hierarchy[key] = []
  }
  const next = order[order.indexOf(level) + 1]
  if (next && id) {
    await loadLevelOptions(next, id)
  }
  await loadTable()
}

async function onDrill(row: LineUpMasterItem) {
  await selectHierarchy(currentLevel.value, row.id)
}

async function clearLevel(level: LineUpMasterLevel) {
  await selectHierarchy(level, '')
}

function selectionChain() {
  return levelOrder
    .filter(level => selected[level])
    .map(level => ({
      level,
      item: hierarchy[level].find(x => x.id === selected[level]),
    }))
}

// ---------- 层级新增/编辑弹窗 ----------
const dialog = reactive({
  visible: false,
  id: '' as EntityId,
  form: { code: '', name: '', isEnabled: true, remark: '', plcId: '' as EntityId },
})

function openCreate() {
  dialog.id = ''
  dialog.form = { code: '', name: '', isEnabled: true, remark: '', plcId: '' }
  dialog.visible = true
  // 子工站弹框需要设备下拉：打开时兜底刷新，避免页面加载窗口期请求失败导致下拉空
  if (currentLevel.value === 'SubStation') {
    loadAllDevices()
  }
}

function openEdit(row: LineUpMasterItem) {
  dialog.id = row.id
  dialog.form = {
    code: row.code || '',
    name: row.name || '',
    isEnabled: row.isEnabled ?? true,
    remark: row.remark || '',
    plcId: row.plcId ? String(row.plcId) : '',
  }
  dialog.visible = true
  if (currentLevel.value === 'SubStation') {
    loadAllDevices()
  }
}

async function saveHierarchy() {
  if (!dialog.form.code.trim() || !dialog.form.name.trim()) {
    ElMessage.warning(t('lineupMaster_form.codeNameRequired'))
    return
  }
  const level = currentLevel.value
  const lineId = selected.ProductionLine || undefined
  const request = {
    parentId: level === 'ProductionLine' ? undefined : currentParentId.value,
    lineId: level === 'ProductionLine' ? undefined : lineId,
    code: dialog.form.code.trim(),
    name: dialog.form.name.trim(),
    isEnabled: level === 'SubStation' ? true : dialog.form.isEnabled,
    plcId: level === 'SubStation' && dialog.form.plcId ? dialog.form.plcId : undefined,
    remark: dialog.form.remark.trim() || undefined,
  }
  try {
    if (dialog.id) {
      await masterApi.update(level, dialog.id, request)
    }
    else {
      await masterApi.create(level, request)
    }
    dialog.visible = false
    ElMessage.success(t('common.saveSuccess'))
    await refreshAfterSave(level)
  }
  catch (e) {
    ElMessage.error(saveFailureMessage(e))
  }
}

function saveFailureMessage(e: unknown): string {
  const code = businessCode(e)
  if (code === 'LineUpDuplicateCode') {
    return t('lineupMaster_form.saveFailedDuplicate')
  }
  if (code === 'LineUpParentMismatch' || code === 'LineUpParentRequired') {
    return t('lineupMaster_form.saveFailedParent')
  }
  if (code === 'LineUpRecordNotFound') {
    return t('lineupMaster_form.saveFailedNotFound')
  }
  return t('lineupMaster_form.saveFailedRetry')
}

async function refreshAfterSave(level: LineUpMasterLevel) {
  await loadTable()
  // 刷新层级下拉选项（当前层及父层都可能变化）
  const idx = levelOrder.indexOf(level)
  await loadLevelOptions(level, idx > 0 ? selected[levelOrder[idx - 1]] : undefined)
}

async function removeHierarchy(row: LineUpMasterItem) {
  const name = row.name || row.code
  // 级联软删：删除任意层级会连带其全部下级与动作业务配置
  const tip = currentLevel.value === 'SubStation'
    ? t('lineupMaster_form.confirmDeleteSubStation', { name })
    : t('lineupMaster_form.confirmDeleteLevel', { level: currentLevelLabel.value, child: childLevelLabelOf(currentLevel.value), name })
  try {
    await ElMessageBox.confirm(tip, t('lineupMaster_form.cascadeDeleteTitle'), { type: 'warning', confirmButtonText: t('lineupMaster_form.confirmCascadeDelete') })
  }
  catch {
    return
  }
  try {
    await masterApi.remove(currentLevel.value, row.id)
    ElMessage.success(t('lineupMaster_form.deletedWithChildren'))
    await refreshAfterSave(currentLevel.value)
  }
  catch (e) {
    ElMessage.error(deleteFailureMessage(e, childLevelLabelOf(currentLevel.value)))
  }
}

// 后端业务错误码（LineUpControllerResults 返回 body.errorMessage = 错误码）
function businessCode(e: unknown): string | undefined {
  const err = e as { response?: { data?: { errorMessage?: string } } }
  return err?.response?.data?.errorMessage
}

function deleteFailureMessage(e: unknown, refLabel: string): string {
  const code = businessCode(e)
  if (code === 'LineUpMasterHasChildren') {
    return t('lineupMaster_form.deleteFailedReferenced', { ref: refLabel })
  }
  if (code === 'LineUpRecordNotFound') {
    return t('lineupMaster_form.deleteFailedNotFound')
  }
  return t('lineupMaster_form.deleteFailedRetry')
}

function childLevelLabelOf(level: LineUpMasterLevel): string {
  switch (level) {
    case 'ProductionLine': return t('lineupMaster_form.process')
    case 'Process': return t('lineupMaster_form.workStation')
    case 'WorkStation': return t('lineupMaster_form.subStation')
    case 'SubStation': return t('lineupMaster_form.relatedActions')
  }
}

// ---------- PLC 设备维护 ----------
const device = reactive({
  loading: false,
  items: [] as LineUpDeviceItem[],
  filterLineId: '' as EntityId,
  keyword: '',
})
const deviceDialog = reactive({
  visible: false,
  id: '' as EntityId,
  form: { code: '', name: '', ipAddress: '', lineId: '' as EntityId, remark: '' },
})

const lineOptions = computed(() => hierarchy.ProductionLine)
const allDevices = ref<LineUpDeviceItem[]>([])

// 子工站弹框的 PLC 设备选项：按当前选中产线过滤（与「PLC 设备」页签口径一致，避免跨产线误绑）；
// 历史数据若已绑定其它产线的设备，把已绑定项追加进选项避免只显示裸 id
const subStationDeviceOptions = computed<LineUpDeviceItem[]>(() => {
  const lineId = selected.ProductionLine
  const list = allDevices.value.filter(dev => dev.lineId && dev.lineId === lineId)
  const boundId = dialog.form.plcId
  if (boundId && !list.some(dev => dev.id === boundId)) {
    const bound = allDevices.value.find(dev => dev.id === boundId)
    if (bound) {
      return [...list, bound]
    }
  }
  return list
})

async function loadDevices() {
  device.loading = true
  try {
    const page = await deviceApi.query({
      lineId: device.filterLineId || undefined,
      keyword: device.keyword.trim() || undefined,
      page: 1,
      pageSize: 200,
    })
    device.items = page.items
  }
  catch {
    ElMessage.error(t('lineupMaster_form.deviceListLoadFailed'))
  }
  finally {
    device.loading = false
  }
}

// 切到 PLC 设备页签：默认查询；产线筛选未手动选过时跟随层级当前选中的产线
const activeTab = ref('hierarchy')
async function onTabChange(name: string | number) {
  if (name !== 'device') {
    return
  }
  if (!device.filterLineId && selected.ProductionLine) {
    device.filterLineId = selected.ProductionLine
  }
  await loadDevices()
}

async function loadAllDevices() {
  try {
    allDevices.value = (await deviceApi.query({ page: 1, pageSize: 200 })).items
  }
  catch {
    allDevices.value = []
  }
}

function openDeviceCreate() {
  deviceDialog.id = ''
  deviceDialog.form = { code: '', name: '', ipAddress: '', lineId: '', remark: '' }
  deviceDialog.visible = true
}

function openDeviceEdit(row: LineUpDeviceItem) {
  deviceDialog.id = row.id
  deviceDialog.form = {
    code: row.code || '',
    name: row.name || '',
    ipAddress: row.ipAddress || '',
    lineId: row.lineId || '',
    remark: row.remark || '',
  }
  deviceDialog.visible = true
}

async function saveDevice() {
  if (!deviceDialog.form.code.trim()) {
    ElMessage.warning(t('lineupMaster_form.deviceNameRequired'))
    return
  }
  const request = {
    code: deviceDialog.form.code.trim(),
    name: deviceDialog.form.name.trim() || undefined,
    ipAddress: deviceDialog.form.ipAddress.trim() || undefined,
    lineId: deviceDialog.form.lineId || undefined,
    remark: deviceDialog.form.remark.trim() || undefined,
  }
  try {
    if (deviceDialog.id) {
      await deviceApi.update(deviceDialog.id, request)
    }
    else {
      await deviceApi.create(request)
    }
    deviceDialog.visible = false
    ElMessage.success(t('common.saveSuccess'))
    await Promise.all([loadDevices(), loadAllDevices()])
  }
  catch (e) {
    const code = businessCode(e)
    if (code === 'LineUpDuplicateCode') {
      ElMessage.error(t('lineupMaster_form.saveFailedDeviceDuplicate'))
    }
    else {
      ElMessage.error(saveFailureMessage(e))
    }
  }
}

async function removeDevice(row: LineUpDeviceItem) {
  try {
    await ElMessageBox.confirm(t('lineupMaster_form.confirmDeleteDevice', { name: row.code }), t('lineupMaster_form.deleteConfirmTitle'), { type: 'warning' })
  }
  catch {
    return
  }
  try {
    await deviceApi.remove(row.id)
    ElMessage.success(t('lineupMaster_form.deletedTip'))
    await Promise.all([loadDevices(), loadAllDevices()])
  }
  catch (e) {
    ElMessage.error(deleteFailureMessage(e, t('lineupMaster_form.refSubStationOrProject')))
  }
}

function lineNameOf(lineId: EntityId | null | undefined) {
  if (!lineId) {
    return '—'
  }
  const line = hierarchy.ProductionLine.find(x => x.id === lineId)
  return line ? (line.name || line.code || '—') : '—'
}

onMounted(async () => {
  await Promise.all([loadLevelOptions('ProductionLine'), loadTable(), loadAllDevices()])
})
</script>

<template>
  <main class="master-page">
    <header class="page-header">
      <div>
        <span>BASIC MASTER DATA</span>
        <h2>{{ $t('lineupMaster_form.pageTitle') }}</h2>
        <p>{{ $t('lineupMaster_form.pageIntro') }}</p>
      </div>
    </header>

    <el-tabs v-model="activeTab" class="master-tabs" type="border-card" @tab-change="onTabChange">
      <el-tab-pane :label="$t('lineupMaster_form.hierarchyTab')" name="hierarchy">
        <section class="hierarchy-selector">
          <label v-for="level in levelOrder" :key="level">
            <span>{{ levelLabels[level] }}</span>
            <el-select
              :model-value="selected[level]"
              filterable
              clearable
              :placeholder="$t('lineupMaster_form.all')"
              @change="selectHierarchy(level, $event || '')"
            >
              <el-option
                v-for="item in hierarchy[level]"
                :key="item.id"
                :label="`${item.code || '—'} · ${item.name || '—'}`"
                :value="item.id"
              />
            </el-select>
          </label>
        </section>

        <div class="table-toolbar">
          <div class="crumb">
            <template v-for="node in selectionChain()" :key="node.level">
              <el-tag closable @close="clearLevel(node.level)">
                {{ levelLabels[node.level] }}：{{ node.item?.name || node.item?.code || selected[node.level] }}
              </el-tag>
            </template>
            <span v-if="!selectionChain().length" class="crumb-hint">{{ $t('lineupMaster_form.crumbHint') }}</span>
          </div>
          <div class="toolbar-actions">
            <ElButton :loading="table.loading" @click="loadTable">
              {{ $t('lineupMaster_form.refresh') }}
            </ElButton>
            <ElButton type="primary" @click="openCreate">
              {{ $t('lineupMaster_form.createLevel', { level: currentLevelLabel }) }}
            </ElButton>
          </div>
        </div>

        <ElTable v-loading="table.loading" :data="displayTableItems" stripe border>
          <ElTableColumn prop="code" :label="currentLevel === 'SubStation' ? $t('lineupMaster_form.subStationCode') : $t('lineupMaster_form.code')" min-width="140" show-overflow-tooltip />
          <ElTableColumn prop="name" :label="currentLevel === 'SubStation' ? $t('lineupMaster_form.subStationName') : $t('lineupMaster_form.name')" min-width="160" show-overflow-tooltip />
          <ElTableColumn v-if="currentLevel === 'SubStation'" :label="$t('lineupMaster_form.bindPlc')" min-width="120">
            <template #default="scope">
              {{ scope.row.plcName || '—' }}
            </template>
          </ElTableColumn>
          <ElTableColumn v-if="currentLevel !== 'SubStation'" :label="$t('lineupMaster_form.status')" width="90">
            <template #default="scope">
              <el-tag :type="scope.row.isEnabled ? 'success' : 'info'" effect="plain">
                {{ scope.row.isEnabled ? $t('lineupMaster_form.enable') : $t('lineupMaster_form.disable') }}
              </el-tag>
            </template>
          </ElTableColumn>
          <ElTableColumn prop="remark" :label="$t('lineupMaster_form.remark')" min-width="180" show-overflow-tooltip>
            <template #default="scope">
              {{ scope.row.remark || '—' }}
            </template>
          </ElTableColumn>
          <ElTableColumn :label="$t('common.operate')" width="200" align="center" fixed="right">
            <template #default="scope">
              <ElButton v-if="currentLevel !== 'SubStation'" link type="primary" @click="onDrill(scope.row)">
                {{ $t('lineupMaster_form.drillDown') }}
              </ElButton>
              <ElButton link type="primary" @click="openEdit(scope.row)">
                {{ $t('common.edit') }}
              </ElButton>
              <ElButton link type="danger" @click="removeHierarchy(scope.row)">
                {{ $t('common.delete') }}
              </ElButton>
            </template>
          </ElTableColumn>
        </ElTable>
      </el-tab-pane>

      <el-tab-pane :label="$t('lineupMaster_form.deviceTab')" name="device">
        <section class="device-filter">
          <label>
            <span>{{ $t('lineupMaster_form.owningLine') }}</span>
            <el-select v-model="device.filterLineId" filterable clearable :placeholder="$t('lineupMaster_form.allLines')" style="width: 220px;" @change="loadDevices">
              <el-option v-for="line in lineOptions" :key="line.id" :label="`${line.code || '—'} · ${line.name || '—'}`" :value="line.id" />
            </el-select>
          </label>
          <label>
            <span>{{ $t('common.keyword') }}</span>
            <el-input
              v-model="device.keyword"
              :placeholder="$t('lineupMaster_form.deviceKeywordPlaceholder')"
              clearable
              style="width: 220px;"
              @keydown.enter="loadDevices"
              @clear="loadDevices"
            />
          </label>
          <ElButton type="primary" @click="loadDevices">
            {{ $t('lineupMaster_form.query') }}
          </ElButton>
          <ElButton type="primary" plain @click="openDeviceCreate">
            {{ $t('lineupMaster_form.addDevice') }}
          </ElButton>
        </section>

        <ElTable v-loading="device.loading" :data="device.items" stripe border>
          <ElTableColumn prop="code" :label="$t('lineupMaster_form.deviceCodeCol')" min-width="140" show-overflow-tooltip />
          <ElTableColumn prop="name" :label="$t('lineupMaster_form.projectName')" min-width="160" show-overflow-tooltip>
            <template #default="scope">
              {{ scope.row.name || '—' }}
            </template>
          </ElTableColumn>
          <ElTableColumn prop="ipAddress" :label="$t('lineupMaster_form.ipAddress')" min-width="130" show-overflow-tooltip>
            <template #default="scope">
              {{ scope.row.ipAddress || '—' }}
            </template>
          </ElTableColumn>
          <ElTableColumn :label="$t('lineupMaster_form.owningLine')" min-width="140">
            <template #default="scope">
              {{ lineNameOf(scope.row.lineId) }}
            </template>
          </ElTableColumn>
          <ElTableColumn prop="remark" :label="$t('lineupMaster_form.remark')" min-width="160" show-overflow-tooltip>
            <template #default="scope">
              {{ scope.row.remark || '—' }}
            </template>
          </ElTableColumn>
          <ElTableColumn :label="$t('common.operate')" width="140" align="center" fixed="right">
            <template #default="scope">
              <ElButton link type="primary" @click="openDeviceEdit(scope.row)">
                {{ $t('common.edit') }}
              </ElButton>
              <ElButton link type="danger" @click="removeDevice(scope.row)">
                {{ $t('common.delete') }}
              </ElButton>
            </template>
          </ElTableColumn>
        </ElTable>
      </el-tab-pane>
    </el-tabs>

    <el-dialog v-model="dialog.visible" :title="dialog.id ? $t('lineupMaster_form.editLevel', { level: currentLevelLabel }) : $t('lineupMaster_form.createLevel', { level: currentLevelLabel })" width="520px">
      <el-form label-width="90px">
        <el-form-item :label="$t('lineupMaster_form.code')" required>
          <el-input v-model="dialog.form.code" maxlength="100" :placeholder="$t('lineupMaster_form.codePlaceholder')" />
        </el-form-item>
        <el-form-item :label="$t('lineupMaster_form.name')" required>
          <el-input v-model="dialog.form.name" maxlength="200" />
        </el-form-item>
        <el-form-item v-if="currentLevel === 'SubStation'" :label="$t('lineupMaster_form.bindPlc')">
          <el-select v-model="dialog.form.plcId" filterable clearable :placeholder="$t('lineupMaster_form.selectDeviceInLine')" style="width: 100%;">
            <el-option v-for="dev in subStationDeviceOptions" :key="dev.id" :label="`${dev.code}${dev.ipAddress ? ` · ${dev.ipAddress}` : ''}`" :value="dev.id" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="currentLevel !== 'SubStation'" :label="$t('lineupMaster_form.status')">
          <el-switch v-model="dialog.form.isEnabled" :active-text="$t('lineupMaster_form.enable')" :inactive-text="$t('lineupMaster_form.disable')" />
        </el-form-item>
        <el-form-item :label="$t('lineupMaster_form.remark')">
          <el-input v-model="dialog.form.remark" type="textarea" :rows="2" maxlength="1000" />
        </el-form-item>
      </el-form>
      <template #footer>
        <ElButton @click="dialog.visible = false">
          {{ $t('common.cancel') }}
        </ElButton>
        <ElButton type="primary" @click="saveHierarchy">
          {{ $t('common.save') }}
        </ElButton>
      </template>
    </el-dialog>

    <el-dialog v-model="deviceDialog.visible" :title="deviceDialog.id ? $t('lineupMaster_form.editDevice') : $t('lineupMaster_form.addDevice')" width="520px">
      <el-form label-width="100px">
        <el-form-item :label="$t('lineupMaster_form.deviceNameForm')" required>
          <el-input v-model="deviceDialog.form.code" maxlength="100" :placeholder="$t('lineupMaster_form.deviceNamePlaceholder')" />
        </el-form-item>
        <el-form-item :label="$t('lineupMaster_form.projectName')">
          <el-input v-model="deviceDialog.form.name" maxlength="200" />
        </el-form-item>
        <el-form-item :label="$t('lineupMaster_form.ipAddress')">
          <el-input v-model="deviceDialog.form.ipAddress" maxlength="100" :placeholder="$t('lineupMaster_form.ipPlaceholder')" />
        </el-form-item>
        <el-form-item :label="$t('lineupMaster_form.owningLine')">
          <el-select v-model="deviceDialog.form.lineId" filterable clearable :placeholder="$t('lineupMaster_form.selectLine')" style="width: 100%;">
            <el-option v-for="line in lineOptions" :key="line.id" :label="`${line.code || '—'} · ${line.name || '—'}`" :value="line.id" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('lineupMaster_form.remark')">
          <el-input v-model="deviceDialog.form.remark" type="textarea" :rows="2" maxlength="1000" />
        </el-form-item>
      </el-form>
      <template #footer>
        <ElButton @click="deviceDialog.visible = false">
          {{ $t('common.cancel') }}
        </ElButton>
        <ElButton type="primary" @click="saveDevice">
          {{ $t('common.save') }}
        </ElButton>
      </template>
    </el-dialog>
  </main>
</template>

<style scoped>
.master-page {
  display: grid;
  gap: 14px;
  min-width: 0;
  padding: 16px;
  background: var(--el-fill-color-lighter);
}

.page-header,
.master-tabs {
  background: var(--el-bg-color);
  border: 1px solid var(--el-border-color-light);
  border-radius: 8px;
}

.page-header {
  display: flex;
  gap: 20px;
  align-items: center;
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

.hierarchy-selector,
.device-filter {
  display: flex;
  gap: 12px;
  align-items: flex-end;
  flex-wrap: wrap;
  margin-bottom: 14px;
}

.hierarchy-selector label,
.device-filter label {
  display: grid;
  gap: 6px;
  min-width: 0;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.hierarchy-selector :deep(.el-select) {
  width: 200px;
}

.table-toolbar {
  display: flex;
  gap: 12px;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  margin-bottom: 12px;
}

.crumb {
  display: flex;
  gap: 8px;
  align-items: center;
  flex-wrap: wrap;
}

.crumb-hint {
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.toolbar-actions {
  display: flex;
  gap: 8px;
}
</style>
