<script setup lang="ts">
import type { FormInstance, FormRules } from 'element-plus'
import { ElMessage } from 'element-plus'
import { useI18n } from 'vue-i18n'
import apiToolDefinition from '@/api/modules/agent/toolDefinition'
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
  toolName: '',
  toolCode: '',
  description: '',
  toolType: 'Local',
  // Http配置
  apiUrl: '',
  apiMethod: 'POST',
  apiHeaders: '{}',
  apiBodyTemplate: '{}',
  // MCP配置
  mcpServerID: null as number | null,
  mcpToolName: '',
  // Local配置
  localAssembly: '',
  localClass: '',
  localMethod: '',
  // Schema配置
  parametersSchema: '{}',
  responseSchema: '{}',
  // 错误处理
  errorHandlingStrategy: 'Retry',
  fallbackToolID: null as number | null,
  timeoutSeconds: 30,
  // 鉴权
  authType: 0,
  authConfig: '{}',
  // 其他
  requiresConfirmation: false,
  isEnabled: true,
})

const formRules = ref<FormRules>({
  toolName: [{ required: true, message: t('common.pleaseInput') + t('tool_form.toolName'), trigger: 'blur' }],
  toolCode: [{ required: true, message: t('common.pleaseInput') + t('tool_form.toolCode'), trigger: 'blur' }],
  toolType: [{ required: true, message: t('common.pleaseSelect') + t('tool_form.toolType'), trigger: 'change' }],
  description: [{ required: true, message: t('common.pleaseInput') + t('tool_form.toolDescription'), trigger: 'blur' }],
})

const toolTypeOptions = computed(() => [
  { label: t('tool_form.toolTypeLocal'), value: 'Local', desc: t('tool_form.toolTypeLocalDesc') },
  { label: t('tool_form.toolTypeHttp'), value: 'Http', desc: t('tool_form.toolTypeHttpDesc') },
  { label: t('tool_form.toolTypeMcp'), value: 'Mcp', desc: t('tool_form.toolTypeMcpDesc') },
])

const apiMethodOptions = ['GET', 'POST', 'PUT', 'DELETE']

const errorHandlingOptions = computed(() => [
  { label: t('tool_form.errorAbort'), value: 'Abort' },
  { label: t('tool_form.errorRetry'), value: 'Retry' },
  { label: t('tool_form.errorFallback'), value: 'Fallback' },
])

const authTypeOptions = computed(() => [
  { label: t('tool_form.authNone'), value: 0 },
  { label: 'API Key', value: 1 },
  { label: 'OAuth', value: 2 },
])

onMounted(() => {
  if (props.id !== '') {
    getInfo()
  }
})

function getInfo() {
  loading.value = true
  apiToolDefinition.detail(props.id).then((res: any) => {
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
            apiToolDefinition.create(submitData).then(() => {
              ElMessage.success(t('tool_form.createSuccess'))
              resolve()
            }).catch(reject)
          }
          else {
            apiToolDefinition.edit(props.id, submitData).then(() => {
              ElMessage.success(t('common.updateSuccess'))
              resolve()
            }).catch(reject)
          }
        }
        else {
          reject(new Error(t('tool_form.formValidateFailed')))
        }
      })
    })
  },
})
</script>

