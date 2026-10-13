<i18n lang="json">
{
  "zh-cn": {
    "tips": "使用屏幕截图能帮助我们更准确地了解问题",
    "description": "请描述您的问题或建议",
    "rules": {
      "description": "请描述您的问题或建议"
    },
    "submit": "提交"
  },
  "zh-tw": {
    "tips": "使用屏幕截圖能幫助我們更準確地了解問題",
    "description": "請描述您的问题或建议",
    "rules": {
      "description": "請描述您的问题或建议"
    },
    "submit": "提交"
  },
  "en": {
    "tips": "Using screenshots can help us understand the problem more accurately",
    "description": "Please describe your problem or suggestion",
    "rules": {
      "description": "Please describe your problem or suggestion"
    },
    "submit": "Submit"
  },
  "ja": {
    "tips": "スクリーンショットを添付すると、問題をより正確に把握できます",
    "description": "問題やご意見をご記入ください",
    "rules": {
      "description": "問題やご意見をご記入してください"
    },
    "submit": "送信"
  }
}
</i18n>

<script setup lang="ts">
import { toTypedSchema } from '@vee-validate/zod'
import { snapdom } from '@zumer/snapdom'
import { useForm } from 'vee-validate'
import { toast } from 'vue-sonner'
import * as z from 'zod'
import { FormControl, FormField, FormItem, FormMessage } from '@/ui/shadcn/ui/form'
import FeedbackCanvas from './feedback-canvas.vue'

const emits = defineEmits<{
  close: []
}>()

const { t } = useI18n()

const canvasBase64 = ref('')
const isLoading = ref(false)
async function onGenerate() {
  if (isLoading.value) {
    return
  }
  isLoading.value = true
  const result = await snapdom(document.getElementById('app-content')!)
  canvasBase64.value = await result.toRaw()
  isLoading.value = false
}

const form = useForm({
  validationSchema: toTypedSchema(z.object({
    description: z.string().min(1, t('rules.description')),
  })),
  initialValues: {
    description: '',
  },
})
const onSubmit = form.handleSubmit((values) => {
  const result: Record<string, any> = {
    ...values,
  }
  if (canvasBase64.value) {
    result.canvasBase64 = canvasBase64.value
  }
  // eslint-disable-next-line no-console
  console.log(result)
  emits('close')
  toast.success('模拟提交成功')
})

defineExpose({
  onSubmit,
})
</script>

<template>
  <div class="flex flex-col gap-4 pb-2">
    <FeedbackCanvas v-if="canvasBase64" v-model="canvasBase64" />
    <div
      v-else class="relative flex-col-center cursor-pointer gap-2 border-2 rounded-lg border-dashed p-12 transition-all" :class="{
        'hover:border-muted-foreground': !isLoading,
        'opacity-50': isLoading,
      }" @click="onGenerate"
    >
      <FaIcon name="ri:screenshot-2-line" class="size-20 text-muted-foreground opacity-50" />
      <div class="text-sm text-muted-foreground">
        {{ t('tips') }}
      </div>
      <div v-if="isLoading" class="absolute inset-b-4 flex-col-center">
        <FaIcon name="i-line-md:loading-twotone-loop" class="size-6 text-muted-foreground" />
      </div>
    </div>
    <form @submit="onSubmit">
      <FormField v-slot="{ componentField, errors }" name="description">
        <FormItem class="relative pb-6 space-y-0">
          <FormControl>
            <FaTextarea v-bind="componentField" class="w-full" :class="errors.length && 'border-destructive'" :placeholder="t('description')" />
          </FormControl>
          <Transition enter-active-class="transition-opacity" enter-from-class="opacity-0" leave-active-class="transition-opacity" leave-to-class="opacity-0">
            <FormMessage class="absolute bottom-1 text-xs" />
          </Transition>
        </FormItem>
      </FormField>
      <FaButton class="w-full" @click="onSubmit">
        {{ t('submit') }}
      </FaButton>
    </form>
  </div>
</template>
