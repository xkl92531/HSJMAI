<script setup lang="ts">
import { ElMessage } from 'element-plus'
import { marked } from 'marked'
import { useI18n } from 'vue-i18n'
import apiLlmModel from '@/api/modules/agent/llmModel'
import messages from '../lang'

const props = defineProps<{
  model: {
    id: number | string
    modelName: string
    modelCode: string
    provider?: string
    modelType?: string
  } | null
}>()

const visible = defineModel<boolean>({
  default: false,
})

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const loading = ref(false)
const inputMessage = ref('')
const chatHistory = ref<{ role: 'user' | 'assistant', content: string, images?: string[] }[]>([])
const systemPrompt = ref(t('llmModel_form.defaultSystemPrompt'))
const showSystemPrompt = ref(false)

// 图片附件
const imageFiles = ref<{ url: string, name: string }[]>([])
const fileInputRef = ref<HTMLInputElement | null>(null)

// 是否是VL多模态模型
const isVLModel = computed(() => props.model?.modelType === 'VL')

// 文件转Base64
function fileToBase64(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.readAsDataURL(file)
    reader.onload = () => resolve(reader.result as string)
    reader.onerror = reject
  })
}

// 处理图片选择
async function handleImageSelect(event: Event) {
  const input = event.target as HTMLInputElement
  if (!input.files?.length) return

  for (const file of Array.from(input.files)) {
    if (!file.type.startsWith('image/')) {
      ElMessage.warning(t('llmModel_form.selectImageFile'))
      continue
    }
    if (file.size > 20 * 1024 * 1024) {
      ElMessage.warning(t('llmModel_form.imageSizeLimit'))
      continue
    }

    const base64 = await fileToBase64(file)
    imageFiles.value.push({ url: base64, name: file.name })
  }

  // 清空input以便再次选择同一文件
  input.value = ''
}

// 移除图片
function removeImage(index: number) {
  imageFiles.value.splice(index, 1)
}

// 触发文件选择
function triggerImageSelect() {
  fileInputRef.value?.click()
}

// 发送消息
async function handleSend() {
  if (!inputMessage.value.trim() || !props.model) {
    return
  }

  const message = inputMessage.value.trim()
  const currentImages = [...imageFiles.value]
  inputMessage.value = ''
  imageFiles.value = []

  // 添加用户消息（包含图片预览）
  chatHistory.value.push({
    role: 'user',
    content: message,
    images: currentImages.map(img => img.url),
  })

  // 添加占位助手消息
  chatHistory.value.push({ role: 'assistant', content: t('llmModel_form.thinking') })

  loading.value = true
  try {
    const res: any = await apiLlmModel.quickTest(props.model.id, {
      message,
      systemPrompt: systemPrompt.value || undefined,
      imageUrls: currentImages.length > 0 ? currentImages.map(img => img.url) : undefined,
    })

    // 更新助手消息
    chatHistory.value[chatHistory.value.length - 1].content = res.backResult?.content || t('llmModel_form.noResponse')
  }
  catch (err: any) {
    chatHistory.value[chatHistory.value.length - 1].content = t('llmModel_form.errorMessage', { message: err.message || t('llmModel_form.requestFailed') })
    ElMessage.error(t('llmModel_form.testFailed'))
  }
  finally {
    loading.value = false
  }
}

// 清空对话
function handleClear() {
  chatHistory.value = []
  imageFiles.value = []
}

// 渲染 Markdown
function renderMarkdown(content: string) {
  if (!content) return ''
  try {
    return marked(content)
  }
  catch {
    return content
  }
}

// 关闭时清空
watch(visible, (val) => {
  if (!val) {
    chatHistory.value = []
    inputMessage.value = ''
    imageFiles.value = []
  }
})
</script>