<template>
  <div v-loading="loading">
    <ElForm ref="formRef" :model="form" :rules="formRules" label-width="120px" label-suffix="：">
      <FaPageMain :title="$t('tool_form.baseInfo')">
        <ElRow :gutter="20">
          <ElCol :span="12">
            <ElFormItem :label="$t('tool_form.toolName')" prop="toolName">
              <ElInput v-model="form.toolName" :placeholder="$t('tool_form.toolNamePlaceholder')" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="12">
            <ElFormItem :label="$t('tool_form.toolCode')" prop="toolCode">
              <ElInput v-model="form.toolCode" :placeholder="$t('tool_form.toolCodePlaceholder')" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="12">
            <ElFormItem :label="$t('tool_form.toolType')" prop="toolType">
              <ElSelect v-model="form.toolType" :placeholder="$t('common.pleaseSelect')" style="width: 100%;">
                <ElOption v-for="item in toolTypeOptions" :key="item.value" :label="item.label" :value="item.value">
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
            <ElFormItem :label="$t('tool_form.timeoutSeconds')">
              <ElInputNumber v-model="form.timeoutSeconds" :min="1" :max="600" style="width: 100%;" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="24">
            <ElFormItem :label="$t('tool_form.toolDescription')" prop="description">
              <ElInput v-model="form.description" type="textarea" :rows="3" :placeholder="$t('tool_form.toolDescriptionPlaceholder')" />
            </ElFormItem>
          </ElCol>
        </ElRow>
      </FaPageMain>

      <!-- Http配置 -->
      <FaPageMain v-if="form.toolType === 'Http'" :title="$t('tool_form.httpConfig')">
        <ElRow :gutter="20">
          <ElCol :span="18">
            <ElFormItem :label="$t('tool_form.apiUrl')">
              <ElInput v-model="form.apiUrl" :placeholder="$t('tool_form.apiUrlPlaceholder')" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="6">
            <ElFormItem :label="$t('tool_form.requestMethod')">
              <ElSelect v-model="form.apiMethod" style="width: 100%;">
                <ElOption v-for="item in apiMethodOptions" :key="item" :label="item" :value="item" />
              </ElSelect>
            </ElFormItem>
          </ElCol>
          <ElCol :span="24">
            <ElFormItem :label="$t('tool_form.requestHeaders')">
              <ElInput v-model="form.apiHeaders" type="textarea" :rows="2" placeholder='{"Content-Type": "application/json"}' />
            </ElFormItem>
          </ElCol>
          <ElCol :span="24">
            <ElFormItem :label="$t('tool_form.requestBodyTemplate')">
              <ElInput v-model="form.apiBodyTemplate" type="textarea" :rows="3" placeholder='{"param1": "{{value1}}", "param2": "{{value2}}"}' />
            </ElFormItem>
          </ElCol>
        </ElRow>
      </FaPageMain>

      <!-- Local配置 -->
      <FaPageMain v-if="form.toolType === 'Local'" :title="$t('tool_form.localConfig')">
        <ElRow :gutter="20">
          <ElCol :span="24">
            <ElFormItem :label="$t('tool_form.assembly')">
              <ElInput v-model="form.localAssembly" :placeholder="$t('tool_form.assemblyPlaceholder')" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="12">
            <ElFormItem :label="$t('tool_form.className')">
              <ElInput v-model="form.localClass" :placeholder="$t('tool_form.classPlaceholder')" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="12">
            <ElFormItem :label="$t('tool_form.methodName')">
              <ElInput v-model="form.localMethod" :placeholder="$t('tool_form.methodPlaceholder')" />
            </ElFormItem>
          </ElCol>
        </ElRow>
      </FaPageMain>

      <!-- MCP配置 -->
      <FaPageMain v-if="form.toolType === 'Mcp'" :title="$t('tool_form.mcpConfig')">
        <ElRow :gutter="20">
          <ElCol :span="12">
            <ElFormItem :label="$t('tool_form.mcpServerId')">
              <ElInputNumber v-model="form.mcpServerID" :placeholder="$t('tool_form.mcpServerIdPlaceholder')" style="width: 100%;" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="12">
            <ElFormItem :label="$t('tool_form.mcpToolName')">
              <ElInput v-model="form.mcpToolName" :placeholder="$t('tool_form.mcpToolNamePlaceholder')" />
            </ElFormItem>
          </ElCol>
        </ElRow>
      </FaPageMain>

      <FaPageMain :title="$t('tool_form.schemaConfig')">
        <ElRow :gutter="20">
          <ElCol :span="24">
            <ElFormItem :label="$t('tool_form.parametersSchema')">
              <ElInput v-model="form.parametersSchema" type="textarea" :rows="4" :placeholder="$t('tool_form.parametersSchemaPlaceholder')" />
            </ElFormItem>
          </ElCol>
          <ElCol :span="24">
            <ElFormItem :label="$t('tool_form.responseSchema')">
              <ElInput v-model="form.responseSchema" type="textarea" :rows="4" :placeholder="$t('tool_form.responseSchemaPlaceholder')" />
            </ElFormItem>
          </ElCol>
        </ElRow>
      </FaPageMain>

      <FaPageMain :title="$t('tool_form.errorAndAuth')">
        <ElRow :gutter="20">
          <ElCol :span="8">
            <ElFormItem :label="$t('tool_form.errorStrategy')">
              <ElSelect v-model="form.errorHandlingStrategy" style="width: 100%;">
                <ElOption v-for="item in errorHandlingOptions" :key="item.value" :label="item.label" :value="item.value" />
              </ElSelect>
            </ElFormItem>
          </ElCol>
          <ElCol :span="8">
            <ElFormItem :label="$t('tool_form.authType')">
              <ElSelect v-model="form.authType" style="width: 100%;">
                <ElOption v-for="item in authTypeOptions" :key="item.value" :label="item.label" :value="item.value" />
              </ElSelect>
            </ElFormItem>
          </ElCol>
          <ElCol :span="8">
            <ElFormItem :label="$t('tool_form.requiresConfirmation')">
              <ElSwitch v-model="form.requiresConfirmation" :active-text="$t('common.yes')" :inactive-text="$t('common.no')" />
            </ElFormItem>
          </ElCol>
          <ElCol v-if="form.authType > 0" :span="24">
            <ElFormItem :label="$t('tool_form.authConfig')">
              <ElInput v-model="form.authConfig" type="textarea" :rows="2" placeholder='{"apiKey": "xxx"}' />
            </ElFormItem>
          </ElCol>
        </ElRow>
      </FaPageMain>

      <FaPageMain :title="$t('tool_form.statusConfig')">
        <ElRow :gutter="20">
          <ElCol :span="12">
            <ElFormItem :label="$t('tool_form.isEnabled')">
              <ElSwitch v-model="form.isEnabled" :active-text="$t('common.enbale')" :inactive-text="$t('common.disable')" />
            </ElFormItem>
          </ElCol>
        </ElRow>
      </FaPageMain>
    </ElForm>
  </div>
</template>
