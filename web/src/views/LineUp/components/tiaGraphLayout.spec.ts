import type { LineUpGraphPreviewSequenceInput } from './tiaGraphLayout'
import type { LineUpImportAction } from '@/api/modules/LineUp/types'
import { describe, expect, it } from 'vitest'
import { buildTiaGraphLayout, buildTiaParallelRails } from './tiaGraphLayout'

function node(sourceKey: string, nodeKind: 'Step' | 'Transition', number: number, extras: Partial<LineUpGraphPreviewSequenceInput['nodes'][number]> = {}): LineUpGraphPreviewSequenceInput['nodes'][number] {
  return {
    nodeKind,
    sourceKey,
    sourceName: `${nodeKind}-${number}`,
    displayName: `${nodeKind}-${number}`,
    number,
    isInitial: false,
    isFinal: false,
    comment: '',
    actionGroupTitle: '',
    logicNetworks: [],
    isMissing: false,
    isJump: false,
    ...extras,
  }
}

function edge(sourceKey: string, fromSourceKey: string, toSourceKey: string, edgeKind = 'Sequence', branchOrder: number | null = null): LineUpGraphPreviewSequenceInput['edges'][number] {
  return {
    sourceKey,
    fromSourceKey,
    toSourceKey,
    fromNodeKind: fromSourceKey.startsWith('s') ? 'Step' : 'Transition',
    toNodeKind: toSourceKey.startsWith('s') ? 'Step' : 'Transition',
    edgeKind,
    branchOrder,
  }
}

const branchedSequence: LineUpGraphPreviewSequenceInput = {
  sourceKey: 'sequence:main',
  sourceName: '主流程',
  displayName: '主流程',
  nodes: [
    node('s10', 'Step', 10, { isInitial: true }),
    node('t1', 'Transition', 1),
    node('s20', 'Step', 20),
    node('s30', 'Step', 30),
    node('t2', 'Transition', 2),
    node('t3', 'Transition', 3),
    node('s40', 'Step', 40, { isFinal: true }),
  ],
  edges: [
    edge('e1', 's10', 't1'),
    edge('e2', 't1', 's20', 'SelectionSplit', 0),
    edge('e3', 't1', 's30', 'SelectionSplit', 1),
    edge('e4', 's20', 't2'),
    edge('e5', 's30', 't3'),
    edge('e6', 't2', 's40', 'SelectionMerge', 0),
    edge('e7', 't3', 's40', 'SelectionMerge', 1),
  ],
}

