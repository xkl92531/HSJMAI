<script setup lang="ts">
import type { FormInstance, FormRules } from 'element-plus'
import { ElMessage } from 'element-plus'
import { useI18n } from 'vue-i18n'
import apiSkillDefinition from '@/api/modules/agent/skillDefinition'
import messages from '../lang'

export interface Props {
  id?: number | string
}

const props = withDefaults(defineProps<Props>(), {
  id: '',
})

const { t, mergeLocaleMessage } = useI18n()
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const loading = ref(false)
const formRef = useTemplateRef<FormInstance>('formRef')

const form = ref({
  id: props.id,
  skillName: '',
  skillCode: '',
  category: 'Analysis',
  complexityLevel: 'Intermediate',
  description: '',
  inputSchema: '{}',
  outputSchema: '{}',
  implementationType: 'Local',
  implementationRef: '',
  toolID: null as number | null,
  isComposite: false,
  prerequisiteSkills: '[]',
  composedSkills: '[]',
  compositionLogic: '{}',
  requiresConfirmation: false,
  safetyLevel: 2,
  isEnabled: true,
  version: 1,
  isAgentSkill: false,
  skillPackagePath: '',
  approvalStatus: 'Approved',
  applicableBrand: '',
  applicableFileFormat: '',
})

const formRules = ref<FormRules>({
  skillName: [{ required: true, message: t('common.pleaseInput') + t('skill_form.skillName'), trigger: 'blur' }],
  skillCode: [{ required: true, message: t('common.pleaseInput') + t('skill_form.skillCode'), trigger: 'blur' }],
  category: [{ required: true, message: t('common.pleaseSelect') + t('skill_form.category'), trigger: 'change' }],
  description: [{ required: true, message: t('common.pleaseInput') + t('skill_form.skillDescription'), trigger: 'blur' }],
})

const categoryOptions = computed(() => [
  { label: t('skill_form.categoryPerception'), value: 'Perception', desc: t('skill_form.categoryPerceptionDesc') },
  { label: t('skill_form.categoryAnalysis'), value: 'Analysis', desc: t('skill_form.categoryAnalysisDesc') },
  { label: t('skill_form.categoryDecision'), value: 'Decision', desc: t('skill_form.categoryDecisionDesc') },
  { label: t('skill_form.categoryExecution'), value: 'Execution', desc: t('skill_form.categoryExecutionDesc') },
  { label: t('skill_form.categoryCollaboration'), value: 'Collaboration', desc: t('skill_form.categoryCollaborationDesc') },
])

const complexityOptions = computed(() => [
  { label: t('skill_form.complexityBasic'), value: 'Basic' },
  { label: t('skill_form.complexityIntermediate'), value: 'Intermediate' },
  { label: t('skill_form.complexityAdvanced'), value: 'Advanced' },
  { label: t('skill_form.complexityExpert'), value: 'Expert' },
])

const implementationTypeOptions = computed(() => [
  { label: t('skill_form.implLocal'), value: 'Local' },
  { label: t('skill_form.implHttp'), value: 'HTTP' },
  { label: t('skill_form.implMcp'), value: 'MCP' },
  { label: t('skill_form.implComposite'), value: 'Composite' },
])

const approvalStatusOptions = computed(() => [
  { label: t('skill_form.statusPending'), value: 'Pending' },
  { label: t('skill_form.statusApproved'), value: 'Approved' },
  { label: t('skill_form.statusRejected'), value: 'Rejected' },
])

const safetyLevelOptions = computed(() => [
  { label: t('skill_form.safetyLevel1'), value: 1 },
  { label: t('skill_form.safetyLevel2'), value: 2 },
  { label: t('skill_form.safetyLevel3'), value: 3 },
  { label: t('skill_form.safetyLevel4'), value: 4 },
])

onMounted(() => {
  if (props.id !== '') {
    getInfo()
  }
})

function getInfo() {
  loading.value = true
  apiSkillDefinition.detail(props.id).then((res: any) => {
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
            apiSkillDefinition.create(submitData).then(() => {
              ElMessage.success(t('skill_form.createSuccess'))
              resolve()
            }).catch(reject)
          }
          else {
            apiSkillDefinition.edit(props.id, submitData).then(() => {
              ElMessage.success(t('common.updateSuccess'))
              resolve()
            }).catch(reject)
          }
        }
        else {
          reject(new Error(t('skill_form.formValidateFailed')))
        }
      })
    })
  },
})
</script>

