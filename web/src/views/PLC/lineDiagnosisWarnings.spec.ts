import { describe, expect, it } from 'vitest'
import { diagnosisWarnings } from './lineDiagnosisWarnings'

describe('diagnosisWarnings', () => {
  it('returns nothing without a result', () => {
    expect(diagnosisWarnings(null)).toEqual([])
    expect(diagnosisWarnings(undefined)).toEqual([])
    expect(diagnosisWarnings({})).toEqual([])
  })

  it('keeps the backend order, trims and drops empty or non-text entries', () => {
    expect(diagnosisWarnings({ warnings: ['  GRAPH 块 FB360 没有工站  ', '', null, 3, 'OP030 有两个建议'] }))
      .toEqual(['GRAPH 块 FB360 没有工站', 'OP030 有两个建议'])
  })

  it('removes duplicates and also reads the raw result of agent mode', () => {
    expect(diagnosisWarnings({
      warnings: ['A', 'B'],
      rawResult: { warnings: ['B', 'C'] },
    })).toEqual(['A', 'B', 'C'])
  })
})
