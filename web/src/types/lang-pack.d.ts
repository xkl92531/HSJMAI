/**
 * View-local i18n language packs (e.g. ./job-center.lang, ./components/lang)
 * are plain ESM JS modules exporting { [locale: string]: { [ns: string]: Record<string, string> } }.
 * This ambient declaration types every '*.lang' import so vue-tsc does not emit
 * TS7016 for each pack (repo-wide accepted pattern; see tools/i18n_conversion_recipe.md).
 */
declare module '*.lang' {
  const messages: Record<string, Record<string, Record<string, string>>>
  export default messages
}
