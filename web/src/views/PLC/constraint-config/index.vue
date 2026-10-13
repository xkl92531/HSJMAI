<route lang="yaml">
meta:
  title: 约束配置
  icon: ant-design:control-outlined
</route>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { useRoute } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import ConstraintAPI from '@/api/modules/PLC/constraint'
import ProjectAPI from '@/api/modules/PLC/project'
import messages from './index.lang'

defineOptions({ name: 'PLCConstraintConfig' })

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const route = useRoute()
const activeTab = ref('rules')

// ---------- 选项常量 ----------
// 仅保留诊断引擎(CheckConstraintViolation)真正会判定、且约束生成实际产出的类型/运算符
const ruleTypeOptions = [
  { label: t('constraint_config.ruleTypeMutex'), value: 'Mutex' },
  { label: t('constraint_config.ruleTypePairing'), value: 'Pairing' },
  { label: t('constraint_config.ruleTypeAllRequired'), value: 'AllRequired' },
  { label: t('constraint_config.ruleTypeSynchronous'), value: 'Synchronous' },
  { label: t('constraint_config.ruleTypeSafety'), value: 'Safety' },
]
const operatorOptions = [
  { value: 'AT_MOST_ONE', label: t('constraint_config.operatorAtMostOne') },
  { value: 'IMPLIES', label: t('constraint_config.operatorImplies') },
  { value: 'ALL_TRUE', label: t('constraint_config.operatorAllTrue') },
  { value: 'SAME_STATE', label: t('constraint_config.operatorSameState') },
  { value: 'REQUIRES_0_WHEN_FALSE', label: t('constraint_config.operatorRequires0WhenFalse') },
  { value: 'MUST_BE_FALSE', label: t('constraint_config.operatorMustBeFalse') },
]
// 运算符中文释义（表格悬浮提示 + 帮助理解）
const operatorMeaning: Record<string, string> = {
  AT_MOST_ONE: t('constraint_config.meaningAtMostOne'),
  IMPLIES: t('constraint_config.meaningImplies'),
  ALL_TRUE: t('constraint_config.meaningAllTrue'),
  SAME_STATE: t('constraint_config.meaningSameState'),
  REQUIRES_0_WHEN_FALSE: t('constraint_config.meaningRequires0WhenFalse'),
  MUST_BE_FALSE: t('constraint_config.meaningMustBeFalse'),
}
const roleOptions = [
  { label: t('constraint_config.roleSource'), value: 'Source' },
  { label: t('constraint_config.roleTarget'), value: 'Target' },
  { label: t('constraint_config.roleMember'), value: 'Member' },
]
const priorityOptions = [
  { value: 'Critical', label: t('constraint_config.priorityCritical') },
  { value: 'High', label: t('constraint_config.priorityHigh') },
  { value: 'Normal', label: t('constraint_config.priorityNormal') },
  { value: 'Info', label: t('constraint_config.priorityInfo') },
]
const priorityLabel: Record<string, string> = {
  Critical: t('constraint_config.priorityLabelCritical'),
  High: t('constraint_config.priorityLabelHigh'),
  Normal: t('constraint_config.priorityLabelNormal'),
  Info: t('constraint_config.priorityLabelInfo'),
}
const typeDefaultOperator: Record<string, string> = {
  Mutex: 'AT_MOST_ONE',
  Pairing: 'IMPLIES',
  AllRequired: 'ALL_TRUE',
  Synchronous: 'SAME_STATE',
  Safety: 'REQUIRES_0_WHEN_FALSE',
}

function ruleTypeTagType(t: string) {
  switch (t) {
    case 'Mutex': case 'Safety': return 'danger'
    case 'Pairing': return 'primary'
    case 'AllRequired': return 'success'
    case 'Synchronous': return 'warning'
    default: return 'info'
  }
}
function priorityType(p: string) {
  switch (p) {
    case 'Critical': return 'danger'
    case 'High': return 'warning'
    case 'Normal': return 'info'
    default: return 'info'
  }
}
// 固定、直观、不跟随主题的角色配色（避免主题主色=橙色把驱动端染成橙色看着像告警）
function roleClass(r: string) {
  switch (r) {
    case 'Source': return 'role-source'
    case 'Target': return 'role-target'
    default: return 'role-member'
  }
}
function roleLabel(r: string) {
  switch (r) {
    case 'Source': return t('constraint_config.roleLabelSource')
    case 'Target': return t('constraint_config.roleLabelTarget')
    case 'Member': return t('constraint_config.roleLabelMember')
    default: return r
  }
}

