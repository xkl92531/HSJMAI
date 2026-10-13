import pack from './projectUpload.lang'

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
  let text = table.projectUpload[key] ?? pack['zh-cn'].projectUpload[key] ?? key
  if (params) {
    text = text.replace(/\{(\w+)\}/g, (match, name: string) => (
      params[name] === undefined ? match : String(params[name])
    ))
  }
  return text
}

function t(key: string, params?: I18nBridgeParams): string {
  if (globalTranslate) {
    return globalTranslate(`projectUpload.${key}`, params)
  }
  return localTranslate(key, params)
}

export interface UploadGraphSummary {
  detectedBlockCount: number
  draftCreatedCount: number
  reusedRevisionCount: number
  failedCount: number
  issueCount: number
}

export interface GraphSummaryPresentation {
  text: string
  type: 'success' | 'warning'
}

export function graphSummaryPresentation(summary: UploadGraphSummary): GraphSummaryPresentation {
  return {
    text: t('graphSummaryText', {
      detected: summary.detectedBlockCount,
      draft: summary.draftCreatedCount,
      reused: summary.reusedRevisionCount,
      failed: summary.failedCount,
      issue: summary.issueCount,
    }),
    type: summary.failedCount > 0 ? 'warning' : 'success',
  }
}

// 上传完成后的后处理任务状态（标签增量注册等，路线 A 2026-09-11）
export interface UploadPostProcess {
  key: string
  title: string
  status: string // Running / Succeeded / Failed / Skipped
  detail: string | null
  finishTime: string | null
}

/** 后处理是否有条目仍在执行（完成态继续轮询的条件） */
export function hasRunningPostProcess(items: UploadPostProcess[] | null | undefined): boolean {
  return (items ?? []).some(item => item.status === 'Running')
}

/** 后处理状态 → 展示文本（未知状态原样透出） */
export function postProcessStatusText(status: string | null | undefined): string {
  const map: Record<string, string> = {
    Running: t('postStatusRunning'),
    Succeeded: t('postStatusSucceeded'),
    Failed: t('postStatusFailed'),
    Skipped: t('postStatusSkipped'),
  }
  return map[status ?? ''] || status || ''
}

/** 后处理状态 → el-tag 类型 */
export function postProcessTagType(status: string | null | undefined): 'primary' | 'success' | 'danger' | 'info' {
  const map: Record<string, 'primary' | 'success' | 'danger' | 'info'> = {
    Running: 'primary',
    Succeeded: 'success',
    Failed: 'danger',
    Skipped: 'info',
  }
  return map[status ?? ''] || 'info'
}

/**
 * 上传进度轮询的下一步（2026-10-02 审查 P1）：
 * 0/1/2（含 XMLV2 约束生成阶段，后端保持 Saving）→ 继续轮询；4 → 失败、停止并显示原因；
 * 3 且后处理仍在跑 → 完成但继续轮询后处理；3 → 完成。未知状态按仍在处理。
 */
export type UploadPollAction = 'continue' | 'completed-wait' | 'completed' | 'failed'

export function uploadPollAction(status: number | null | undefined, postProcesses: UploadPostProcess[] | null | undefined): UploadPollAction {
  if (status === 4) {
    return 'failed'
  }
  if (status === 3) {
    return hasRunningPostProcess(postProcesses) ? 'completed-wait' : 'completed'
  }
  return 'continue'
}
