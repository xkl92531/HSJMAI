<script setup lang="ts">
import type { ModelPurposeBinding, ModelPurposeOption } from '@/api/modules/agent/modelPurposeBinding'
import { ElMessage } from 'element-plus'
import { useI18n } from 'vue-i18n'
import apiModelPurposeBinding from '@/api/modules/agent/modelPurposeBinding'
import messages from './lang'

const visible = defineModel<boolean>({ default: false })
const { t, mergeLocaleMessage } = useI18n()

for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const bindings = ref<ModelPurposeBinding[]>([])
const models = ref<ModelPurposeOption[]>([])
const selectedModels = ref<Record<string, string>>({})
const loading = ref(false)
const savingPurpose = ref('')
const errorMessage = ref('')
let loadSequence = 0

watch(visible, (open) => {
  if (open) {
    loadBindings()
  }
  else {
    loadSequence++
    errorMessage.value = ''
  }
})

async function loadBindings() {
  const sequence = ++loadSequence
  loading.value = true
  errorMessage.value = ''
  try {
    const result = await apiModelPurposeBinding.list()
    if (sequence !== loadSequence) {
      return
    }
    bindings.value = result.bindings
    models.value = result.models
    selectedModels.value = Object.fromEntries(result.bindings.map(binding => [
      binding.purposeCode,
      result.models.some(model => model.id === binding.modelId && model.isEnabled)
        ? binding.modelId ?? ''
        : '',
    ]))
  }
  catch {
    if (sequence === loadSequence) {
      errorMessage.value = t('modelPurpose.loadFailed')
    }
  }
  finally {
    if (sequence === loadSequence) {
      loading.value = false
    }
  }
}

function purposeName(binding: ModelPurposeBinding) {
  const key = `modelPurpose.purposes.${binding.purposeCode}`
  return t(key) === key ? binding.displayName || binding.purposeCode : t(key)
}

function eligibleModels(binding: ModelPurposeBinding) {
  const types = binding.purposeCode === 'memory_embedding' ? ['Embedding'] : ['Chat', 'VL']
  return models.value.filter(model => model.isEnabled && types.includes(model.modelType))
}

function statusType(binding: ModelPurposeBinding): 'warning' | 'danger' | 'success' {
  if (binding.status === 'Ready') {
    return 'success'
  }
  if (binding.status === 'Unbound') {
    return 'warning'
  }
  return 'danger'
}

function statusLabel(binding: ModelPurposeBinding) {
  const statusKeys: Record<string, string> = {
    Ready: 'bound',
    Unbound: 'unbound',
    ModelUnavailable: 'unavailable',
    ModelDisabled: 'disabled',
    ModelTypeMismatch: 'typeMismatch',
    ModelIncomplete: 'incomplete',
  }
  return t(`modelPurpose.${statusKeys[binding.status] ?? 'invalid'}`)
}

async function saveBinding(binding: ModelPurposeBinding) {
  const modelId = selectedModels.value[binding.purposeCode]
  if (!modelId || modelId === binding.modelId || savingPurpose.value) {
    return
  }
  savingPurpose.value = binding.purposeCode
  errorMessage.value = ''
  try {
    await apiModelPurposeBinding.set({
      purposeCode: binding.purposeCode,
      modelId,
      expectedRevision: binding.revision,
    })
    await loadBindings()
    ElMessage.success(t('modelPurpose.saved'))
  }
  catch (error: any) {
    if (error?.response?.status === 409) {
      await loadBindings()
      errorMessage.value = t('modelPurpose.conflict')
    }
    else {
      errorMessage.value = t('modelPurpose.saveFailed')
    }
  }
  finally {
    savingPurpose.value = ''
  }
}
</script>

<template>
  <ElDialog v-model="visible" :title="$t('modelPurpose.title')" width="min(960px, calc(100vw - 32px))" append-to-body destroy-on-close>
    <p class="purpose-intro">
      {{ $t('modelPurpose.description') }}
    </p>
    <ElAlert v-if="errorMessage" :title="errorMessage" type="error" show-icon class="purpose-alert" />
    <ElTable v-loading="loading" :data="bindings" stripe border class="purpose-table">
      <ElTableColumn :label="$t('modelPurpose.purpose')" min-width="170">
        <template #default="{ row }">
          {{ purposeName(row) }}
        </template>
      </ElTableColumn>
      <ElTableColumn :label="$t('modelPurpose.currentModel')" min-width="190">
        <template #default="{ row }">
          <div v-if="row.modelId">
            <div>{{ row.modelName || row.modelCode || row.modelId }}</div>
            <small v-if="row.modelCode && row.modelName" class="secondary-text">{{ row.modelCode }}</small>
          </div>
          <span v-else class="secondary-text">{{ $t('modelPurpose.unbound') }}</span>
        </template>
      </ElTableColumn>
      <ElTableColumn :label="$t('modelPurpose.status')" width="105" align="center">
        <template #default="{ row }">
          <ElTag :type="statusType(row)" size="small">
            {{ statusLabel(row) }}
          </ElTag>
        </template>
      </ElTableColumn>
      <ElTableColumn :label="$t('modelPurpose.revision')" width="82" align="center">
        <template #default="{ row }">
          {{ row.revision }}
        </template>
      </ElTableColumn>
      <ElTableColumn :label="$t('modelPurpose.selectModel')" min-width="240">
        <template #default="{ row }">
          <ElSelect v-model="selectedModels[row.purposeCode]" :placeholder="$t('modelPurpose.selectPlaceholder')" filterable :disabled="loading || Boolean(savingPurpose)" class="model-select">
            <ElOption v-for="model in eligibleModels(row)" :key="model.id" :label="`${model.modelName} (${model.modelCode} · ${model.modelType})`" :value="model.id" />
          </ElSelect>
        </template>
      </ElTableColumn>
      <ElTableColumn :label="$t('modelPurpose.action')" width="95" align="center">
        <template #default="{ row }">
          <ElButton type="primary" size="small" :loading="savingPurpose === row.purposeCode" :disabled="loading || Boolean(savingPurpose) || !selectedModels[row.purposeCode] || selectedModels[row.purposeCode] === row.modelId" @click="saveBinding(row)">
            {{ $t('modelPurpose.save') }}
          </ElButton>
        </template>
      </ElTableColumn>
      <template #empty>
        {{ $t('modelPurpose.empty') }}
      </template>
    </ElTable>
    <template #footer>
      <ElButton :loading="loading" @click="loadBindings">
        {{ $t('modelPurpose.refresh') }}
      </ElButton>
      <ElButton @click="visible = false">
        {{ $t('modelPurpose.close') }}
      </ElButton>
    </template>
  </ElDialog>
</template>

<style scoped>
.purpose-intro {
  margin: 0 0 16px;
  color: var(--el-text-color-secondary);
}

.purpose-alert {
  margin-bottom: 16px;
}

.purpose-table {
  width: 100%;
}

.secondary-text {
  color: var(--el-text-color-secondary);
}

.model-select {
  width: 100%;
}
</style>