describe('buildTiaGraphLayout', () => {
  it('connects every edge to an existing laid-out node', () => {
    const layout = buildTiaGraphLayout(branchedSequence)
    const ids = new Set(layout.nodes.map(item => item.id))

    for (const item of layout.edges) {
      expect(ids.has(item.source)).toBe(true)
      expect(ids.has(item.target)).toBe(true)
    }
  })

  it('places branch targets on separate same-rank lanes', () => {
    const layout = buildTiaGraphLayout(branchedSequence)
    const left = layout.nodes.find(item => item.id === 's20')!
    const right = layout.nodes.find(item => item.id === 's30')!

    expect(left.x).not.toBe(right.x)
    expect(left.y).toBe(right.y)
    expect(Math.abs(left.x - right.x)).toBeGreaterThanOrEqual(150)
  })

  it('staggers split and merge rails per branch so parallel condition lines stay distinguishable', () => {
    // 39fc742 起：同源选择分支/同目标汇合各占一条水平轨（RAIL_STAGGER 错层），
    // 共用一条 railY 会让两条条件线完全重合、无法分辨哪条通往哪一步；并行边仍共享双轨
    const layout = buildTiaGraphLayout(branchedSequence)
    const split = layout.edges.filter(item => item.kind === 'SelectionSplit')
    const merge = layout.edges.filter(item => item.kind === 'SelectionMerge')

    expect(split).toHaveLength(2)
    expect(new Set(split.map(item => item.vertices[0]?.x)).size).toBe(1)
    expect(new Set(split.map(item => item.vertices[0]?.y)).size).toBe(2)

    expect(merge).toHaveLength(2)
    expect(new Set(merge.map(item => item.vertices.at(-1)?.x)).size).toBe(1)
    expect(new Set(merge.map(item => item.vertices.at(-1)?.y)).size).toBe(2)
  })

  it('marks jump edges as dashed and remains cycle-safe', () => {
    const sequence: LineUpGraphPreviewSequenceInput = {
      sourceKey: 'sequence:cycle',
      sourceName: '循环',
      displayName: '循环',
      nodes: [
        node('s1', 'Step', 1, { isInitial: true }),
        node('t1', 'Transition', 1),
        node('s2', 'Step', 2),
        node('t2', 'Transition', 2, { isJump: true }),
      ],
      edges: [
        edge('c1', 's1', 't1'),
        edge('c2', 't1', 's2'),
        edge('c3', 's2', 't2'),
        edge('c4', 't2', 's1', 'Jump'),
      ],
    }

    const layout = buildTiaGraphLayout(sequence)
    const jump = layout.edges.find(item => item.kind === 'Jump')!

    expect(layout.nodes).toHaveLength(4)
    expect(jump.dashed).toBe(true)
    expect(jump.label).toBe('→ S1')
  })

  it('is deterministic and drops only edges with missing endpoints', () => {
    const sequence: LineUpGraphPreviewSequenceInput = {
      ...branchedSequence,
      edges: [...branchedSequence.edges, edge('missing', 't1', 's999')],
    }

    const first = buildTiaGraphLayout(sequence)
    const second = buildTiaGraphLayout(structuredClone(sequence))

    expect(first).toEqual(second)
    expect(first.edges.some(item => item.id === 'missing')).toBe(false)
    expect(first.omittedEdgeCount).toBe(1)
  })

  it('keeps a long single-lane process narrow instead of stretching the panel', () => {
    const nodes = Array.from({ length: 400 }, (_, index) =>
      node(
        index % 2 === 0 ? `s${index}` : `t${index}`,
        index % 2 === 0 ? 'Step' : 'Transition',
        index,
        { isInitial: index === 0, isFinal: index === 399 },
      ))
    const edges = nodes.slice(1).map((item, index) =>
      edge(`long-${index}`, nodes[index].sourceKey, item.sourceKey),
    )
    const layout = buildTiaGraphLayout({
      sourceKey: 'sequence:long',
      sourceName: '长流程',
      displayName: '长流程',
      nodes,
      edges,
    })

    expect(layout.nodes).toHaveLength(400)
    expect(layout.edges).toHaveLength(399)
    expect(layout.width).toBe(360)
    expect(layout.height).toBeGreaterThan(39_000)
  })

  it('keeps Selection branches single-rail and projects Parallel groups as shared double rails', () => {
    expect(buildTiaParallelRails(buildTiaGraphLayout(branchedSequence))).toEqual([])

    const parallel = {
      ...branchedSequence,
      edges: branchedSequence.edges.map(item => ({
        ...item,
        edgeKind: item.edgeKind === 'SelectionSplit'
          ? 'ParallelSplit'
          : item.edgeKind === 'SelectionMerge'
            ? 'ParallelMerge'
            : item.edgeKind,
      })),
    }
    const layout = buildTiaGraphLayout(parallel)
    const rails = buildTiaParallelRails(layout)

    expect(rails).toHaveLength(2)
    expect(rails.map(item => item.kind)).toEqual(['ParallelSplit', 'ParallelMerge'])
    for (const rail of rails) {
      expect(rail.x2).toBeGreaterThan(rail.x1)
      expect(rail.secondaryY - rail.primaryY).toBe(6)
      const groupedEdges = layout.edges.filter(edge => edge.kind === rail.kind)
      expect(new Set(groupedEdges.map(edge => edge.vertices[0].y)).size).toBe(1)
      expect(rail.edgeSourceKeys).toEqual(groupedEdges.map(edge => edge.id))
    }
  })

  it('keeps topology dimensions unchanged when exact selection actions are supplied', () => {
    const action: LineUpImportAction = {
      sourceKey: 'action:s20:1',
      stepSourceKey: 's20',
      sourceName: 'A1',
      displayName: 'A1',
      ordinal: 1,
      stepNumber: 20,
      qualifier: 'N',
      event: null,
      interlock: null,
      tokens: ['Motor := TRUE'],
      actionText: 'Motor := TRUE',
    }
    const withoutActions = buildTiaGraphLayout(branchedSequence)
    const withActions = buildTiaGraphLayout({ ...branchedSequence, actions: [action] })

    expect(withActions).toEqual(withoutActions)
  })

  it('keeps a 77-step one-lane sequence width bounded', () => {
    const nodes = Array.from({ length: 153 }, (_, index) => {
      const isStep = index % 2 === 0
      return node(
        isStep ? `s${index / 2 + 1}` : `t${(index + 1) / 2}`,
        isStep ? 'Step' : 'Transition',
        isStep ? index / 2 + 1 : (index + 1) / 2,
        { isInitial: index === 0, isFinal: index === 152 },
      )
    })
    const edges = nodes.slice(1).map((item, index) =>
      edge(`one-lane-${index}`, nodes[index].sourceKey, item.sourceKey),
    )

    const layout = buildTiaGraphLayout({
      sourceKey: 'sequence:77-steps',
      sourceName: '77 Steps',
      displayName: '77 Steps',
      nodes,
      actions: [],
      edges,
    })

    expect(layout.nodes.filter(item => item.kind === 'Step')).toHaveLength(77)
    expect(layout.width).toBe(360)
  })

  it('keeps 5-way selection branches on distinct rails instead of clamping them onto one Y', () => {
    // 旧缺陷：RAIL_STAGGER=14 的错层被 max(srcBottom+12) 钳制，第 4/5 条分支
    // 塌到同一边界 Y，转换条件连线完全重合（分支越多越容易触发）。
    const seq: LineUpGraphPreviewSequenceInput = {
      sourceKey: 'sequence:probeA',
      sourceName: 'probeA',
      displayName: 'probeA',
      nodes: [
        node('s1', 'Step', 1, { isInitial: true }),
        node('t1', 'Transition', 1),
        node('t2', 'Transition', 2),
        node('t3', 'Transition', 3),
        node('t4', 'Transition', 4),
        node('t5', 'Transition', 5),
        node('s2', 'Step', 2),
        node('s3', 'Step', 3),
        node('s4', 'Step', 4),
        node('s5', 'Step', 5),
        node('s6', 'Step', 6),
      ],
      edges: [
        edge('e1', 's1', 't1', 'SelectionSplit', 0),
        edge('e2', 's1', 't2', 'SelectionSplit', 1),
        edge('e3', 's1', 't3', 'SelectionSplit', 2),
        edge('e4', 's1', 't4', 'SelectionSplit', 3),
        edge('e5', 's1', 't5', 'SelectionSplit', 4),
        edge('e6', 't1', 's2', 'Sequence'),
        edge('e7', 't2', 's3', 'Sequence'),
        edge('e8', 't3', 's4', 'Sequence'),
        edge('e9', 't4', 's5', 'Sequence'),
        edge('e10', 't5', 's6', 'Sequence'),
      ],
    }

    const layout = buildTiaGraphLayout(seq)
    const splitYs = layout.edges.filter(item => item.kind === 'SelectionSplit').map(item => item.vertices[0]?.y)

    expect(splitYs).toHaveLength(5)
    expect(new Set(splitYs).size).toBe(5)
    expectNoRailOverlaps(layout)
  })

  it('separates selection-merge rails from cross-column sequence lines sharing one gap', () => {
    // 旧缺陷：Merge 轨与跨列 Sequence 走廊线的 Y 都锚定「层隙中点」独立计算，
    // Transition→Step 层隙里 merge lane0 与 corridor lane0 恒同 Y，x 交叠即重合。
    const seq: LineUpGraphPreviewSequenceInput = {
      sourceKey: 'sequence:probeB',
      sourceName: 'probeB',
      displayName: 'probeB',
      nodes: [
        node('s1', 'Step', 1, { isInitial: true }),
        node('t1', 'Transition', 1),
        node('s2', 'Step', 2),
        node('s3', 'Step', 3),
        node('s6', 'Step', 6),
        node('t2', 'Transition', 2),
        node('t3', 'Transition', 3),
        node('t4', 'Transition', 4),
        node('s4', 'Step', 4, { isFinal: true }),
        node('s9', 'Step', 9),
      ],
      edges: [
        edge('e0', 's1', 't1', 'Sequence'),
        edge('e1', 't1', 's2', 'SelectionSplit', 0),
        edge('e2', 't1', 's3', 'SelectionSplit', 1),
        edge('e3', 't1', 's6', 'SelectionSplit', 2),
        edge('e4', 's2', 't2', 'Sequence'),
        edge('e5', 's3', 't3', 'Sequence'),
        edge('e6', 's6', 't4', 'Sequence'),
        edge('e7', 't2', 's4', 'SelectionMerge', 0),
        edge('e8', 't3', 's4', 'SelectionMerge', 1),
        edge('e9', 't4', 's4', 'SelectionMerge', 2),
        edge('e10', 't2', 's9', 'Sequence'),
        edge('e11', 't3', 's9', 'Sequence'),
      ],
    }

    expectNoRailOverlaps(buildTiaGraphLayout(seq))
  })
})

/** 通用不变量：任何两条 x 区间交叠的水平轨道线，Y 间距必须 ≥ 10px（不可分辨的重合）。 */
function expectNoRailOverlaps(layout: ReturnType<typeof buildTiaGraphLayout>) {
  const rails = layout.edges
    .filter(edge => edge.vertices.length >= 2)
    .map((edge) => {
      const railY = edge.vertices[0].y
      const railPoints = edge.vertices.filter(point => point.y === railY)
      return {
        id: edge.id,
        y: railY,
        x1: Math.min(...railPoints.map(point => point.x)),
        x2: Math.max(...railPoints.map(point => point.x)),
      }
    })
    .filter(rail => rail.x2 > rail.x1)

  for (let i = 0; i < rails.length; i++) {
    for (let j = i + 1; j < rails.length; j++) {
      const a = rails[i]
      const b = rails[j]
      if (a.x1 < b.x2 - 0.5 && b.x1 < a.x2 - 0.5) {
        expect(Math.abs(a.y - b.y), `rails ${a.id}@${a.y} vs ${b.id}@${b.y} overlap in x`).toBeGreaterThanOrEqual(10)
      }
    }
  }
}
