<script setup lang="ts">
import dayjs from '@/utils/dayjs'
import { setVxeLanguage } from '@/utils/vxeLocale'
import { ua } from '@/utils/ua'
import Provider from './ui/provider/index.vue'
import 'dayjs/locale/en'
import 'dayjs/locale/ja'
import 'dayjs/locale/zh-cn'
import 'dayjs/locale/zh-tw'

const route = useRoute()

const settingsStore = useSettingsStore()

// 必须用 global scope：菜单/导航标题走 i18n.global 作用域（useMenu useScope:'global'），
// 若取局部 composer，切语言只更新本组件局部 locale，全局 locale 不动 → 菜单恒停初始语言
const { locale, t } = useI18n({ useScope: 'global' })
const { auth } = useAuth()
const { generateI18nTitle } = useMenu()

document.body.setAttribute('data-os', ua.getOS().name || '')

const isAuth = computed(() => {
  return route.matched.every((item) => {
    return auth(item.meta.auth ?? '')
  })
})

// 设置网页 title
watch([
  () => settingsStore.settings.app.enableDynamicTitle,
  () => settingsStore.title,
  () => settingsStore.customTitleList,
  () => settingsStore.lang,
], () => {
  nextTick(() => {
    if (settingsStore.settings.app.enableDynamicTitle && settingsStore.title) {
      const title = settingsStore.customTitleList.find(item => item.fullPath === route.fullPath)?.title || generateI18nTitle(settingsStore.title)
      document.title = `${title} - ${t('app.title')}`
    }
    else {
      document.title = t('app.title')
    }
  })
}, {
  immediate: true,
  deep: true,
})

onMounted(() => {
  settingsStore.setMode(document.documentElement.clientWidth)
  window.addEventListener('resize', () => {
    settingsStore.setMode(document.documentElement.clientWidth)
  })
})

watch(() => settingsStore.lang, () => {
  locale.value = settingsStore.lang
})

watch(() => settingsStore.lang, () => {
  switch (settingsStore.lang) {
    case 'zh-cn':
      dayjs.locale('zh-cn')
      break
    case 'zh-tw':
      dayjs.locale('zh-tw')
      break
    case 'en':
      dayjs.locale('en')
      break
    case 'ja':
      dayjs.locale('ja')
      break
  }
}, {
  immediate: true,
})

// vxe 组件库语言跟随（分页 / 空数据 / 日期等内置文案）
watch(() => settingsStore.lang, (val) => {
  setVxeLanguage(val)
}, {
  immediate: true,
})
</script>

<template>
  <Provider>
    <RouterView v-slot="{ Component }">
      <component :is="Component" v-if="isAuth" />
      <FaNotAllowed v-else />
    </RouterView>
    <FaBackToTop />
    <FaLoginAgain />
    <FaCheckUpdates />
    <FaToast />
    <FaNotification />
    <FaSystemInfo />
  </Provider>
</template>
