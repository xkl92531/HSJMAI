<script setup lang="ts">
import type { UseTimeAgoMessages, UseTimeAgoUnitNamesDefault } from '@vueuse/core'
import { useTimeAgo } from '@vueuse/core'

defineOptions({
  name: 'FaTimeAgo',
})

const props = withDefaults(defineProps<{
  date?: Date
  max?: UseTimeAgoUnitNamesDefault
  messages?: UseTimeAgoMessages
  showSecond?: boolean
  updateInterval?: number
}>(), {
  date: () => new Date(),
  updateInterval: 30_000,
})

const settingsStore = useSettingsStore()

// 中文文案（原有默认文案，zh-cn / zh-tw / en 等非日文语言继续沿用，保持既有行为不变）
const zhCnMessages: UseTimeAgoMessages = {
  justNow: '刚刚',
  past: n => n.match(/\d/) ? `${n}前` : n,
  future: n => n.match(/\d/) ? `${n}后` : n,
  month: (n, past) => n === 1
    ? past
      ? '上个月'
      : '下个月'
    : `${n} 月`,
  year: (n, past) => n === 1
    ? past
      ? '去年'
      : '明年'
    : `${n} 年`,
  day: (n, past) => n === 1
    ? past
      ? '昨天'
      : '明天'
    : `${n} 天`,
  week: (n, past) => n === 1
    ? past
      ? '上周'
      : '下周'
    : `${n} 周`,
  hour: n => `${n} 小时`,
  minute: n => `${n} 分钟`,
  second: n => `${n} 秒`,
  invalid: '',
}

// 日文文案（当前应用语言为 ja 时启用）
const jaMessages: UseTimeAgoMessages = {
  justNow: 'たった今',
  past: n => n.match(/\d/) ? `${n}前` : n,
  future: n => n.match(/\d/) ? `${n}後` : n,
  month: (n, past) => n === 1
    ? past
      ? '先月'
      : '来月'
    : `${n}ヶ月`,
  year: (n, past) => n === 1
    ? past
      ? '去年'
      : '来年'
    : `${n} 年`,
  day: (n, past) => n === 1
    ? past
      ? '昨日'
      : '明日'
    : `${n} 日`,
  week: (n, past) => n === 1
    ? past
      ? '先週'
      : '来週'
    : `${n} 週間`,
  hour: n => `${n} 時間`,
  minute: n => `${n} 分`,
  second: n => `${n} 秒`,
  invalid: '',
}

// 时间文案跟随当前应用语言（settingsStore.lang，与 dayjs / vxe 的语言切换保持一致）：ja 用日文，其余语言沿用原有中文文案；外部传入的 messages 优先
const timeAgoMessages = computed<UseTimeAgoMessages>(() => props.messages ?? (settingsStore.lang === 'ja' ? jaMessages : zhCnMessages))

const timeAgo = useTimeAgo(props.date, {
  messages: timeAgoMessages.value,
  max: props.max,
  showSecond: props.showSecond,
  updateInterval: props.updateInterval,
})
</script>

<template>
  <span>{{ timeAgo }}</span>
</template>
