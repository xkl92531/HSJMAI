/** 一键排查结果里的排查前提示（后端 LineDiagnosisResultDto.Warnings，如拿不准工站的 GRAPH 块） */
export function diagnosisWarnings(result: { warnings?: unknown, rawResult?: { warnings?: unknown } | null } | null | undefined): string[] {
  const seen = new Set<string>()
  const list: string[] = []
  for (const source of [result?.warnings, result?.rawResult?.warnings]) {
    if (!Array.isArray(source)) {
      continue
    }
    for (const item of source) {
      if (typeof item !== 'string') {
        continue
      }
      const text = item.trim()
      if (text && !seen.has(text)) {
        seen.add(text)
        list.push(text)
      }
    }
  }
  return list
}