<template>
  <ElDialog
    v-model="visible"
    :title="$t('llmModel_form.quickTestTitle', { name: model?.modelName || '' })"
    width="800px"
    :close-on-click-modal="false"
    destroy-on-close
  >
    <div class="quick-test-dialog">
      <!-- 系统提示设置 -->
      <div class="system-prompt-section">
        <div class="section-header" @click="showSystemPrompt = !showSystemPrompt">
          <span>{{ $t('llmModel_form.systemPromptLabel') }}</span>
          <ElIcon :class="{ 'rotate-180': showSystemPrompt }">
            <i-ep-arrow-down />
          </ElIcon>
        </div>
        <ElCollapseTransition>
          <div v-show="showSystemPrompt" class="system-prompt-input">
            <ElInput
              v-model="systemPrompt"
              type="textarea"
              :rows="3"
              :placeholder="$t('llmModel_form.systemPromptPh')"
            />
          </div>
        </ElCollapseTransition>
      </div>

      <!-- 对话区域 -->
      <div class="chat-container">
        <div v-if="chatHistory.length === 0" class="empty-state">
          <ElIcon size="48" color="#ccc"><i-ep-chat-dot-round /></ElIcon>
          <p>{{ $t('llmModel_form.emptyChatTip') }}</p>
          <p v-if="isVLModel" class="hint">{{ $t('llmModel_form.vlHint') }}</p>
        </div>

        <div v-for="(item, index) in chatHistory" :key="index" :class="['chat-message', item.role]">
          <div class="avatar">
            <ElAvatar :size="32" :style="{ background: item.role === 'user' ? 'var(--el-color-primary)' : 'var(--el-color-success)' }">
              {{ item.role === 'user' ? 'U' : 'A' }}
            </ElAvatar>
          </div>
          <div class="message-body">
            <!-- 用户消息中的图片 -->
            <div v-if="item.images?.length" class="message-images">
              <img v-for="(img, imgIdx) in item.images" :key="imgIdx" :src="img" class="preview-image" />
            </div>
            <div class="message-content" v-html="renderMarkdown(item.content)" />
          </div>
        </div>
      </div>

      <!-- 图片预览区域 (仅VL模型显示) -->
      <div v-if="isVLModel && imageFiles.length > 0" class="image-preview-section">
        <div v-for="(img, index) in imageFiles" :key="index" class="image-preview-item">
          <img :src="img.url" class="preview-thumb" />
          <span class="image-name">{{ img.name }}</span>
          <ElIcon class="remove-btn" @click="removeImage(index)"><i-ep-close /></ElIcon>
        </div>
      </div>

      <!-- 输入区域 -->
      <div class="input-section">
        <div class="input-row">
          <!-- VL模型显示图片上传按钮 -->
          <ElButton v-if="isVLModel" @click="triggerImageSelect" :disabled="loading" class="upload-btn">
            <ElIcon><i-ep-picture /></ElIcon>
          </ElButton>
          <input
            ref="fileInputRef"
            type="file"
            accept="image/*"
            multiple
            style="display: none"
            @change="handleImageSelect"
          />
          <ElInput
            v-model="inputMessage"
            type="textarea"
            :rows="2"
            :placeholder="isVLModel ? $t('llmModel_form.inputPlaceholderVL') : $t('llmModel_form.inputPlaceholder')"
            :disabled="loading"
            class="message-input"
            @keydown.enter.exact.prevent="handleSend"
          />
        </div>
        <div class="action-buttons">
          <ElButton @click="handleClear" :disabled="loading || chatHistory.length === 0">
            {{ $t('llmModel_form.clearChat') }}
          </ElButton>
          <ElButton type="primary" @click="handleSend" :loading="loading" :disabled="!inputMessage.trim()">
            {{ $t('llmModel_form.send') }}
          </ElButton>
        </div>
      </div>

      <!-- 模型信息 -->
      <div class="model-info">
        <ElTag size="small" type="info">{{ model?.provider }}</ElTag>
        <ElTag v-if="isVLModel" size="small" type="success">{{ $t('llmModel_form.multimodal') }}</ElTag>
        <span class="model-code">{{ model?.modelCode }}</span>
      </div>
    </div>
  </ElDialog>
</template>

<style scoped lang="scss">
.quick-test-dialog {
  display: flex;
  flex-direction: column;
  height: 550px;
}

.system-prompt-section {
  margin-bottom: 12px;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 6px;
  overflow: hidden;

  .section-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 10px 12px;
    cursor: pointer;
    background: var(--el-fill-color-light);
    font-size: 13px;
    color: var(--el-text-color-secondary);

    &:hover {
      background: var(--el-fill-color);
    }

    .el-icon {
      transition: transform 0.3s;
    }

    .rotate-180 {
      transform: rotate(180deg);
    }
  }

  .system-prompt-input {
    padding: 12px;
  }
}

.chat-container {
  flex: 1;
  overflow-y: auto;
  padding: 12px;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 6px;
  margin-bottom: 12px;
  background: var(--el-fill-color-lighter);

  .empty-state {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    height: 100%;
    color: var(--el-text-color-secondary);
    gap: 12px;

    .hint {
      font-size: 12px;
      color: var(--el-color-success);
    }
  }
}

.chat-message {
  display: flex;
  gap: 12px;
  margin-bottom: 16px;

  &.user {
    flex-direction: row-reverse;

    .message-body {
      align-items: flex-end;
    }

    .message-content {
      background: var(--el-color-primary-light-9);
      border-radius: 12px 0 12px 12px;
    }
  }

  &.assistant {
    .message-content {
      background: var(--el-bg-color);
      border-radius: 0 12px 12px 12px;
    }
  }

  .message-body {
    display: flex;
    flex-direction: column;
    max-width: 70%;
    gap: 8px;
  }

  .message-images {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;

    .preview-image {
      max-width: 150px;
      max-height: 150px;
      border-radius: 8px;
      object-fit: cover;
      border: 1px solid var(--el-border-color-lighter);
    }
  }

  .message-content {
    padding: 12px 16px;
    line-height: 1.6;
    border: 1px solid var(--el-border-color-lighter);

    :deep(p) {
      margin: 0;
    }

    :deep(pre) {
      background: var(--el-fill-color-darker);
      padding: 8px;
      border-radius: 4px;
      overflow-x: auto;
    }
  }
}

.image-preview-section {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-bottom: 12px;
  padding: 8px;
  background: var(--el-fill-color-lighter);
  border-radius: 6px;

  .image-preview-item {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 4px 8px;
    background: var(--el-bg-color);
    border-radius: 4px;
    border: 1px solid var(--el-border-color-lighter);

    .preview-thumb {
      width: 32px;
      height: 32px;
      object-fit: cover;
      border-radius: 4px;
    }

    .image-name {
      font-size: 12px;
      max-width: 100px;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .remove-btn {
      cursor: pointer;
      color: var(--el-text-color-secondary);

      &:hover {
        color: var(--el-color-danger);
      }
    }
  }
}

.input-section {
  display: flex;
  flex-direction: column;
  gap: 8px;

  .input-row {
    display: flex;
    gap: 8px;
    align-items: flex-start;

    .upload-btn {
      flex-shrink: 0;
      height: 52px;
    }

    .message-input {
      flex: 1;
    }
  }

  .action-buttons {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
  }
}

.model-info {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 8px;
  font-size: 12px;
  color: var(--el-text-color-secondary);

  .model-code {
    font-family: monospace;
  }
}
</style>
