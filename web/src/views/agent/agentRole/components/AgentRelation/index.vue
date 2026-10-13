<script setup lang="ts">
import { ElMessage } from 'element-plus'
import { useI18n } from 'vue-i18n'
import apiAgentRole from '@/api/modules/agent/agentRole'
import apiAgentRelation from '@/api/modules/agent/agentRelation'
import messages from '../lang'

const props = defineProps<{
  roleId: string | number
  roleName: string
}>()

const visible = defineModel<boolean>({
  default: false,
})

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const l in messages) {
  mergeLocaleMessage(l, messages[l])
}

const loading = ref(false)
const saving = ref(false)
const allAgents = ref<any[]>([])
const selectedSet = ref(new Set<number | string>())

watch(visible, async (val) => {
  if (val && props.roleId) {
    await loadData()
  }
})

async function loadData() {
  loading.value = true
  try {
    // 并行加载所有智能体和已关联的下属ID
    const [agentsRes, subordinateIdsRes] = await Promise.all([
      apiAgentRole.listAll({ isEnabled: true }),
      apiAgentRelation.getSubordinateIdsByPlannerRoleId(props.roleId),
    ])
    
    // 过滤掉当前角色（不能将自己设为下属）
    allAgents.value = (agentsRes || []).filter((a: any) => a.id !== props.roleId)
    selectedSet.value = new Set(subordinateIdsRes || [])
  }
  catch {
    allAgents.value = []
    selectedSet.value = new Set()
  }
  finally {
    loading.value = false
  }
}

function toggleAgent(agentId: number | string) {
  if (selectedSet.value.has(agentId)) {
    selectedSet.value.delete(agentId)
  }
  else {
    selectedSet.value.add(agentId)
  }
  selectedSet.value = new Set(selectedSet.value)
}

function isSelected(agentId: number | string) {
  return selectedSet.value.has(agentId)
}

async function onSave() {
  saving.value = true
  try {
    const subordinateRoleIds = Array.from(selectedSet.value)
    await apiAgentRelation.saveSubordinates(props.roleId, subordinateRoleIds)
    ElMessage.success(t('agentRole_form.saveSubordinatesSuccess'))
    visible.value = false
  }
  catch {
    ElMessage.error(t('agentRole_form.saveFailed'))
  }
  finally {
    saving.value = false
  }
}

function onCancel() {
  visible.value = false
}

function getAgentTypeColor(type: string) {
  const colors: Record<string, string> = {
    Standard: 'primary',
    Workflow: 'success',
    Composite: 'warning',
    Code: 'danger',
  }
  return colors[type] || 'info'
}

function selectAll() {
  selectedSet.value = new Set(allAgents.value.map(a => a.id))
}

function clearAll() {
  selectedSet.value = new Set()
}
</script>

<template>
  <ElDialog v-model="visible" :title="$t('agentRole_form.subordinateTitle', { name: roleName })" width="800px" :close-on-click-modal="false" append-to-body destroy-on-close>
    <div v-loading="loading">
      <div class="flex justify-between items-center mb-4">
        <ElAlert type="warning" :closable="false" class="flex-1 mr-4">
          {{ $t('agentRole_form.subordinateTip', { name: roleName }) }}
        </ElAlert>
        <ElSpace>
          <ElButton size="small" @click="selectAll">
            {{ $t('agentRole_form.selectAll') }}
          </ElButton>
          <ElButton size="small" @click="clearAll">
            {{ $t('agentRole_form.clearAll') }}
          </ElButton>
        </ElSpace>
      </div>

      <div class="agent-list">
        <div
          v-for="agent in allAgents"
          :key="agent.id"
          class="agent-item"
          :class="{ 'is-selected': isSelected(agent.id) }"
          @click="toggleAgent(agent.id)"
        >
          <ElCheckbox :model-value="isSelected(agent.id)" @click.stop @change="toggleAgent(agent.id)">
            <div class="agent-info">
              <div class="agent-name">
                {{ agent.roleName }}
                <ElTag :type="getAgentTypeColor(agent.agentType)" size="small" class="ml-2">
                  {{ agent.agentType }}
                </ElTag>
              </div>
              <div class="agent-desc">
                {{ agent.description || $t('agentRole_form.noDescription') }}
              </div>
              <div class="agent-code">
                {{ $t('agentRole_form.code') }}: {{ agent.roleCode }}
              </div>
            </div>
          </ElCheckbox>
        </div>
      </div>

      <div v-if="allAgents.length === 0 && !loading" class="text-center text-gray-400 py-8">
        {{ $t('agentRole_form.noSubordinatesAvailable') }}
      </div>

      <div v-if="allAgents.length > 0" class="mt-4 text-gray-400 text-sm">
        {{ $t('agentRole_form.selectedSubordinateCount', { selected: selectedSet.size, total: allAgents.length }) }}
      </div>
    </div>
    <template #footer>
      <ElButton size="large" @click="onCancel">
        {{ $t('common.cancel') }}
      </ElButton>
      <ElButton type="primary" size="large" :loading="saving" @click="onSave">
        {{ $t('common.save') }}
      </ElButton>
    </template>
  </ElDialog>
</template>

<style scoped>
.agent-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
  max-height: 400px;
  overflow-y: auto;
}

.agent-item {
  padding: 12px;
  border: 1px solid var(--el-border-color-light);
  border-radius: 8px;
  cursor: pointer;
  transition: all 0.15s;
}

.agent-item:hover {
  border-color: var(--el-color-warning-light-5);
  background: var(--el-fill-color-light);
}

.agent-item.is-selected {
  border-color: var(--el-color-warning);
  background: var(--el-color-warning-light-9);
}

.agent-info {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.agent-name {
  font-weight: 500;
  display: flex;
  align-items: center;
}

.agent-desc {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.agent-code {
  color: var(--el-text-color-placeholder);
  font-size: 12px;
}
</style>