// ---------- 项目下拉 ----------
const projects = ref<any[]>([])
const projectId = ref<number | string>('')

async function loadProjects() {
  try {
    const res: any = await ProjectAPI.list({ page: 1, limit: 1000 })
    projects.value = res.data || []
    const q = route.query.projectId as string
    if (q && projects.value.some((p: any) => String(p.id) === String(q))) {
      projectId.value = q
    }
    else if (projects.value.length > 0) {
      projectId.value = projects.value[0].id
    }
    if (projectId.value) fetchRules()
  }
  catch (e: any) {
    ElMessage.error(e.message || t('constraint_config.loadProjectsFailed'))
  }
}

// ---------- 约束列表 ----------
const loading = ref(false)
const rules = ref<any[]>([])
const pagination = reactive({ page: 1, limit: 10, total: 0 })
const filterForm = reactive({ ruleType: '', equipmentId: '' })

async function fetchRules() {
  if (!projectId.value) {
    rules.value = []
    pagination.total = 0
    return
  }
  loading.value = true
  try {
    const res: any = await ConstraintAPI.list({
      projectId: projectId.value,
      page: pagination.page,
      limit: pagination.limit,
      ruleType: filterForm.ruleType || undefined,
      equipmentId: filterForm.equipmentId || undefined,
    })
    rules.value = res.data || []
    pagination.total = res.total || 0
  }
  catch (e: any) {
    ElMessage.error(e.message || t('constraint_config.loadRulesFailed'))
  }
  finally {
    loading.value = false
  }
}

function onProjectChange() {
  pagination.page = 1
  fetchRules()
}
function handleSearch() {
  pagination.page = 1
  fetchRules()
}
function handleReset() {
  filterForm.ruleType = ''
  filterForm.equipmentId = ''
  pagination.page = 1
  fetchRules()
}
function handlePageChange(v: number) {
  pagination.page = v
  fetchRules()
}

function handleDelete(row: any) {
  ElMessageBox.confirm(t('constraint_config.deleteConfirmMsg', { name: row.ruleName }), t('common.confirmDelete'), {
    confirmButtonText: t('common.confirm'),
    cancelButtonText: t('common.cancel'),
    type: 'warning',
  }).then(async () => {
    try {
      await ConstraintAPI.delete(row.id)
      ElMessage.success(t('common.deleteSuccess'))
      fetchRules()
    }
    catch (e: any) {
      ElMessage.error(e.message || t('constraint_config.deleteFailed'))
    }
  }).catch(() => {})
}

// ---------- 新增/编辑弹窗 ----------
const dialogVisible = ref(false)
const dialogType = ref<'create' | 'edit'>('create')
const editId = ref<number | string>('')
const form = reactive<any>({
  ruleName: '',
  ruleType: 'Mutex',
  operator: 'AT_MOST_ONE',
  equipmentId: '',
  priority: 'High',
  confidence: undefined,
  description: '',
  signals: [] as any[],
})

function resetForm() {
  form.ruleName = ''
  form.ruleType = 'Mutex'
  form.operator = 'AT_MOST_ONE'
  form.equipmentId = ''
  form.priority = 'High'
  form.confidence = undefined
  form.description = ''
  form.signals = [{ signalName: '', signalAddress: '', signalRole: 'Member' }]
}

function onTypeChange(t: string) {
  if (typeDefaultOperator[t]) form.operator = typeDefaultOperator[t]
}

function handleCreate() {
  if (!projectId.value) {
    ElMessage.warning(t('constraint_config.selectProjectFirst'))
    return
  }
  dialogType.value = 'create'
  editId.value = ''
  resetForm()
  dialogVisible.value = true
}

