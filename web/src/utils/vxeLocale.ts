import type { VxeGlobalI18nLocale } from 'vxe-pc-ui'
import { VxeUI } from 'vxe-pc-ui'
import vxeZhCN from 'vxe-pc-ui/es/language/zh-CN'
import vxeZhTW from 'vxe-pc-ui/es/language/zh-TW'
import vxeEnUS from 'vxe-pc-ui/es/language/en-US'
import vxeJaJP from 'vxe-pc-ui/es/language/ja-JP'

// 应用语言代码 -> vxe 语言代码映射（与 src/locales 语言包一一对应）
const langMap: Record<string, VxeGlobalI18nLocale> = {
  'zh-cn': 'zh-CN',
  'zh-tw': 'zh-TW',
  'en': 'en-US',
  'ja': 'ja-JP',
}

/**
 * 注册 vxe 组件库多语言资源（应用启动时调用一次）
 * vxe 的分页 / 空数据 / 日期选择等内置文案由这些语言包驱动
 */
export function setupVxeLocales() {
  VxeUI.setI18n('zh-CN', vxeZhCN)
  VxeUI.setI18n('zh-TW', vxeZhTW)
  VxeUI.setI18n('en-US', vxeEnUS)
  VxeUI.setI18n('ja-JP', vxeJaJP)
}

/**
 * 切换 vxe 组件库语言（跟随 settingsStore.lang）
 */
export function setVxeLanguage(lang: string) {
  VxeUI.setLanguage(langMap[lang] ?? 'zh-CN')
}
