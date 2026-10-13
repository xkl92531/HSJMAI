import type { LineUpGraphRuntimeProjection, LineUpRuntimeTone } from '../monitor/monitorProjection'
import type { LineUpGraphPreviewSequenceInput } from './tiaGraphLayout'
import pack from './lang'

// —— 非 vue 模块的 i18n 桥（组件外无 useI18n 上下文）——
// 优先动态接入全局 i18n 实例（@/locales 具名导出 i18n，legacy:false 下 i18n.global.t 可用），
// 并把本视图语言包幂等合并进全局消息（页面 setup 的 mergeLocaleMessage 合并同一份数据，互不冲突）；
// 动态导入在无 vite 插件链的环境（vitest）无法解析 @/locales 的虚拟模块，捕获后回退本地四语字典：
// 语言探测顺序 = localStorage 'userPreferences'（fantastic-admin 本地偏好，storageTo=local 时写入）
// → navigator.language → zh-cn，与全局 fallbackLocale 一致（测试环境因此稳定拿到 zh-cn 文案）。
type I18nBridgeParams = Record<string, string | number>
type I18nBridgeTranslate = (key: string, params?: I18nBridgeParams) => string

let globalTranslate: I18nBridgeTranslate | undefined

void import('@/locales').then((mod) => {
  // vue-i18n Composer 的泛型重载与四语字典结构不完全吻合，这里按桥签名收窄（运行时语义不变）
  const bridge = mod.i18n.global as unknown as {
    t: I18nBridgeTranslate
    mergeLocaleMessage: (locale: string, message: Record<string, unknown>) => void
  }
  for (const locale of Object.keys(pack)) {
    bridge.mergeLocaleMessage(locale, pack[locale])
  }
  globalTranslate = bridge.t
}).catch(() => {
  // vitest 等未挂 vite 插件链的环境：保持本地字典回退
})

function detectBridgeLocale(): string {
  try {
    if (typeof localStorage !== 'undefined') {
      const raw = localStorage.getItem('userPreferences')
      const parsed = raw ? JSON.parse(raw) as Record<string, { app?: { defaultLang?: string } } | undefined> : null
      if (parsed && typeof parsed === 'object') {
        for (const profile of Object.values(parsed)) {
          const lang = profile?.app?.defaultLang
          if (typeof lang === 'string' && pack[lang]) {
            return lang
          }
        }
      }
    }
  }
  catch {
    // 本地偏好读取失败（JSON 损坏等）：继续走浏览器语言探测
  }
  const nav = typeof navigator !== 'undefined' ? String(navigator.language ?? '').toLowerCase() : ''
  if (/^zh-(tw|hant|hk|mo)/.test(nav)) {
    return 'zh-tw'
  }
  if (nav.startsWith('zh')) {
    return 'zh-cn'
  }
  if (nav.startsWith('ja')) {
    return 'ja'
  }
  if (nav.startsWith('en')) {
    return 'en'
  }
  return 'zh-cn'
}

function localTranslate(key: string, params?: I18nBridgeParams): string {
  const table = pack[detectBridgeLocale()] ?? pack['zh-cn']
  let text = table.lineupShared[key] ?? pack['zh-cn'].lineupShared[key] ?? key
  if (params) {
    text = text.replace(/\{(\w+)\}/g, (match, name: string) => (
      params[name] === undefined ? match : String(params[name])
    ))
  }
  return text
}

function t(key: string, params?: I18nBridgeParams): string {
  if (globalTranslate) {
    return globalTranslate(`lineupShared.${key}`, params)
  }
  return localTranslate(key, params)
}

export type TiaGraphOnlineTone = 'static' | 'online' | 'degraded' | 'offline' | 'connecting'

export interface TiaGraphToolbarState {
  activeStepSourceKey: string
  canLocateActiveStep: boolean
  onlineLabel: string
  onlineTone: TiaGraphOnlineTone
}

// online 复用 lineupShared.statusOnline（与 LineUpMonitorTree 连接徽标同词），其余为工具栏专用键
const onlineLabelKeys: Record<Exclude<TiaGraphOnlineTone, 'static'>, string> = {
  online: 'statusOnline',
  degraded: 'toolbarDegraded',
  offline: 'toolbarOffline',
  connecting: 'toolbarConnecting',
}

/** 可定位的步色调 = 工步值仍为 true 的四种状态（Active/Waiting/Warning/TimedOut）。
 * 2026-09-11 需求②：「第一个 true 的工步」含超时/等待中的步——设备停在超时步时也要能定位。 */
const locatableStepTones: LineUpRuntimeTone[] = ['active', 'waiting', 'warning', 'timeout']

export function resolveTiaGraphToolbarState(
  sequence: LineUpGraphPreviewSequenceInput | null,
  runtime: LineUpGraphRuntimeProjection | null,
): TiaGraphToolbarState {
  if (!runtime) {
    return { activeStepSourceKey: '', canLocateActiveStep: false, onlineLabel: t('toolbarStaticPreview'), onlineTone: 'static' }
  }
  const onlineTone = runtime.connectionTone
  // degraded（读值部分失败）时步状态按需求①保持上一有效值，仍可定位；offline 无值可依
  const activeStepSourceKey = onlineTone === 'online' || onlineTone === 'degraded'
    ? sequence?.nodes.find(node => node.nodeKind === 'Step'
        && locatableStepTones.includes(runtime.nodes[node.sourceKey]?.tone ?? 'idle'))?.sourceKey ?? ''
    : ''
  return {
    activeStepSourceKey,
    canLocateActiveStep: Boolean(activeStepSourceKey),
    onlineLabel: t(onlineLabelKeys[onlineTone]),
    onlineTone,
  }
}

export function nextTiaGraphZoom(current: number, direction: 'in' | 'out') {
  const requested = current + (direction === 'in' ? 0.15 : -0.15)
  return Math.round(Math.min(2, Math.max(0.2, requested)) * 100) / 100
}

export interface TiaGraphWheelInput {
  deltaY: number
  deltaMode: number
  ctrlKey: boolean
  metaKey: boolean
}

export function resolveTiaGraphWheelPan(input: TiaGraphWheelInput, viewportHeight: number) {
  if (input.ctrlKey || input.metaKey) {
    return null
  }
  const unit = input.deltaMode === 1
    ? 36
    : input.deltaMode === 2
      ? Math.max(0, viewportHeight)
      : 1
  const requested = input.deltaY * unit
  return Math.min(160, Math.max(-160, requested))
}
