import { describe, expect, it } from 'vitest'
import { totalGraphDiffCounts } from '@/api/modules/LineUp/catalog'

describe('graph publish diff projection', () => {
  it('totals the count fields returned by the backend contract', () => {
    expect(totalGraphDiffCounts({
      sequenceCount: 2,
      nodeCount: 3,
      actionCount: 4,
      edgeCount: 5,
    })).toBe(14)
  })
})
