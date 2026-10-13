<script setup lang="ts">
import { ElMessage } from 'element-plus'
import { useI18n } from 'vue-i18n'
import apiToolDefinition from '@/api/modules/agent/toolDefinition'
import apiRoleToolRelation from '@/api/modules/agent/roleToolRelation'
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
const allTools = ref<any[]>([])
// 使用 Set 优化选中状态判断性能
const selectedSet = ref(new Set<number | string>())

watch(visible, async (val) => {
  if (val && props.roleId) {
    await loadData()
  }
})

async function loadData() {
  loading.value = true
  try {
    // 并行加载所有工具和已授权工具ID
    const [toolsRes, toolIdsRes] = await Promise.all([
      apiToolDefinition.listAll({ isEnabled: true }),
      apiRoleToolRelation.getToolIdsByRoleId(props.roleId),
    ])
    allTools.value = toolsRes || []
    selectedSet.value = new Set(toolIdsRes || [])
  }
  catch {
    allTools.value = []
    selectedSet.value = new Set()
  }
  finally {
    loading.value = false
  }
}

function toggleTool(toolId: number | string) {
  if (selectedSet.value.has(toolId)) {
    selectedSet.value.delete(toolId)
  }
  else {
    selectedSet.value.add(toolId)
  }
  // 触发响应式更新
  selectedSet.value = new Set(selectedSet.value)
}

function isSelected(toolId: number | string) {
  return selectedSet.value.has(toolId)
}

async function onSave() {
  saving.value = true
  try {
    const toolIds = Array.from(selectedSet.value)
    await apiRoleToolRelation.saveToolAuth(props.roleId, toolIds)
    ElMessage.success(t('agentRole_form.saveToolAuthSuccess'))
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

function getToolTypeColor(type: string) {
  const colors: Record<string, string> = {
    Local: 'success',
    Http: 'primary',
    Mcp: 'warning',
  }
  return colors[type] || 'info'
}

function selectAll() {
  selectedSet.value = new Set(allTools.value.map(t => t.id))
}

function clearAll() {
  selectedSet.value = new Set()
}
</script>

<template>
  <ElDialog v-model="visible" :title="$t('agentRole_form.toolAuthTitle', { name: roleName })" width="800px" :close-on-click-modal="false" append-to-body destroy-on-close>
    <div v-loading="loading">
      <div class="flex justify-between items-center mb-4">
        <ElAlert type="info" :closable="false" class="flex-1 mr-4">
          {{ $t('agentRole_form.toolAuthTip', { name: roleName }) }}
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

      <div class="tool-list">
        <div
          v-for="tool in allTools"
          :key="tool.id"
          class="tool-item"
          :class="{ 'is-selected': isSelected(tool.id) }"
          @click="toggleTool(tool.id)"
        >
          <ElCheckbox :model-value="isSelected(tool.id)" @click.stop @change="toggleTool(tool.id)">
            <div class="tool-info">
              <div class="tool-name">
                {{ tool.toolName }}
                <ElTag :type="getToolTypeColor(tool.toolType)" size="small" class="ml-2">
                  {{ tool.toolType }}
                </ElTag>
              </div>
              <div class="tool-desc">
                {{ tool.description || $t('agentRole_form.noDescription') }}
              </div>
              <div class="tool-code">
                {{ $t('agentRole_form.code') }}: {{ tool.toolCode }}
              </div>
            </div>
          </ElCheckbox>
        </div>
      </div>

      <div v-if="allTools.length === 0 && !loading" class="text-center text-gray-400 py-8">
        {{ $t('agentRole_form.noToolsAvailable') }}
      </div>

      <div v-if="allTools.length > 0" class="mt-4 text-gray-400 text-sm">
        {{ $t('agentRole_form.selectedToolCount', { selected: selectedSet.size, total: allTools.length }) }}
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
.tool-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
  max-height: 400px;
  overflow-y: auto;
}

.tool-item {
  padding: 12px;
  border: 1px solid var(--el-border-color-light);
  border-radius: 8px;
  cursor: pointer;
  transition: all 0.15s;
}

.tool-item:hover {
  border-color: var(--el-color-primary-light-5);
  background: var(--el-fill-color-light);
}

.tool-item.is-selected {
  border-color: var(--el-color-primary);
  background: var(--el-color-primary-light-9);
}

.tool-info {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.tool-name {
  font-weight: 500;
  display: flex;
  align-items: center;
}

.tool-desc {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}

.tool-code {
  color: var(--el-text-color-placeholder);
  font-size: 12px;
}
</style>
