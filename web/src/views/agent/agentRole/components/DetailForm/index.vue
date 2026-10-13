<script setup lang="ts">
import type { FormInstance, FormRules } from 'element-plus'
import { ElMessage } from 'element-plus'
import { useI18n } from 'vue-i18n'
import apiAgentRole from '@/api/modules/agent/agentRole'
import apiLlmModel from '@/api/modules/agent/llmModel'
import messages from '../lang'

export interface Props {
  id?: number | string
}

const props = withDefaults(defineProps<Props>(), {
  id: '',
})

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const l in messages) {
  mergeLocaleMessage(l, messages[l])
}

const loading = ref(false)
const formRef = useTemplateRef<FormInstance>('formRef')
const modelList = ref<any[]>([])

const form = ref({
  id: props.id,
  roleName: '',
  roleCode: '',
  description: '',
  avatarUrl: '',
  welcomeMessage: '',
  systemPrompt: '',
  modelID: null as number | null,
  modelParams: '{"temperature": 0.7, "topP": 0.9}',
  agentType: 'Standard',
  agentConfig: '{"maxSteps": 10, "enableReAct": true}',
  implementationClass: '',
  isEnabled: true,
  isPublic: false,
  version: 1,
})

const formRules = ref<FormRules>({
  roleName: [{ required: true, message: t('agentRole_formRules.roleName'), trigger: 'blur' }],
  roleCode: [{ required: true, message: t('agentRole_formRules.roleCode'), trigger: 'blur' }],
  agentType: [{ required: true, message: t('agentRole_formRules.agentType'), trigger: 'change' }],
})

const agentTypeOptions = [
  { label: t('agentRole_form.agentTypeStandard'), value: 'Standard', desc: t('agentRole_form.agentTypeStandardDesc') },
  { label: t('agentRole_form.agentTypeWorkflow'), value: 'Workflow', desc: t('agentRole_form.agentTypeWorkflowDesc') },
  { label: t('agentRole_form.agentTypeComposite'), value: 'Composite', desc: t('agentRole_form.agentTypeCompositeDesc') },
  { label: t('agentRole_form.agentTypeCode'), value: 'Code', desc: t('agentRole_form.agentTypeCodeDesc') },
]

onMounted(async () => {
  await loadModels()
  if (props.id !== '') {
    getInfo()
  }
})

async function loadModels() {
  try {
    const res = await apiLlmModel.listAll({ isEnabled: true })
    modelList.value = res || []
  }
  catch {
    modelList.value = []
  }
}

function getInfo() {
  loading.value = true
  apiAgentRole.detail(props.id).then((res: any) => {
    Object.assign(form.value, res)
  }).finally(() => {
    loading.value = false
  })
}

defineExpose({
  submit() {
    return new Promise<void>((resolve, reject) => {
      formRef.value?.validate((valid) => {
        if (valid) {
          const submitData = { ...form.value }
          if (props.id === '') {
            apiAgentRole.create(submitData).then(() => {
              ElMessage.success(t('agentRole_form.createSuccess'))
              resolve()
            }).catch(reject)
          }
          else {
            apiAgentRole.edit(props.id, submitData).then(() => {
              ElMessage.success(t('common.updateSuccess'))
              resolve()
            }).catch(reject)
          }
        }
        else {
          reject(new Error(t('agentRole_form.validateFailed')))
        }
      })
    })
  },
})
</script>

