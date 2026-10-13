import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

const source = readFileSync(new URL('./LineUpMonitorTree.vue', import.meta.url), 'utf8')

describe('lineUpMonitorTree source contract', () => {
  it('uses a fixed scrolling tree with exact node identity', () => {
    expect(source).toContain('<el-tree')
    expect(source).toContain('node-key="nodeId"')
    expect(source).toContain('class="monitor-tree__scroll"')
    expect(source).toMatch(/overflow:\s*hidden auto/)
  })

  it('keeps parent expansion separate from exact work-position and GRAPH selection', () => {
    expect(source).toContain('if (data.nodeType === \'SubStation\')')
    expect(source).toContain('emit(\'selectSubStation\', data.entityId)')
    expect(source).toContain('if (data.nodeType === \'Graph\')')
    expect(source).toContain('emit(\'selectGraph\', data.entityId, parent.entityId)')
    expect(source).toContain(':expand-on-click-node="false"')
    expect(source).toContain('treeRef.value?.store.nodesMap[data.nodeId]')
  })

  it('highlights the selected GRAPH leaf when present, else the work position', () => {
    expect(source).toContain('`graph:${props.selectedBindingId}`')
    expect(source).toContain('`substation:${props.selectedSubStationId}`')
  })

  it('exposes debounced search, refresh, status text and collapse recovery', () => {
    expect(source).toContain('createLineUpMonitorTreeSearch')
    expect(source).toContain('emit(\'refresh\')')
    expect(source).toContain('statusText[data.runtimeStatus]')
    expect(source).toContain('statusToken[data.runtimeStatus]')
    expect(source).toContain('emit(\'toggleCollapsed\')')
    expect(source).toContain('monitor-tree--collapsed')
  })
})