<template>
  <div v-loading="loading">
    <ElForm ref="formRef" :model="form" :rules="formRules" label-width="120px" label-suffix="：">
      <FaPageMain :title="$t('skill_form.baseInfo')">
        <ElRow :gutter="20">
          <ElCol :span="12">
            <ElFormItem :label="$t('skill_form.skillName')" prop="skillName">
              <ElInput v-model="form.skillName" :placeholder="$t('skill_form.skillNamePlaceholder')" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="12">
            <ElFormItem :label="$t('skill_form.skillCode')" prop="skillCode">
              <ElInput v-model="form.skillCode" :placeholder="$t('skill_form.skillCodePlaceholder')" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="12">
            <ElFormItem :label="$t('skill_form.category')" prop="category">
              <ElSelect v-model="form.category" :placeholder="$t('common.pleaseSelect')" style="width: 100%;">
                <ElOption v-for="item in categoryOptions" :key="item.value" :label="item.label" :value="item.value">
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
            <ElFormItem :label="$t('skill_form.complexity')">
              <ElSelect v-model="form.complexityLevel" :placeholder="$t('common.pleaseSelect')" style="width: 100%;">
                <ElOption v-for="item in complexityOptions" :key="item.value" :label="item.label" :value="item.value" />
              </ElSelect>
            </ElFormItem>
          </ElCol>
          <ElCol :span="24">
            <ElFormItem :label="$t('skill_form.skillDescription')" prop="description">
              <ElInput v-model="form.description" type="textarea" :rows="3" :placeholder="$t('skill_form.skillDescriptionPlaceholder')" />
            </ElFormItem>
          </ElCol>
        </ElRow>
      </FaPageMain>

      <FaPageMain :title="$t('skill_form.implConfig')">
        <ElRow :gutter="20">
          <ElCol :span="12">
            <ElFormItem :label="$t('skill_form.implementationType')">
              <ElSelect v-model="form.implementationType" :placeholder="$t('common.pleaseSelect')" style="width: 100%;">
                <ElOption v-for="item in implementationTypeOptions" :key="item.value" :label="item.label" :value="item.value" />
              </ElSelect>
            </ElFormItem>
          </ElCol>
          <ElCol :span="12">
            <ElFormItem :label="$t('skill_form.toolId')">
              <ElInputNumber v-model="form.toolID" :placeholder="$t('skill_form.toolIdPlaceholder')" style="width: 100%;" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="24">
            <ElFormItem :label="$t('skill_form.implementationRef')">
              <ElInput v-model="form.implementationRef" :placeholder="$t('skill_form.implementationRefPlaceholder')" />
            </ElFormItem>
          </ElCol>
        </ElRow>
      </FaPageMain>

      <FaPageMain :title="$t('skill_form.agentSkillConfig')">
        <ElRow :gutter="20">
          <ElCol :span="8">
            <ElFormItem label="Agent Skill">
              <ElSwitch v-model="form.isAgentSkill" :active-text="$t('common.yes')" :inactive-text="$t('common.no')" />
            </ElFormItem>
          </ElCol>
          <ElCol v-if="form.isAgentSkill" :span="16">
            <ElFormItem :label="$t('skill_form.skillPackagePath')">
              <ElInput v-model="form.skillPackagePath" :placeholder="$t('skill_form.skillPackagePathPlaceholder')" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="12">
            <ElFormItem :label="$t('skill_form.applicableBrand')">
              <ElInput v-model="form.applicableBrand" :placeholder="$t('skill_form.applicableBrandPlaceholder')" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="12">
            <ElFormItem :label="$t('skill_form.applicableFileFormat')">
              <ElInput v-model="form.applicableFileFormat" :placeholder="$t('skill_form.applicableFileFormatPlaceholder')" />
            </ElFormItem>
          </ElCol>
        </ElRow>
      </FaPageMain>

      <FaPageMain :title="$t('skill_form.schemaConfig')">
        <ElRow :gutter="20">
          <ElCol :span="24">
            <ElFormItem :label="$t('skill_form.inputSchema')">
              <ElInput v-model="form.inputSchema" type="textarea" :rows="3" :placeholder="$t('skill_form.inputSchemaPlaceholder')" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="24">
            <ElFormItem :label="$t('skill_form.outputSchema')">
              <ElInput v-model="form.outputSchema" type="textarea" :rows="3" :placeholder="$t('skill_form.outputSchemaPlaceholder')" />
            </ElFormItem>
          </ElCol>
        </ElRow>
      </FaPageMain>

      <FaPageMain :title="$t('skill_form.safetyAndStatus')">
        <ElRow :gutter="20">
          <ElCol :span="8">
            <ElFormItem :label="$t('skill_form.safetyLevel')">
              <ElSelect v-model="form.safetyLevel" style="width: 100%;">
                <ElOption v-for="item in safetyLevelOptions" :key="item.value" :label="item.label" :value="item.value" />
              </ElSelect>
            </ElFormItem>
          </ElCol>
          <ElCol :span="8">
            <ElFormItem :label="$t('skill_form.approvalStatus')">
              <ElSelect v-model="form.approvalStatus" style="width: 100%;">
                <ElOption v-for="item in approvalStatusOptions" :key="item.value" :label="item.label" :value="item.value" />
              </ElSelect>
            </ElFormItem>
          </ElCol>
          <ElCol :span="8">
            <ElFormItem :label="$t('skill_form.requiresConfirmation')">
              <ElSwitch v-model="form.requiresConfirmation" :active-text="$t('common.yes')" :inactive-text="$t('common.no')" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="8">
            <ElFormItem :label="$t('skill_form.isEnabled')">
              <ElSwitch v-model="form.isEnabled" :active-text="$t('common.enbale')" :inactive-text="$t('common.disable')" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="8">
            <ElFormItem :label="$t('skill_form.version')">
              <ElInputNumber v-model="form.version" :min="1" style="width: 100%;" />
            </ElFormItem>
          </ElCol>
        </ElRow>
      </FaPageMain>
    </ElForm>
  </div>
</template>