<template>
  <div v-loading="loading">
    <ElForm ref="formRef" :model="form" :rules="formRules" label-width="120px" label-suffix="：">
      <FaPageMain :title="$t('agentRole_form.baseInfo')">
        <ElRow :gutter="20">
          <ElCol :span="12">
            <ElFormItem :label="$t('agentRole_form.roleName')" prop="roleName">
              <ElInput v-model="form.roleName" :placeholder="$t('agentRole_form.roleNameExample')" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="12">
            <ElFormItem :label="$t('agentRole_form.roleCode')" prop="roleCode">
              <ElInput v-model="form.roleCode" :placeholder="$t('agentRole_form.roleCodePlaceholder')" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="12">
            <ElFormItem :label="$t('agentRole_form.agentType')" prop="agentType">
              <ElSelect v-model="form.agentType" :placeholder="$t('common.pleaseSelect')" style="width: 100%;">
                <ElOption v-for="item in agentTypeOptions" :key="item.value" :label="item.label" :value="item.value">
                  <div>
                    <div>{{ item.label }}</div>
                    <div class="text-xs text-gray-400">
                      {{ item.desc }}
                    </div>
                  </div>
                </ElOption>
              </ElSelect>
            </ElFormItem>
          </ElCol>
          <ElCol :span="12">
            <ElFormItem :label="$t('agentRole_form.relateModel')">
              <ElSelect v-model="form.modelID" :placeholder="$t('agentRole_form.modelPlaceholder')" clearable style="width: 100%;">
                <ElOption v-for="item in modelList" :key="item.id" :label="`${item.modelName} (${item.provider})`" :value="item.id" />
              </ElSelect>
            </ElFormItem>
          </ElCol>
          <ElCol :span="24">
            <ElFormItem :label="$t('agentRole_form.description')">
              <ElInput v-model="form.description" type="textarea" :rows="2" :placeholder="$t('agentRole_form.descriptionPlaceholder')" />
            </ElFormItem>
          </ElCol>
        </ElRow>
      </FaPageMain>

      <FaPageMain :title="$t('agentRole_form.promptConfig')">
        <ElRow :gutter="20">
          <ElCol :span="24">
            <ElFormItem :label="$t('agentRole_form.welcomeMessage')">
              <ElInput v-model="form.welcomeMessage" type="textarea" :rows="2" :placeholder="$t('agentRole_form.welcomeMessagePlaceholder')" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="24">
            <ElFormItem :label="$t('agentRole_form.systemPrompt')">
              <ElInput v-model="form.systemPrompt" type="textarea" :rows="8" :placeholder="$t('agentRole_form.systemPromptPlaceholder')" />
            </ElFormItem>
          </ElCol>
        </ElRow>
      </FaPageMain>

      <FaPageMain :title="$t('agentRole_form.advancedConfig')">
        <ElRow :gutter="20">
          <ElCol :span="24">
            <ElFormItem :label="$t('agentRole_form.modelParams')">
              <ElInput v-model="form.modelParams" type="textarea" :rows="3" placeholder='{"temperature": 0.7, "topP": 0.9}' />
            </ElFormItem>
          </ElCol>
          <ElCol :span="24">
            <ElFormItem :label="$t('agentRole_form.agentConfig')">
              <ElInput v-model="form.agentConfig" type="textarea" :rows="3" placeholder='{"maxSteps": 10, "enableReAct": true}' />
            </ElFormItem>
          </ElCol>
          <ElCol v-if="form.agentType === 'Code'" :span="24">
            <ElFormItem :label="$t('agentRole_form.implementationClass')">
              <ElInput v-model="form.implementationClass" :placeholder="$t('agentRole_form.implementationClassPlaceholder')" />
            </ElFormItem>
          </ElCol>
        </ElRow>
      </FaPageMain>

      <FaPageMain :title="$t('agentRole_form.statusConfig')">
        <ElRow :gutter="20">
          <ElCol :span="8">
            <ElFormItem :label="$t('agentRole_form.isEnabled')">
              <ElSwitch v-model="form.isEnabled" :active-text="$t('common.enbale')" :inactive-text="$t('common.disable')" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="8">
            <ElFormItem :label="$t('agentRole_form.isPublicLabel')">
              <ElSwitch v-model="form.isPublic" :active-text="$t('agentRole_form.publicText')" :inactive-text="$t('agentRole_form.privateText')" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="8">
            <ElFormItem :label="$t('agentRole_form.version')">
              <ElInputNumber v-model="form.version" :min="1" style="width: 100%;" />
            </ElFormItem>
          </ElCol>
        </ElRow>
      </FaPageMain>
    </ElForm>
  </div>
</template>
