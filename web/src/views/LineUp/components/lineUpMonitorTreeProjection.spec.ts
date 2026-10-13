import type { LineUpMonitorNavigationNode } from '@/api/modules/LineUp/monitor'
import { describe, expect, it, vi } from 'vitest'
import {
  createLineUpMonitorTreeSearch,
  projectMonitorTree,
  validateMonitorTreeNodes,
} from './lineUpMonitorTree'

const nodes: LineUpMonitorNavigationNode[] = [
  node('line:10', 'Line', null, '10', 'L10', '一号线', 4, 1, 'Ready'),
  node('process:20', 'Process', 'line:10', '20', 'P20', '上料', 3, 1, 'Ready'),
  node('station:30', 'Station', 'process:20', '30', 'OP020', '缓存上料', 2, 1, 'Ready'),
  node('substation:3001', 'SubStation', 'station:30', '3001', 'OP020-01', '主工位', 0, 1, 'Ready'),
  node('substation:3002', 'SubStation', 'station:30', '3002', 'OP020-02', '备用工位', 0, 0, 'Unconfigured'),
  node('graph:7001', 'Graph', 'substation:3001', '7001', 'FB260_MainGraph', '', 0, 0, 'Ready'),
  node('graph:7002', 'Graph', 'substation:3001', '7002', 'FB261_HomeGraph', '', 0, 1, 'Error'),
]

describe('projectMonitorTree', () => {
  it('keeps complete ancestors and exact string entity identities', () => {
    const tree = projectMonitorTree(nodes, 'op020-01')

    expect(tree).toHaveLength(1)
    const subStation = tree[0].children[0].children[0].children[0]
    expect(subStation.entityId).toBe('3001')
    expect(tree[0].issueCount).toBe(1)
    expect(subStation.isLeaf).toBe(false)
  })

  it('nests GRAPH leaves under their work position with statuses', () => {
    const subStation = flatten(projectMonitorTree(nodes))
      .find(item => item.nodeType === 'SubStation' && item.entityId === '3001')

    const graphs = subStation?.children ?? []
    expect(graphs).toHaveLength(2)
    expect(graphs[0].code).toBe('FB260_MainGraph')
    expect(graphs[0].isLeaf).toBe(true)
    expect(graphs[0].showStatus).toBe(true)
    expect(graphs[1].runtimeStatus).toBe('Error')
    expect(graphs.every(graph => graph.children.length === 0)).toBe(true)
  })

  it('matches GRAPH names in keyword search while keeping ancestors', () => {
    const projected = flatten(projectMonitorTree(nodes, 'homegraph'))

    expect(projected.map(item => item.nodeId)).toEqual([
      'line:10',
      'process:20',
      'station:30',
      'substation:3001',
      'graph:7002',
    ])
  })

  it('keeps projected node data extensible for tree component bookkeeping', () => {
    const tree = projectMonitorTree(nodes)
    const projected = flatten(tree)

    expect(projected).toHaveLength(7)
    expect(() => Object.defineProperty(projected[0], '$treeNodeId', {
      configurable: true,
      value: 1,
    })).not.toThrow()
    expect(Object.isExtensible(projected[0].children)).toBe(true)
  })

  it('shows runtime status on work positions and GRAPH leaves only', () => {
    const projected = flatten(projectMonitorTree(nodes))

    expect(projected.map(item => (
      'showStatus' in item ? item.showStatus : undefined
    ))).toEqual([false, false, false, true, true, true, true])
  })

  it.each([
    ['duplicate-node', [...nodes, { ...nodes[3] }], 'MonitorTreeDuplicateNodeId'],
    ['missing-parent', [{ ...nodes[3], parentNodeId: 'station:missing' }], 'MonitorTreeParentMissing'],
    ['unknown-type', [{ ...nodes[3], nodeType: 'Device' as any }], 'MonitorTreeNodeTypeInvalid'],
    ['graph-under-station', nodes.map((item, index) => index === 5 ? { ...item, parentNodeId: 'station:30' } : item), 'MonitorTreeParentInvalid'],
    ['cycle', [
      node('station:30', 'Station', 'substation:3001', '30', 'OP020', '缓存上料', 1, 0, 'Ready'),
      node('substation:3001', 'SubStation', 'station:30', '3001', 'OP020-01', '主工位', 0, 0, 'Ready'),
    ], 'MonitorTreeCycle'],
    ['conflicting-entity', [
      ...nodes,
      node('substation:other', 'SubStation', 'station:other', '3001', 'OP020-01', '冲突工位', 0, 0, 'Ready'),
    ], 'MonitorTreeEntityConflict'],
  ])('rejects %s rows with a stable issue code', (_, invalid, code) => {
    const data = invalid as LineUpMonitorNavigationNode[]
    const issues = validateMonitorTreeNodes(data)
    const projectedIds = flatten(projectMonitorTree(data)).map(item => item.nodeId)

    expect(issues.map(issue => issue.code)).toContain(code)
    if (code === 'MonitorTreeDuplicateNodeId') {
      expect(projectedIds).not.toContain('substation:3001')
    }
    if (code === 'MonitorTreeEntityConflict') {
      expect(projectedIds).not.toContain('substation:3001')
    }
    if (code === 'MonitorTreeParentInvalid') {
      expect(projectedIds).not.toContain('graph:7001')
    }
  })
})

describe('createLineUpMonitorTreeSearch', () => {
  it('emits only the latest trimmed query without changing identity text', () => {
    vi.useFakeTimers()
    const emitted: string[] = []
    const search = createLineUpMonitorTreeSearch(value => emitted.push(value), 250)

    search.schedule(' OP020 ')
    search.schedule(' OP020-01 ')
    vi.advanceTimersByTime(249)
    expect(emitted).toEqual([])
    vi.advanceTimersByTime(1)
    expect(emitted).toEqual(['OP020-01'])
    search.dispose()
    vi.useRealTimers()
  })
})

function node(
  nodeId: string,
  nodeType: LineUpMonitorNavigationNode['nodeType'],
  parentNodeId: string | null,
  entityId: string,
  code: string,
  name: string,
  descendantCount: number,
  issueCount: number,
  runtimeStatus: LineUpMonitorNavigationNode['runtimeStatus'],
): LineUpMonitorNavigationNode {
  return {
    nodeId,
    nodeType,
    parentNodeId,
    entityId,
    code,
    name,
    sortOrder: 0,
    descendantCount,
    issueCount,
    runtimeStatus,
    publishedBindingCount: runtimeStatus === 'Unconfigured' ? 0 : 1,
  }
}

function flatten(tree: ReturnType<typeof projectMonitorTree>): ReturnType<typeof projectMonitorTree> {
  return tree.flatMap(item => [item, ...flatten(item.children)])
}