function handleEdit(row: any) {
  dialogType.value = 'edit'
  editId.value = row.id
  form.ruleName = row.ruleName
  form.ruleType = row.ruleType
  form.operator = row.operator
  form.equipmentId = row.equipmentId || ''
  form.priority = row.priority || 'High'
  form.confidence = row.confidence ?? undefined
  form.description = row.description || ''
  form.signals = (row.signals || []).map((s: any) => ({
    signalName: s.signalName,
    signalAddress: s.signalAddress || '',
    signalRole: s.signalRole || 'Member',
  }))
  if (form.signals.length === 0) {
    form.signals = [{ signalName: '', signalAddress: '', signalRole: 'Member' }]
  }
  dialogVisible.value = true
}

function addSignalRow() {
  form.signals.push({ signalName: '', signalAddress: '', signalRole: 'Member' })
}
function removeSignalRow(i: number) {
  form.signals.splice(i, 1)
}

async function submitForm() {
  if (!form.ruleName?.trim()) {
    ElMessage.warning(t('constraint_config.inputRuleName'))
    return
  }
  const validSignals = form.signals.filter((s: any) => s.signalName?.trim())
  if (validSignals.length < 1) {
    ElMessage.warning(t('constraint_config.atLeastOneSignal'))
    return
  }

  const payload = {
    projectId: projectId.value,
    ruleName: form.ruleName.trim(),
    ruleType: form.ruleType,
    operator: form.operator,
    equipmentId: form.equipmentId || null,
    priority: form.priority,
    confidence: form.confidence ?? null,
    description: form.description || null,
    signals: validSignals.map((s: any, i: number) => ({
      signalName: s.signalName.trim(),
      signalAddress: s.signalAddress || null,
      signalRole: s.signalRole,
      sortOrder: i,
    })),
  }

  try {
    if (dialogType.value === 'create') {
      await ConstraintAPI.createRule(payload)
    }
    else {
      await ConstraintAPI.updateRule(editId.value, payload)
    }
    ElMessage.success(t('common.saveSuccess'))
    dialogVisible.value = false
    fetchRules()
  }
  catch (e: any) {
    ElMessage.error(e.message || t('constraint_config.saveFailed'))
  }
}

// ---------- 提示词 ----------
const promptLoading = ref(false)
const promptSaving = ref(false)
const promptContent = ref('')
const promptIsCustomized = ref(false)

async function fetchPrompt() {
  promptLoading.value = true
  try {
    const res: any = await ConstraintAPI.getPrompt()
    promptContent.value = res.content || ''
    promptIsCustomized.value = !!res.isCustomized
  }
  catch (e: any) {
    ElMessage.error(e.message || t('constraint_config.loadPromptFailed'))
  }
  finally {
    promptLoading.value = false
  }
}

async function savePrompt() {
  if (!promptContent.value?.trim()) {
    ElMessage.warning(t('constraint_config.promptEmptyWarning'))
    return
  }
  promptSaving.value = true
  try {
    await ConstraintAPI.savePrompt(promptContent.value)
    ElMessage.success(t('constraint_config.promptSaved'))
    fetchPrompt()
  }
  catch (e: any) {
    ElMessage.error(e.message || t('constraint_config.saveFailed'))
  }
  finally {
    promptSaving.value = false
  }
}

function resetPrompt() {
  ElMessageBox.confirm(t('constraint_config.resetPromptConfirmMsg'), t('constraint_config.restoreDefault'), {
    confirmButtonText: t('common.confirm'),
    cancelButtonText: t('common.cancel'),
    type: 'warning',
  }).then(async () => {
    try {
      await ConstraintAPI.resetPrompt()
      ElMessage.success(t('constraint_config.restoredToDefault'))
      fetchPrompt()
    }
    catch (e: any) {
      ElMessage.error(e.message || t('constraint_config.operateFailed'))
    }
  }).catch(() => {})
}

onMounted(() => {
  loadProjects()
  fetchPrompt()
})
</script>

