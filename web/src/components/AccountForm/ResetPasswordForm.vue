<i18n lang="json">
{
  "zh-cn": {
    "intro": "忘记密码了? 🔒",
    "login": "去登录",
    "form": {
      "account": "用户名",
      "captcha": "验证码",
      "sendCaptcha": "发送验证码",
      "captchaCountdown": "{0} 秒后可重新发送",
      "newPassword": "新密码",
      "confirm": "确认"
    },
    "rules": {
      "account": "请输入用户名",
      "captcha": "请输入验证码",
      "newPassword": "请输入新密码",
      "newPasswordLength": "密码长度为6到18位"
    }
  },
  "zh-tw": {
    "intro": "忘記密碼了? 🔒",
    "login": "去登錄",
    "form": {
      "account": "用戶名",
      "captcha": "驗證碼",
      "sendCaptcha": "發送驗證碼",
      "captchaCountdown": "{0} 秒後可重新發送",
      "newPassword": "新密碼",
      "confirm": "確認"
    },
    "rules": {
      "account": "請輸入用戶名",
      "captcha": "請輸入驗證碼",
      "newPassword": "請輸入新密碼",
      "newPasswordLength": "密碼長度為6到18位"
    }
  },
  "en": {
    "intro": "Forget password? 🔒",
    "login": "Go to login",
    "form": {
      "account": "Account",
      "captcha": "Captcha",
      "sendCaptcha": "Send Captcha",
      "captchaCountdown": "Can resend in {0}s",
      "newPassword": "New Password",
      "confirm": "Confirm"
    },
    "rules": {
      "account": "Please enter account",
      "captcha": "Please enter captcha",
      "newPassword": "Please enter new password",
      "newPasswordLength": "The length of the password is 6 to 18 bits"
    }
  },
  "ja": {
    "intro": "パスワードをお忘れですか？ 🔒",
    "login": "ログインへ",
    "form": {
      "account": "ユーザー名",
      "captcha": "認証コード",
      "sendCaptcha": "認証コードを送信",
      "captchaCountdown": "{0} 秒後に再送信できます",
      "newPassword": "新しいパスワード",
      "confirm": "確認"
    },
    "rules": {
      "account": "ユーザー名を入力してください",
      "captcha": "認証コードを入力してください",
      "newPassword": "新しいパスワードを入力してください",
      "newPasswordLength": "パスワードは6～18文字で入力してください"
    }
  }
}
</i18n>

<script setup lang="ts">
import { toTypedSchema } from '@vee-validate/zod'
import { useForm } from 'vee-validate'
import * as z from 'zod'
import { FormControl, FormField, FormItem, FormMessage } from '@/ui/shadcn/ui/form'

defineOptions({
  name: 'ResetPasswordForm',
})

const props = defineProps<{
  account?: string
}>()

const emits = defineEmits<{
  onLogin: [account?: string]
  onResetPassword: [account?: string]
}>()

const { t } = useI18n()

const loading = ref(false)

const form = useForm({
  validationSchema: toTypedSchema(z.object({
    account: z.string().min(1, t('rules.account')),
    captcha: z.string().min(6, t('rules.captcha')),
    newPassword: z.string().min(1, t('rules.newPassword')).min(6, t('rules.newPasswordLength')).max(18, t('rules.newPasswordLength')),
  })),
  initialValues: {
    account: props.account ?? '',
    captcha: '',
    newPassword: '',
  },
})
const onSubmit = form.handleSubmit((values) => {
  loading.value = true
  emits('onResetPassword', values.account)
})

const countdown = ref(0)
const countdownInterval = ref(Number.NaN)
function handleSendCaptcha() {
  countdown.value = 60
  countdownInterval.value = window.setInterval(() => {
    countdown.value--
    if (countdown.value === 0) {
      clearInterval(countdownInterval.value)
    }
  }, 1000)
}
</script>

<template>
  <div class="min-h-500px w-full flex-col-stretch-center p-12">
    <form @submit="onSubmit">
      <FaBlurReveal :delay="0.2" :duration="0.4" class="mb-8 space-y-2">
        <h3 class="text-4xl color-[var(--el-text-color-primary)] font-bold">
          {{ t('intro') }}
        </h3>
        <p class="text-sm text-muted-foreground lg:text-base">
          演示系统未提供该功能
        </p>
      </FaBlurReveal>
      <FormField v-slot="{ componentField, errors }" name="account">
        <FormItem class="relative pb-6 space-y-0">
          <FormControl>
            <FaInput type="text" :placeholder="t('form.account')" class="w-full" :class="errors.length && 'border-destructive'" v-bind="componentField" />
          </FormControl>
          <Transition enter-active-class="transition-opacity" enter-from-class="opacity-0" leave-active-class="transition-opacity" leave-to-class="opacity-0">
            <FormMessage class="absolute bottom-1 text-xs" />
          </Transition>
        </FormItem>
      </FormField>
      <div class="flex-start-between gap-2">
        <FormField v-slot="{ componentField, value, setValue }" name="captcha">
          <FormItem class="relative pb-6 space-y-0">
            <FormControl>
              <FaPinInput :model-value="value" :name="componentField.name" :length="6" class="border-destructive" @update:model-value="val => setValue(val)" />
            </FormControl>
            <Transition enter-active-class="transition-opacity" enter-from-class="opacity-0" leave-active-class="transition-opacity" leave-to-class="opacity-0">
              <FormMessage class="absolute bottom-1 text-xs" />
            </Transition>
          </FormItem>
        </FormField>
        <FaButton variant="outline" size="lg" :disabled="countdown > 0" class="flex-1 px-4" @click="handleSendCaptcha">
          {{ countdown === 0 ? t('form.sendCaptcha') : t('form.captchaCountdown', [countdown]) }}
        </FaButton>
      </div>
      <FormField v-slot="{ componentField, errors }" name="newPassword">
        <FormItem class="relative pb-6 space-y-0">
          <FormControl>
            <FaInput type="password" :placeholder="t('form.newPassword')" class="w-full" :class="errors.length && 'border-destructive'" v-bind="componentField" />
          </FormControl>
          <Transition enter-active-class="transition-opacity" enter-from-class="opacity-0" leave-active-class="transition-opacity" leave-to-class="opacity-0">
            <FormMessage class="absolute bottom-1 text-xs" />
          </Transition>
        </FormItem>
      </FormField>
      <FaButton :loading="loading" size="lg" class="mt-4 w-full" type="submit">
        {{ t('form.confirm') }}
      </FaButton>
      <div class="mt-4 flex-center gap-2 text-sm color-[var(--el-text-color-secondary)]">
        <FaButton variant="link" class="h-auto p-0" @click="emits('onLogin', form.values.account)">
          {{ t('login') }}
        </FaButton>
      </div>
    </form>
  </div>
</template>
