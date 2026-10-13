import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

const source = readFileSync(new URL('./index.vue', import.meta.url), 'utf8')

describe('binding settings page', () => {
  it('owns the tree and all runtime binding lifecycle actions', () => {
    expect(source).toContain('<LineUpMonitorTree')
    expect(source).toContain('workspace.prepare')
    expect(source).toContain('workspace.validate')
    expect(source).toContain('workspace.setMonitoring')
    expect(source).toContain('ElMessageBox.confirm')
  })

  it('links to GRAPH structure and exact work-position monitoring without alias or publish controls', () => {
    expect(source).toContain('path: \'/lineup/graphs\'')
    expect(source).toContain('path: \'/lineup/monitor\'')
    expect(source).toContain('subStationId: workspace.selectedSubStationId.value')
    expect(source).not.toContain('saveAlias')
    expect(source).not.toContain('publishRevision')
  })
})
