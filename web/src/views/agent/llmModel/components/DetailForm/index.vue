<script setup lang="ts">
import type { FormInstance, FormRules } from 'element-plus'
import { ElMessage } from 'element-plus'
import { useI18n } from 'vue-i18n'
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
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const loading = ref(false)
const formRef = useTemplateRef<FormInstance>('formRef')

const form = ref({
  id: props.id,
  modelName: '',
  modelCode: '',
  modelType: 'Chat',
  provider: 'OpenAI',
  baseUrl: '',
  apiKey: '',
  contextWindow: 128000,
  maxTokens: 4096,
  dimension: null as number | null,
  costPerInputToken: null as number | null,
  costPerOutputToken: null as number | null,
  rateLimitRPM: null as number | null,
  rateLimitTPM: null as number | null,
  isLocal: false,
  supportStreaming: true,
  fallbackModelID: null as number | null,
  healthCheckUrl: '',
  isEnabled: true,
  description: '',
  customRequestParams: '',
})

const formRules = ref<FormRules>({
  modelName: [{ required: true, message: t('llmModel_formRules.modelName'), trigger: 'blur' }],
  modelCode: [{ required: true, message: t('llmModel_formRules.modelCode'), trigger: 'blur' }],
  modelType: [{ required: true, message: t('llmModel_formRules.modelType'), trigger: 'change' }],
  provider: [{ required: true, message: t('llmModel_formRules.provider'), trigger: 'change' }],
  baseUrl: [{ required: true, message: t('llmModel_formRules.baseUrl'), trigger: 'blur' }],
})

const modelTypeOptions = computed(() => [
  { label: t('llmModel_form.modelTypeChat'), value: 'Chat' },
  { label: t('llmModel_form.modelTypeVL'), value: 'VL' },
  { label: t('llmModel_form.modelTypeEmbedding'), value: 'Embedding' },
  { label: t('llmModel_form.modelTypeRerank'), value: 'Rerank' },
  { label: t('llmModel_form.modelTypeSTT'), value: 'STT' },
  { label: t('llmModel_form.modelTypeTTS'), value: 'TTS' },
  { label: t('llmModel_form.modelTypeImage'), value: 'Image' },
])

const providerOptions = [
  { label: 'OpenAI', value: 'OpenAI' },
  { label: 'Azure', value: 'Azure' },
  { label: 'DeepSeek', value: 'DeepSeek' },
  { label: 'Ollama', value: 'Ollama' },
  { label: 'Claude', value: 'Claude' },
  { label: 'Gemini', value: 'Gemini' },
]

// JSON 格式校验
const jsonError = ref('')

function validateJson(val: string) {
  if (!val || !val.trim()) {
    jsonError.value = ''
    return
  }
  try {
    JSON.parse(val)
    jsonError.value = ''
  }
  catch {
    jsonError.value = t('llmModel_form.jsonInvalid')
  }
}

function formatJson() {
  if (!form.value.customRequestParams?.trim()) return
  try {
    const obj = JSON.parse(form.value.customRequestParams)
    form.value.customRequestParams = JSON.stringify(obj, null, 2)
    jsonError.value = ''
  }
  catch {
    jsonError.value = t('llmModel_form.jsonFormatError')
  }
}

function insertTemplate() {
  const template = {
    stream: true,
    temperature: 0.7,
    chat_template_kwargs: {
      enable_thinking: false,
    },
  }
  form.value.customRequestParams = JSON.stringify(template, null, 2)
  jsonError.value = ''
}

onMounted(() => {
  if (props.id !== '') {
    getInfo()
  }
})

function getInfo() {
  loading.value = true
  apiLlmModel.detail(props.id).then((res: any) => {
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
            apiLlmModel.create(submitData).then(() => {
              ElMessage.success(t('llmModel_form.createSuccess'))
              resolve()
            }).catch(reject)
          }
          else {
            apiLlmModel.edit(props.id, submitData).then(() => {
              ElMessage.success(t('common.updateSuccess'))
              resolve()
            }).catch(reject)
          }
        }
        else {
          reject(new Error(t('llmModel_form.validateFailed')))
        }
      })
    })
  },
})
</script>

