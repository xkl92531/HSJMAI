<script setup lang="ts">
import { ElMessage } from 'element-plus'
import { useI18n } from 'vue-i18n'
import apiSkillDefinition from '@/api/modules/agent/skillDefinition'
import apiAgentSkillRelation from '@/api/modules/agent/agentSkillRelation'
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
const allSkills = ref<any[]>([])
const selectedSet = ref(new Set<number | string>())

watch(visible, async (val) => {
  if (val && props.roleId) {
    await loadData()
  }
})

async function loadData() {
  loading.value = true
  try {
    // 并行加载所有已批准技能和已授权技能ID
    const [skillsRes, skillIdsRes] = await Promise.all([
      apiSkillDefinition.listAll({ isEnabled: true, approvalStatus: 'Approved' }),
      apiAgentSkillRelation.getSkillIdsByRoleId(props.roleId),
    ])
    allSkills.value = skillsRes || []
    selectedSet.value = new Set(skillIdsRes || [])
  }
  catch {
    allSkills.value = []
    selectedSet.value = new Set()
  }
  finally {
    loading.value = false
  }
}

function toggleSkill(skillId: number | string) {
  if (selectedSet.value.has(skillId)) {
    selectedSet.value.delete(skillId)
  }
  else {
    selectedSet.value.add(skillId)
  }
  selectedSet.value = new Set(selectedSet.value)
}

function isSelected(skillId: number | string) {
  return selectedSet.value.has(skillId)
}

async function onSave() {
  saving.value = true
  try {
    const skillIds = Array.from(selectedSet.value)
    await apiAgentSkillRelation.saveSkillAuth(props.roleId, skillIds)
    ElMessage.success(t('agentRole_form.saveSkillAuthSuccess'))
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

function getCategoryColor(category: string) {
  const colors: Record<string, string> = {
    Perception: 'primary',
    Analysis: 'success',
    Decision: 'warning',
    Execution: 'danger',
    Collaboration: 'info',
  }
  return colors[category] || 'info'
}

function selectAll() {
  selectedSet.value = new Set(allSkills.value.map(s => s.id))
}

function clearAll() {
  selectedSet.value = new Set()
}
</script>

<template>
  <ElDialog v-model="visible" :title="$t('agentRole_form.skillAuthTitle', { name: roleName })" width="800px" :close-on-click-modal="false" append-to-body destroy-on-close>
    <div v-loading="loading">
      <div class="flex justify-between items-center mb-4">
        <ElAlert type="info" :closable="false" class="flex-1 mr-4">
          {{ $t('agentRole_form.skillAuthTip', { name: roleName }) }}
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

      <div class="skill-list">
        <div
          v-for="skill in allSkills"
          :key="skill.id"
          class="skill-item"
          :class="{ 'is-selected': isSelected(skill.id) }"
          @click="toggleSkill(skill.id)"
        >
          <ElCheckbox :model-value="isSelected(skill.id)" @click.stop @change="toggleSkill(skill.id)">
            <div class="skill-info">
              <div class="skill-name">
                {{ skill.skillName }}
                <ElTag v-if="skill.category" :type="getCategoryColor(skill.category)" size="small" class="ml-2">
                  {{ skill.category }}
                </ElTag>
                <ElTag v-if="skill.isAgentSkill" type="success" size="small" class="ml-1">
                  Agent Skill
                </ElTag>
              </div>
              <div class="skill-desc">
                {{ skill.description || $t('agentRole_form.noDescription') }}
              </div>
              <div class="skill-meta">
                <span>{{ $t('agentRole_form.code') }}: {{ skill.skillCode }}</span>
                <span v-if="skill.applicableBrand" class="ml-4">{{ $t('agentRole_form.brand') }}: {{ skill.applicableBrand }}</span>
                <span v-if="skill.applicableFileFormat" class="ml-4">{{ $t('agentRole_form.format') }}: {{ skill.applicableFileFormat }}</span>
              </div>
            </div>
          </ElCheckbox>
        </div>
      </div>

      <div v-if="allSkills.length === 0 && !loading" class="text-center text-gray-400 py-8">
        {{ $t('agentRole_form.noSkillsAvailable') }}
      </div>

      <div v-if="allSkills.length > 0" class="mt-4 text-gray-400 text-sm">
        {{ $t('agentRole_form.selectedSkillCount', { selected: selectedSet.size, total: allSkills.length }) }}
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
.skill-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
  max-height: 400px;
  overflow-y: auto;
}

.skill-item {
  padding: 12px;
  border: 1px solid var(--el-border-color-light);
  border-radius: 8px;
  cursor: pointer;
  transition: all 0.15s;
}

.skill-item:hover {
  border-color: var(--el-color-primary-light-5);
  background: var(--el-fill-color-light);
}

.skill-item.is-selected {
  border-color: var(--el-color-primary);
  background: var(--el-color-primary-light-9);
}

.skill-info {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.skill-name {
  font-weight: 500;
  display: flex;
  align-items: center;
}

.skill-desc {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.skill-meta {
  color: var(--el-text-color-placeholder);
  font-size: 12px;
}
</style>
