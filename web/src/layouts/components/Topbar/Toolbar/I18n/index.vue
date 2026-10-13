<script setup lang="ts">
import { localesName } from '@/locales'

defineOptions({
  name: 'I18n',
})

const settingsStore = useSettingsStore()

const langItems = computed(() => {
  return Object.keys(localesName).map((item: any) => ({
    label: localesName[item],
    disabled: settingsStore.lang === item,
    handle: () => settingsStore.setDefaultLang(item),
  }))
})

const isAnimating = ref(false)

watch(() => settingsStore.lang, (newVal, oldVal) => {
  if (newVal !== oldVal) {
    isAnimating.value = true
  }
})
</script>

<template>
  <FaDropdown :items="[langItems]">
    <FaButton variant="ghost" size="icon" class="size-9" :class="{ animation: isAnimating }" @animationend="isAnimating = false">
      <FaIcon name="i-ri:translate" class="size-4" />
    </FaButton>
  </FaDropdown>
</template>

<style scoped>
.animation {
  transform-origin: center top;
  animation: animation 1s;
}

@keyframes animation {
  0% {
    transform: rotateY(0deg);
  }

  100% {
    transform: rotateY(360deg);
  }
}
</style>