<template>
  <div v-loading="loading">
    <ElForm ref="formRef" :model="form" :rules="formRules" label-width="120px" label-suffix="：">
      <FaPageMain :title="$t('llmModel_form.baseInfo')">
        <ElRow :gutter="20">
          <ElCol :span="12">
            <ElFormItem :label="$t('llmModel_form.modelName')" prop="modelName">
              <ElInput v-model="form.modelName" :placeholder="$t('llmModel_form.modelNameExample')" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="12">
            <ElFormItem :label="$t('llmModel_form.modelCode')" prop="modelCode">
              <ElInput v-model="form.modelCode" :placeholder="$t('llmModel_form.modelCodeExample')" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="12">
            <ElFormItem :label="$t('llmModel_form.modelType')" prop="modelType">
              <ElSelect v-model="form.modelType" :placeholder="$t('common.pleaseSelect')" style="width: 100%;">
                <ElOption v-for="item in modelTypeOptions" :key="item.value" :label="item.label" :value="item.value" />
              </ElSelect>
            </ElFormItem>
          </ElCol>
          <ElCol :span="12">
            <ElFormItem :label="$t('llmModel_form.provider')" prop="provider">
              <ElSelect v-model="form.provider" :placeholder="$t('common.pleaseSelect')" style="width: 100%;">
                <ElOption v-for="item in providerOptions" :key="item.value" :label="item.label" :value="item.value" />
              </ElSelect>
            </ElFormItem>
          </ElCol>
          <ElCol :span="24">
            <ElFormItem :label="$t('llmModel_form.description')" prop="description">
              <ElInput v-model="form.description" type="textarea" :rows="2" :placeholder="$t('llmModel_form.descriptionPh')" />
            </ElFormItem>
          </ElCol>
        </ElRow>
      </FaPageMain>

      <FaPageMain :title="$t('llmModel_form.connConfig')">
        <ElRow :gutter="20">
          <ElCol :span="12">
            <ElFormItem :label="$t('llmModel_form.baseUrl')" prop="baseUrl">
              <ElInput v-model="form.baseUrl" :placeholder="$t('llmModel_form.baseUrlPh')" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="12">
            <ElFormItem :label="$t('llmModel_form.apiKey')" prop="apiKey">
              <ElInput v-model="form.apiKey" type="password" show-password :placeholder="$t('llmModel_form.apiKeyPh')" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="12">
            <ElFormItem :label="$t('llmModel_form.healthCheckUrl')">
              <ElInput v-model="form.healthCheckUrl" :placeholder="$t('llmModel_form.healthCheckUrlPh')" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="12">
            <ElFormItem :label="$t('llmModel_form.isLocal')">
              <ElSwitch v-model="form.isLocal" />
            </ElFormItem>
          </ElCol>
        </ElRow>
      </FaPageMain>

      <FaPageMain :title="$t('llmModel_form.capabilityConfig')">
        <ElRow :gutter="20">
          <ElCol :span="8">
            <ElFormItem :label="$t('llmModel_form.contextWindow')">
              <ElInputNumber v-model="form.contextWindow" :min="1000" :max="2000000" style="width: 100%;" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="8">
            <ElFormItem :label="$t('llmModel_form.maxTokens')">
              <ElInputNumber v-model="form.maxTokens" :min="100" :max="100000" style="width: 100%;" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="8">
            <ElFormItem :label="$t('llmModel_form.dimension')">
              <ElInputNumber v-model="form.dimension" :min="1" :placeholder="$t('llmModel_form.dimensionPh')" style="width: 100%;" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="8">
            <ElFormItem :label="$t('llmModel_form.rateLimitRPM')">
              <ElInputNumber v-model="form.rateLimitRPM" :min="1" :placeholder="$t('llmModel_form.rpmPh')" style="width: 100%;" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="8">
            <ElFormItem :label="$t('llmModel_form.rateLimitTPM')">
              <ElInputNumber v-model="form.rateLimitTPM" :min="1" :placeholder="$t('llmModel_form.tpmPh')" style="width: 100%;" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="8">
            <ElFormItem :label="$t('llmModel_form.supportStreaming')">
              <ElSwitch v-model="form.supportStreaming" />
            </ElFormItem>
          </ElCol>
        </ElRow>
      </FaPageMain>

      <FaPageMain :title="$t('llmModel_form.costConfig')">
        <ElRow :gutter="20">
          <ElCol :span="12">
            <ElFormItem :label="$t('llmModel_form.costPerInputToken')">
              <ElInputNumber v-model="form.costPerInputToken" :precision="6" :min="0" :placeholder="$t('llmModel_form.costPh')" style="width: 100%;" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="12">
            <ElFormItem :label="$t('llmModel_form.costPerOutputToken')">
              <ElInputNumber v-model="form.costPerOutputToken" :precision="6" :min="0" :placeholder="$t('llmModel_form.costPh')" style="width: 100%;" />
            </ElFormItem>
          </ElCol>
        </ElRow>
      </FaPageMain>

      <FaPageMain :title="$t('llmModel_form.customRequestParamsTitle')">
        <div style="margin-bottom: 12px; color: var(--el-text-color-secondary); font-size: 13px;">
          {{ $t('llmModel_form.customParamsTip') }}
        </div>
        <ElRow :gutter="20">
          <ElCol :span="24">
            <ElFormItem :label="$t('llmModel_form.requestParams')" prop="customRequestParams">
              <ElInput
                v-model="form.customRequestParams"
                type="textarea"
                :rows="8"
                placeholder='{&#10;  "stream": true,&#10;  "temperature": 0.7,&#10;  "chat_template_kwargs": {&#10;    "enable_thinking": false&#10;  }&#10;}'
                style="font-family: monospace;"
                @blur="validateJson(form.customRequestParams)"
              />
              <div style="display: flex; align-items: center; gap: 8px; margin-top: 8px;">
                <ElButton size="small" @click="formatJson">
                  {{ $t('llmModel_form.formatJson') }}
                </ElButton>
                <ElButton size="small" type="primary" plain @click="insertTemplate">
                  {{ $t('llmModel_form.insertTemplate') }}
                </ElButton>
                <span v-if="jsonError" style="color: var(--el-color-danger); font-size: 12px;">{{ jsonError }}</span>
                <span v-else-if="form.customRequestParams?.trim()" style="color: var(--el-color-success); font-size: 12px;">{{ $t('llmModel_form.jsonValid') }}</span>
              </div>
            </ElFormItem>
          </ElCol>
        </ElRow>
      </FaPageMain>

      <FaPageMain :title="$t('llmModel_form.statusConfig')">
        <ElRow :gutter="20">
          <ElCol :span="12">
            <ElFormItem :label="$t('llmModel_form.isEnabled')">
              <ElSwitch v-model="form.isEnabled" :active-text="$t('common.enbale')" :inactive-text="$t('common.disable')" />
            </ElFormItem>
          </ElCol>
        </ElRow>
      </FaPageMain>
    </ElForm>
  </div>
</template>