<template>
  <div class="absolute-container">
    <FaPageMain class="flex-1 overflow-auto" :main-class="{ 'flex-1 flex flex-col overflow-auto': true }">
      <el-tabs v-model="activeTab" class="cc-tabs">
        <!-- Tab 1: 约束规则 -->
        <el-tab-pane :label="$t('constraint_config.rulesTab')" name="rules">
          <div class="pane">
          <div class="toolbar">
            <el-form :inline="true" @submit.prevent>
              <el-form-item :label="$t('constraint_config.project')">
                <el-select v-model="projectId" filterable :placeholder="$t('constraint_config.selectProjectPlaceholder')" style="width: 220px" @change="onProjectChange">
                  <el-option v-for="p in projects" :key="p.id" :label="p.projectName" :value="p.id" />
                </el-select>
              </el-form-item>
              <el-form-item :label="$t('constraint_config.ruleType')">
                <el-select v-model="filterForm.ruleType" :placeholder="$t('constraint_config.all')" clearable style="width: 160px">
                  <el-option v-for="opt in ruleTypeOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
                </el-select>
              </el-form-item>
              <el-form-item :label="$t('constraint_config.equipmentId')">
                <el-input v-model="filterForm.equipmentId" :placeholder="$t('constraint_config.equipmentId')" clearable style="width: 140px" @keyup.enter="handleSearch" />
              </el-form-item>
              <el-form-item>
                <el-button type="primary" @click="handleSearch">
                  <FaIcon name="i-ep:search" class="mr-1" /> {{ $t('common.search') }}
                </el-button>
                <el-button @click="handleReset">
                  <FaIcon name="i-ep:refresh" class="mr-1" /> {{ $t('common.reset') }}
                </el-button>
                <el-button type="success" @click="handleCreate">
                  <FaIcon name="i-ep:plus" class="mr-1" /> {{ $t('constraint_config.addRule') }}
                </el-button>
              </el-form-item>
            </el-form>
          </div>

          <div class="role-legend">
            <span class="legend-label">{{ $t('constraint_config.signalRoleLegend') }}</span>
            <el-tag size="small" class="role-source">{{ $t('constraint_config.roleSource') }}</el-tag>
            <el-tag size="small" class="role-target">{{ $t('constraint_config.roleTarget') }}</el-tag>
            <el-tag size="small" class="role-member">{{ $t('constraint_config.roleMember') }}</el-tag>
            <span class="legend-hint">{{ $t('constraint_config.legendHint') }}</span>
          </div>

          <div class="table-box">
          <el-table v-loading="loading" :data="rules" height="100%" style="width: 100%" stripe border row-key="id">
            <el-table-column prop="ruleName" :label="$t('constraint_config.ruleName')" min-width="160" show-overflow-tooltip />
            <el-table-column prop="ruleType" :label="$t('constraint_config.type')" width="110" align="center">
              <template #default="{ row }">
                <el-tag :type="ruleTypeTagType(row.ruleType)" size="small">{{ row.ruleType }}</el-tag>
              </template>
            </el-table-column>
            <el-table-column prop="operator" :label="$t('constraint_config.operator')" width="180" align="center">
              <template #default="{ row }">
                <el-tooltip :content="operatorMeaning[row.operator] || row.operator" placement="top">
                  <el-text type="info" size="small">{{ row.operator }}</el-text>
                </el-tooltip>
              </template>
            </el-table-column>
            <el-table-column prop="priority" :label="$t('constraint_config.priority')" width="90" align="center">
              <template #default="{ row }">
                <el-tag :type="priorityType(row.priority)" size="small">{{ priorityLabel[row.priority] || row.priority }}</el-tag>
              </template>
            </el-table-column>
            <el-table-column :label="$t('constraint_config.signals')" min-width="280">
              <template #default="{ row }">
                <div class="signal-list">
                  <el-tag
                    v-for="(s, i) in (row.signals || [])"
                    :key="i"
                    size="small"
                    :class="['signal-tag', roleClass(s.signalRole)]"
                    :title="`${s.signalName} (${s.signalAddress || '-'}) [${roleLabel(s.signalRole)}]`"
                  >
                    {{ s.signalName }}
                    <span v-if="s.signalRole !== 'Member'" class="role-badge">{{ roleLabel(s.signalRole) }}</span>
                  </el-tag>
                </div>
              </template>
            </el-table-column>
            <el-table-column prop="source" :label="$t('constraint_config.source')" width="90" align="center">
              <template #default="{ row }">
                <el-tag :type="row.source === 'Manual' ? 'warning' : 'info'" size="small">
                  {{ row.source === 'Manual' ? $t('constraint_config.manual') : (row.source || 'AI') }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column :label="$t('common.operate')" width="130" fixed="right" align="center">
              <template #default="{ row }">
                <el-button link type="primary" size="small" @click="handleEdit(row)">
                  <FaIcon name="i-ep:edit" /> {{ $t('common.edit') }}
                </el-button>
                <el-button link type="danger" size="small" @click="handleDelete(row)">
                  <FaIcon name="i-ep:delete" /> {{ $t('common.delete') }}
                </el-button>
              </template>
            </el-table-column>
          </el-table>
          </div>

          <el-pagination
            v-if="pagination.total > 0"
            v-model:current-page="pagination.page"
            v-model:page-size="pagination.limit"
            :total="pagination.total"
            layout="total, prev, pager, next"
            class="pagination"
            background
            @current-change="handlePageChange"
          />
          </div>
        </el-tab-pane>

        <!-- Tab 2: 生成提示词 -->
        <el-tab-pane :label="$t('constraint_config.promptTab')" name="prompt">
          <div v-loading="promptLoading" class="pane prompt-pane">
            <div class="prompt-toolbar">
              <el-tag size="small" :type="promptIsCustomized ? 'warning' : 'success'">
                {{ promptIsCustomized ? $t('constraint_config.customized') : $t('constraint_config.systemDefault') }}
              </el-tag>
              <span class="cc-hint">{{ $t('constraint_config.charCount', { count: promptContent.length }) }}</span>
              <div class="toolbar-spacer" />
              <el-button type="primary" :loading="promptSaving" @click="savePrompt">
                <FaIcon name="i-ep:check" class="mr-1" /> {{ $t('common.save') }}
              </el-button>
              <el-button @click="resetPrompt">
                <FaIcon name="i-ep:refresh-left" class="mr-1" /> {{ $t('constraint_config.restoreDefault') }}
              </el-button>
            </div>
            <el-alert type="info" :closable="false" show-icon class="mb-3">
              <template #title>
                {{ $t('constraint_config.promptAlertPre') }}<strong>{{ $t('constraint_config.promptAlertStrong') }}</strong>{{ $t('constraint_config.promptAlertPost') }}
              </template>
            </el-alert>
            <div class="textarea-box">
              <el-input
                v-model="promptContent"
                type="textarea"
                class="prompt-textarea"
                resize="none"
                :placeholder="$t('constraint_config.promptPlaceholder')"
              />
            </div>
          </div>
        </el-tab-pane>
      </el-tabs>
    </FaPageMain>

    <!-- 新增/编辑弹窗 -->
    <el-dialog v-model="dialogVisible" :title="dialogType === 'create' ? $t('constraint_config.addRule') : $t('constraint_config.editRule')" width="760px" destroy-on-close>
      <el-form :model="form" label-width="90px">
        <el-row :gutter="12">
          <el-col :span="12">
            <el-form-item :label="$t('constraint_config.ruleName')" required>
              <el-input v-model="form.ruleName" :placeholder="$t('constraint_config.ruleName')" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('constraint_config.equipmentId')">
              <el-input v-model="form.equipmentId" :placeholder="$t('constraint_config.optional')" />
            </el-form-item>
          </el-col>
        </el-row>
        <el-row :gutter="12">
          <el-col :span="12">
            <el-form-item :label="$t('constraint_config.ruleType')">
              <el-select v-model="form.ruleType" style="width: 100%" @change="onTypeChange">
                <el-option v-for="o in ruleTypeOptions" :key="o.value" :label="o.label" :value="o.value" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('constraint_config.priority')">
              <el-select v-model="form.priority" style="width: 100%">
                <el-option v-for="o in priorityOptions" :key="o.value" :label="o.label" :value="o.value" />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>
        <el-form-item :label="$t('constraint_config.operator')">
          <el-select v-model="form.operator" style="width: 100%">
            <el-option v-for="o in operatorOptions" :key="o.value" :label="o.label" :value="o.value" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('constraint_config.description')">
          <el-input v-model="form.description" type="textarea" :autosize="{ minRows: 4, maxRows: 10 }" :placeholder="$t('constraint_config.optional')" />
        </el-form-item>
        <el-form-item :label="$t('constraint_config.signalList')">
          <div class="signals-editor">
            <div v-for="(s, i) in form.signals" :key="i" class="signal-row">
              <el-input v-model="s.signalName" :placeholder="$t('constraint_config.signalName')" style="flex: 2" />
              <el-input v-model="s.signalAddress" :placeholder="$t('constraint_config.addressPlaceholder')" style="flex: 1.2" />
              <el-select v-model="s.signalRole" style="width: 150px">
                <el-option v-for="r in roleOptions" :key="r.value" :label="r.label" :value="r.value" />
              </el-select>
              <el-button link type="danger" @click="removeSignalRow(i)">
                <FaIcon name="i-ep:remove" />
              </el-button>
            </div>
            <el-button link type="primary" @click="addSignalRow">
              <FaIcon name="i-ep:plus" class="mr-1" /> {{ $t('constraint_config.addSignal') }}
            </el-button>
          </div>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible = false">{{ $t('common.cancel') }}</el-button>
        <el-button type="primary" @click="submitForm">{{ $t('common.save') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.absolute-container {
  position: absolute;
  display: flex;
  flex-direction: column;
  width: 100%;
  height: 100%;
}

.cc-tabs {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
}

.cc-tabs :deep(.el-tabs__content) {
  flex: 1;
  min-height: 0;
}

.cc-tabs :deep(.el-tab-pane) {
  height: 100%;
}

/* 每个标签内部：填满高度的 flex 列 */
.pane {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
}

/* 表格区：占满剩余高度，el-table height=100% 在内部滚动 */
.table-box {
  flex: 1;
  min-height: 0;
}

.pagination {
  justify-content: flex-end;
  margin-top: 12px;
}

/* 提示词框区：占满剩余高度，textarea 内部滚动 */
.textarea-box {
  flex: 1;
  min-height: 0;
}

.toolbar {
  margin-bottom: 12px;
}

.toolbar :deep(.el-form-item) {
  margin-bottom: 8px;
}

.role-legend {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
  margin-bottom: 10px;
}

.legend-label {
  font-size: 13px;
  color: var(--el-text-color-secondary);
}

.legend-hint {
  font-size: 12px;
  color: var(--el-text-color-placeholder);
}

.signal-list {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}

.signal-tag {
  max-width: 220px;
  overflow: hidden;
  text-overflow: ellipsis;
}

/* 角色固定配色：蓝=驱动端 / 绿=反馈端 / 灰=成员（覆盖 el-tag 主题色变量，表格与图例统一） */
.role-source {
  --el-tag-bg-color: #eef4ff;
  --el-tag-border-color: #c6d8fb;
  --el-tag-text-color: #2563eb;
}

.role-target {
  --el-tag-bg-color: #eafaf0;
  --el-tag-border-color: #bfe6cd;
  --el-tag-text-color: #16a34a;
}

.role-member {
  --el-tag-bg-color: #f4f4f5;
  --el-tag-border-color: #e2e2e6;
  --el-tag-text-color: #6b7280;
}

.role-badge {
  margin-left: 4px;
  font-size: 10px;
  opacity: 0.7;
}

.signals-editor {
  width: 100%;
}

.signal-row {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 8px;
}

.prompt-pane {
  max-width: 1100px;
}

.prompt-toolbar {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 4px 0 10px;
}

.toolbar-spacer {
  flex: 1;
}

.prompt-textarea {
  height: 100%;
}

.prompt-textarea :deep(.el-textarea__inner) {
  height: 100%;
  font-family: 'JetBrains Mono', Consolas, Monaco, monospace;
  font-size: 13px;
  line-height: 1.6;
}

.cc-hint {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
</style>
