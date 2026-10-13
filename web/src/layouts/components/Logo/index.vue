<script setup lang="ts">
import imgLogo from '@/assets/images/logo.png'

defineOptions({
  name: 'Logo',
})

withDefaults(
  defineProps<{
    showLogo?: boolean
    showTitle?: boolean
  }>(),
  {
    showLogo: true,
    showTitle: true,
  },
)

const settingsStore = useSettingsStore()

const { t } = useI18n({ useScope: 'global' })

const title = computed(() => t('app.title'))
const logo = ref(imgLogo)

const to = computed(() => settingsStore.settings.home.enable ? settingsStore.settings.home.fullPath : '')
</script>

<template>
  <RouterLink :to class="h-[var(--g-sidebar-logo-height)] w-inherit flex-center gap-2 px-3 text-primary no-underline" :class="{ 'cursor-default': !settingsStore.settings.home.enable }" :title="title">
    <img v-if="showLogo" :src="logo" class="logo h-[30px] w-[30px] object-contain">
    <span v-if="showTitle" class="block truncate font-bold">{{ title }}</span>
  </RouterLink>
</template>
