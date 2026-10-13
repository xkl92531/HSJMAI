<i18n lang="yaml">
zh-cn:
  profile: 个人设置
  preferences: 偏好设置
  hotkeys: 快捷键介绍
  feedback: 反馈
  logout: 退出登录
  current_account: 当前登录账号
zh-tw:
  profile: 個人設置
  preferences: 偏好設置
  hotkeys: 快捷鍵介紹
  feedback: 反饋
  logout: 退出登錄
  current_account: 當前登錄帳號
en:
  profile: Personal Settings
  preferences: Preferences
  hotkeys: Hotkeys Introduction
  feedback: Feedback
  logout: Logout
  current_account: Current Login Account
ja:
  profile: 個人設定
  preferences: 環境設定
  hotkeys: ショートカットキーの一覧
  feedback: フィードバック
  logout: ログアウト
  current_account: 現在ログイン中のアカウント
</i18n>

<script setup lang="ts">
import type { HTMLAttributes } from 'vue'
import { cn } from '@/utils'
import eventBus from '@/utils/eventBus'
import Feedback from './feedback.vue'
import Profile from './profile.vue'

const props = withDefaults(defineProps<{
  onlyAvatar?: boolean
  dropdownAlign?: 'start' | 'center' | 'end'
  dropdownSide?: 'left' | 'right' | 'top' | 'bottom'
  buttonVariant?: 'secondary' | 'ghost'
  class?: HTMLAttributes['class']
}>(), {
  dropdownAlign: 'end',
  dropdownSide: 'right',
  buttonVariant: 'ghost',
})

const router = useRouter()

const settingsStore = useSettingsStore()
const userStore = useUserStore()

const { t } = useI18n()
const { generateI18nTitle } = useMenu()

const isProfileShow = ref(false)
const isFeedbackShow = ref(false)
</script>

<template>
  <FaDropdown
    :align="dropdownAlign" :side="dropdownSide" :items="[
      [
        { label: generateI18nTitle(settingsStore.settings.home.title), icon: 'i-mdi:home', handle: () => router.push({ path: settingsStore.settings.home.fullPath }), hide: !settingsStore.settings.home.enable },
        { label: t('profile'), icon: 'i-mdi:account', handle: () => isProfileShow = true },
        { label: t('preferences'), icon: 'i-mdi:cog', handle: () => eventBus.emit('global-preferences-toggle'), hide: !settingsStore.settings.userPreferences.enable },
        { label: t('feedback'), icon: 'i-mdi:comment-processing', handle: () => isFeedbackShow = true, hide: !settingsStore.settings.app.enableFeedback },
      ],
      [
        { label: t('hotkeys'), icon: 'i-mdi:keyboard', handle: () => eventBus.emit('global-hotkeys-intro-toggle'), hide: settingsStore.mode !== 'pc' },
      ],
      [
        { label: t('logout'), icon: 'i-mdi:logout', handle: () => userStore.logout(settingsStore.settings.home.fullPath) },
      ],
    ]" class="flex-center"
  >
    <template #header>
      <div class="space-y-2">
        <div class="text-xs text-secondary-foreground/50 font-light">
          {{ t('current_account') }}
        </div>
        <div class="flex-center-start gap-2">
          <FaAvatar :src="userStore.avatar" :fallback="userStore.account.slice(0, 2)" shape="square" />
          <div class="space-y-1">
            <div class="text-base lh-none">
              {{ userStore.empName }}
            </div>
            <!-- <div class="text-xs text-secondary-foreground/50 font-normal">
              [ 这里可以显示邮箱 ]
            </div> -->
          </div>
        </div>
      </div>
    </template>
    <FaButton
      :variant="buttonVariant" :class="cn('flex-center gap-1 p-2', {
        'size-8 p-1': onlyAvatar,
      }, props.class)"
    >
      <FaAvatar :src="userStore.avatar" :class="cn('size-6', { 'size-full': onlyAvatar })">
        <FaIcon name="i-carbon:user-avatar-filled" class="size-6 text-secondary-foreground/50" />
      </FaAvatar>
      <div v-if="!onlyAvatar" class="min-w-0 flex-center-between flex-1 gap-2">
        <div class="flex-1 truncate text-start">
          {{ userStore.account }}
        </div>
        <FaIcon name="i-material-symbols:expand-all-rounded" />
      </div>
    </FaButton>
  </FaDropdown>
  <FaModal v-model="isProfileShow" align-center :header="false" :footer="false" :close-on-click-overlay="false" :close-on-press-escape="false" class="h-500px max-w-xl overflow-hidden" content-class="min-h-full p-0 flex">
    <Profile />
  </FaModal>
  <FaModal v-model="isFeedbackShow" :title="t('feedback')" :footer="false" class="max-w-[800px]">
    <Feedback @close="isFeedbackShow = false" />
  </FaModal>
</template>
