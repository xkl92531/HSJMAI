<i18n lang="json">
{
  "zh-cn": {
    "accountLogin": "账号密码登录",
    "qrcodeLogin": "扫码登录",
    "intro": "欢迎使用 👋🏻",
    "remember": "记住我",
    "forget": "忘记密码了?",
    "noAccount": "还没有帐号?",
    "register": "注册新帐号",
    "wechatQrcode": "请使用微信扫码登录",
    "testLogin": "演示账号一键登录",
    "form": {
      "account": "用户名",
      "password": "密码",
      "login": "登录"
    },
    "rules": {
      "account": "请输入用户名",
      "password": "请输入密码"
    }
  },
  "zh-tw": {
    "accountLogin": "帳號密碼登入",
    "qrcodeLogin": "掃碼登入",
    "intro": "歡迎使用 👋🏻",
    "remember": "記住我",
    "forget": "忘記密碼了?",
    "noAccount": "還沒有帳號?",
    "register": "註冊新帳號",
    "wechatQrcode": "請使用微信掃碼登入",
    "testLogin": "演示帳號一键登入",
    "form": {
      "account": "用戶名",
      "password": "密碼",
      "login": "登入"
    },
    "rules": {
      "account": "請輸入用戶名",
      "password": "請輸入密碼"
    }
  },
  "en": {
    "accountLogin": "Account Login",
    "qrcodeLogin": "Scan Login",
    "intro": "Welcome aboard 👋🏻",
    "remember": "Remember Me",
    "forget": "Forget Password?",
    "noAccount": "No Account?",
    "register": "Register New Account",
    "wechatQrcode": "Please use WeChat scan login",
    "testLogin": "Demo Account One-click Login",
    "form": {
      "account": "Account",
      "password": "Password",
      "login": "Login"
    },
    "rules": {
      "account": "Please enter the account",
      "password": "Please enter the password"
    }
  },
  "ja": {
    "accountLogin": "アカウントログイン",
    "qrcodeLogin": "QRコードログイン",
    "intro": "ようこそ 👋🏻",
    "remember": "ログイン状態を保持する",
    "forget": "パスワードをお忘れですか？",
    "noAccount": "アカウントをお持ちでない方",
    "register": "新規アカウント登録",
    "wechatQrcode": "WeChatのQRコードでログインしてください",
    "testLogin": "デモアカウントでワンクリックログイン",
    "form": {
      "account": "ユーザー名",
      "password": "パスワード",
      "login": "ログイン"
    },
    "rules": {
      "account": "ユーザー名を入力してください",
      "password": "パスワードを入力してください"
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
  name: 'LoginForm',
})

const props = defineProps<{
  account?: string
}>()

const emits = defineEmits<{
  onLogin: [account?: string]
  onRegister: [account?: string]
  onResetPassword: [account?: string]
}>()

const { t } = useI18n()
const userStore = useUserStore()

const title = computed(() => t('app.title'))
const loading = ref(false)

// 登录方式，default 账号密码登录，qrcode 扫码登录
const type = ref<'default' | 'qrcode'>('default')

const form = useForm({
  validationSchema: toTypedSchema(z.object({
    account: z.string().min(1, t('rules.account')),
    password: z.string().min(1, t('rules.password')),
    remember: z.boolean(),
  })),
  initialValues: {
    account: props.account ?? localStorage.getItem('login_account') ?? '',
    password: '',
    remember: localStorage.getItem('login_account') !== null,
  },
})
const onSubmit = form.handleSubmit((values) => {
  loading.value = true
  userStore.login(values).then(() => {
    if (values.remember) {
      localStorage.setItem('login_account', values.account)
    }
    else {
      localStorage.removeItem('login_account')
    }
    emits('onLogin', values.account)
  }).finally(() => {
    loading.value = false
  })
})

function testAccount(account: string) {
  form.setFieldValue('account', account)
  form.setFieldValue('password', '123456')
  onSubmit()
}
</script>

<template>
  <div class="min-h-500px w-full flex-col-stretch-center p-12">
    <FaBlurReveal :delay="0.2" :duration="0.4" class="mb-6 space-y-2">
      <h3 class="text-4xl color-[var(--el-text-color-primary)] font-bold">
        {{ t('intro') }}
      </h3>
      <p class="text-sm text-muted-foreground lg:text-base">
        {{ title }}
      </p>
    </FaBlurReveal>
    <!-- <div class="mb-4">
      <FaTabs
        v-model="type" :list="[
          { label: t('accountLogin'), value: 'default' },
          // { label: t('qrcodeLogin'), value: 'qrcode' },
        ]" class="inline-flex"
      />
    </div> -->
    <div v-show="type === 'default'">
      <form @submit="onSubmit">
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
        <FormField v-slot="{ componentField, errors }" name="password">
          <FormItem class="relative pb-6 space-y-0">
            <FormControl>
              <FaInput type="password" :placeholder="t('form.password')" class="w-full" :class="errors.length && 'border-destructive'" v-bind="componentField" />
            </FormControl>
            <Transition enter-active-class="transition-opacity" enter-from-class="opacity-0" leave-active-class="transition-opacity" leave-to-class="opacity-0">
              <FormMessage class="absolute bottom-1 text-xs" />
            </Transition>
          </FormItem>
        </FormField>
        <div class="mb-4 flex-center-between">
          <div class="flex-center-start">
            <FormField v-slot="{ componentField }" type="checkbox" name="remember">
              <FormItem>
                <FormControl>
                  <FaCheckbox v-bind="componentField">
                    {{ t('remember') }}
                  </FaCheckbox>
                </FormControl>
              </FormItem>
            </FormField>
          </div>
          <FaButton variant="link" class="h-auto p-0" type="button" @click="emits('onResetPassword', form.values.account)">
            {{ t('forget') }}
          </FaButton>
        </div>
        <FaButton :loading="loading" size="lg" class="w-full" type="submit">
          {{ t('form.login') }}
        </FaButton>
        <!-- <div class="mt-4 flex-center gap-2 text-sm">
          <span class="text-secondary-foreground op-50">{{ t('noAccount') }}</span>
          <FaButton variant="link" class="h-auto p-0" type="button" @click="emits('onRegister', form.values.account)">
            {{ t('register') }}
          </FaButton>
        </div> -->
      </form>
     
    </div>
    <!-- <div v-show="type === 'qrcode'">
      <div class="flex-col-center">
        <img src="https://s2.loli.net/2024/04/26/GsahtuIZ9XOg5jr.png" class="h-[250px] w-[250px]">
        <div class="mt-2 text-sm text-secondary-foreground op-50">
          {{ t('wechatQrcode') }}
        </div>
      </div>
    </div> -->
  </div>
</template>
