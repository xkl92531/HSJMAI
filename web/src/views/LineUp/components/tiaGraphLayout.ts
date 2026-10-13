import type { LineUpImportAction, LineUpImportEdge, LineUpImportNode, LineUpImportSequence } from '@/api/modules/LineUp/types'

export type LineUpGraphPreviewNodeInput = LineUpImportNode
export type LineUpGraphPreviewEdgeInput = LineUpImportEdge

export interface LineUpGraphPreviewSequenceInput extends Omit<LineUpImportSequence, 'actions'> {
  actions?: LineUpImportAction[]
  viewerSchemaVersion?: number
  viewerCapabilities?: string[]
  viewerWarnings?: string[]
}

export interface TiaLayoutPoint {
  x: number
  y: number
}

export interface TiaLayoutNode {
  id: string
  kind: 'Step' | 'Transition'
  x: number
  y: number
  width: number
  height: number
  number: number
  label: string
  conditionSummary: string
  isInitial: boolean
  isFinal: boolean
  isMissing: boolean
}

export interface TiaLayoutEdge {
  id: string
  source: string
  target: string
  kind: string
  branchOrder: number | null
  vertices: TiaLayoutPoint[]
  dashed: boolean
  label: string
}

export interface TiaLayoutResult {
  nodes: TiaLayoutNode[]
  edges: TiaLayoutEdge[]
  width: number
  height: number
  omittedEdgeCount: number
}

export interface TiaParallelRail {
  key: string
  kind: 'ParallelSplit' | 'ParallelMerge'
  primaryY: number
  secondaryY: number
  x1: number
  x2: number
  edgeSourceKeys: string[]
}

export interface TiaGraphViewState {
  zoom: number
  translateX: number
  translateY: number
  selectedNodeKey: string
}

const STEP_WIDTH = 150
const STEP_HEIGHT = 64
const TRANSITION_WIDTH = 72
const TRANSITION_HEIGHT = 22
const LANE_PITCH = 210
const RANK_PITCH = 150
const MARGIN_X = 72
const MARGIN_Y = 56

interface Component {
  id: number
  nodeIds: string[]
  sortKey: string
}

function compareText(left: string, right: string) {
  return left.localeCompare(right, 'zh-CN')
}

function compareNodes(
  left: LineUpGraphPreviewNodeInput,
  right: LineUpGraphPreviewNodeInput,
) {
  if (left.isInitial !== right.isInitial) {
    return left.isInitial ? -1 : 1
  }
  if (left.number !== right.number) {
    return left.number - right.number
  }
  if (left.nodeKind !== right.nodeKind) {
    return compareText(left.nodeKind, right.nodeKind)
  }
  return compareText(left.sourceKey, right.sourceKey)
}

function compareEdges(left: LineUpGraphPreviewEdgeInput, right: LineUpGraphPreviewEdgeInput) {
  const leftOrder = left.branchOrder ?? Number.MAX_SAFE_INTEGER
  const rightOrder = right.branchOrder ?? Number.MAX_SAFE_INTEGER
  if (leftOrder !== rightOrder) {
    return leftOrder - rightOrder
  }
  if (left.fromSourceKey !== right.fromSourceKey) {
    return compareText(left.fromSourceKey, right.fromSourceKey)
  }
  if (left.toSourceKey !== right.toSourceKey) {
    return compareText(left.toSourceKey, right.toSourceKey)
  }
  return compareText(left.sourceKey, right.sourceKey)
}

function buildStronglyConnectedComponents(
  nodeIds: string[],
  adjacency: Map<string, string[]>,
): { components: Component[], componentByNode: Map<string, number> } {
  let nextIndex = 0
  const indices = new Map<string, number>()
  const lowLinks = new Map<string, number>()
  const stack: string[] = []
  const onStack = new Set<string>()
  const groups: string[][] = []

  const visit = (nodeId: string) => {
    indices.set(nodeId, nextIndex)
    lowLinks.set(nodeId, nextIndex)
    nextIndex += 1
    stack.push(nodeId)
    onStack.add(nodeId)

    for (const target of adjacency.get(nodeId) ?? []) {
      if (!indices.has(target)) {
        visit(target)
        lowLinks.set(nodeId, Math.min(lowLinks.get(nodeId)!, lowLinks.get(target)!))
      }
      else if (onStack.has(target)) {
        lowLinks.set(nodeId, Math.min(lowLinks.get(nodeId)!, indices.get(target)!))
      }
    }

    if (lowLinks.get(nodeId) !== indices.get(nodeId)) {
      return
    }

    const group: string[] = []
    while (stack.length > 0) {
      const member = stack.pop()!
      onStack.delete(member)
      group.push(member)
      if (member === nodeId) {
        break
      }
    }
    groups.push(group.sort(compareText))
  }

  for (const nodeId of [...nodeIds].sort(compareText)) {
    if (!indices.has(nodeId)) {
      visit(nodeId)
    }
  }

  const components = groups
    .map(nodeIdsInGroup => ({ nodeIds: nodeIdsInGroup, sortKey: nodeIdsInGroup[0] }))
    .sort((left, right) => compareText(left.sortKey, right.sortKey))
    .map((group, id) => ({ ...group, id }))
  const componentByNode = new Map<string, number>()
  for (const component of components) {
    for (const nodeId of component.nodeIds) {
      componentByNode.set(nodeId, component.id)
    }
  }
  return { components, componentByNode }
}

function buildRanks(
  nodes: LineUpGraphPreviewNodeInput[],
  edges: LineUpGraphPreviewEdgeInput[],
) {
  const adjacency = new Map(nodes.map(node => [node.sourceKey, [] as string[]]))
  for (const edge of edges) {
    adjacency.get(edge.fromSourceKey)!.push(edge.toSourceKey)
  }
  for (const targets of adjacency.values()) {
    targets.sort(compareText)
  }

  const { components, componentByNode } = buildStronglyConnectedComponents(
    nodes.map(node => node.sourceKey),
    adjacency,
  )
  const outgoing = new Map(components.map(component => [component.id, new Set<number>()]))
  const indegree = new Map(components.map(component => [component.id, 0]))
  for (const edge of edges) {
    const source = componentByNode.get(edge.fromSourceKey)!
    const target = componentByNode.get(edge.toSourceKey)!
    if (source === target || outgoing.get(source)!.has(target)) {
      continue
    }
    outgoing.get(source)!.add(target)
    indegree.set(target, indegree.get(target)! + 1)
  }

  const componentById = new Map(components.map(component => [component.id, component]))
  const queue = components
    .filter(component => indegree.get(component.id) === 0)
    .map(component => component.id)
    .sort((left, right) => compareText(componentById.get(left)!.sortKey, componentById.get(right)!.sortKey))
  const componentRank = new Map(components.map(component => [component.id, 0]))
  while (queue.length > 0) {
    const current = queue.shift()!
    const nextRank = componentRank.get(current)! + componentById.get(current)!.nodeIds.length
    const targets = [...outgoing.get(current)!].sort((left, right) =>
      compareText(componentById.get(left)!.sortKey, componentById.get(right)!.sortKey),
    )
    for (const target of targets) {
      componentRank.set(target, Math.max(componentRank.get(target)!, nextRank))
      indegree.set(target, indegree.get(target)! - 1)
      if (indegree.get(target) === 0) {
        queue.push(target)
        queue.sort((left, right) =>
          compareText(componentById.get(left)!.sortKey, componentById.get(right)!.sortKey),
        )
      }
    }
  }

  const ranks = new Map<string, number>()
  for (const component of components) {
    component.nodeIds.forEach((nodeId, index) => {
      ranks.set(nodeId, componentRank.get(component.id)! + index)
    })
  }
  return ranks
}

function incomingOrder(
  nodeId: string,
  incoming: Map<string, LineUpGraphPreviewEdgeInput[]>,
) {
  const first = incoming.get(nodeId)?.[0]
  return first?.branchOrder ?? Number.MAX_SAFE_INTEGER
}

function jumpLabel(target: LineUpGraphPreviewNodeInput | undefined) {
  if (!target) {
    return ''
  }
  return `→ ${target.nodeKind === 'Step' ? 'S' : 'T'}${target.number}`
}

export function buildTiaGraphLayout(sequence: LineUpGraphPreviewSequenceInput): TiaLayoutResult {
  const uniqueNodes = new Map<string, LineUpGraphPreviewNodeInput>()
  for (const item of sequence.nodes) {
    if ((item.nodeKind === 'Step' || item.nodeKind === 'Transition') && !uniqueNodes.has(item.sourceKey)) {
      uniqueNodes.set(item.sourceKey, item)
    }
  }
  const nodes = [...uniqueNodes.values()].sort(compareNodes)
  const validEdges = sequence.edges
    .filter(item => uniqueNodes.has(item.fromSourceKey) && uniqueNodes.has(item.toSourceKey))
    .sort(compareEdges)
  const flowEdges = validEdges.filter(item => item.edgeKind !== 'Jump')
  const ranks = buildRanks(nodes, flowEdges)
  const incoming = new Map(nodes.map(item => [item.sourceKey, [] as LineUpGraphPreviewEdgeInput[]]))
  for (const item of validEdges) {
    incoming.get(item.toSourceKey)!.push(item)
  }
  for (const items of incoming.values()) {
    items.sort(compareEdges)
  }

  const layers = new Map<number, LineUpGraphPreviewNodeInput[]>()
  for (const item of nodes) {
    const rank = ranks.get(item.sourceKey) ?? 0
    if (!layers.has(rank)) {
      layers.set(rank, [])
    }
    layers.get(rank)!.push(item)
  }

  const relativePositions = new Map<string, { x: number, y: number, width: number, height: number }>()
  // 列对齐（重心法）：层内初始序仍按入边序，再做 下/上/下 三趟重心扫描，
  // 让同一分支链尽量保持同一列 → 顺序边为竖直直线（同列不加拐点）。
  const layerKeys = new Map<number, string[]>()
  const initialIndex = new Map<string, number>()
  const rankList = [...layers.keys()].sort((left, right) => left - right)
  const outgoing = new Map(nodes.map(item => [item.sourceKey, [] as LineUpGraphPreviewEdgeInput[]]))
  for (const item of validEdges) {
    outgoing.get(item.fromSourceKey)!.push(item)
  }
  for (const rank of rankList) {
    const layer = layers.get(rank)!
    layer.sort((left, right) => {
      const order = incomingOrder(left.sourceKey, incoming) - incomingOrder(right.sourceKey, incoming)
      return order || compareNodes(left, right)
    })
    const keys = layer.map(item => item.sourceKey)
    keys.forEach((key, index) => initialIndex.set(key, index))
    layerKeys.set(rank, keys)
  }
  const columnOf = new Map<string, number>()
  const sweep = (down: boolean) => {
    const orderedRanks = down ? rankList : [...rankList].reverse()
    for (const rank of orderedRanks) {
      const keys = layerKeys.get(rank)!
      const bary = (key: string) => {
        const neighborKeys = (down ? incoming.get(key)! : outgoing.get(key)!)
          .map(edge => down ? edge.fromSourceKey : edge.toSourceKey)
        const cols = neighborKeys
          .map(neighbor => columnOf.get(neighbor))
          .filter((col): col is number => col !== undefined)
        if (!cols.length) {
          return initialIndex.get(key) ?? 0
        }
        return cols.reduce((sum, col) => sum + col, 0) / cols.length
      }
      keys.sort((left, right) => bary(left) - bary(right)
        || (initialIndex.get(left) ?? 0) - (initialIndex.get(right) ?? 0))
      keys.forEach((key, index) => columnOf.set(key, index))
    }
  }
  sweep(true)
  sweep(false)
  sweep(true)

  // —— 节点 x 坐标与尺寸（与层距无关，先定）；y 随后按每层 gapPitch 累计 ——
  const sizeByKey = new Map<string, { width: number, height: number }>()
  const centerXByKey = new Map<string, number>()
  const rankMaxHeight = new Map<number, number>()
  let minimumX = 0
  let maximumX = 0
  for (const rank of rankList) {
    const keys = layerKeys.get(rank)!
    let maxHeight = 0
    keys.forEach((key, index) => {
      const item = uniqueNodes.get(key)!
      const width = item.nodeKind === 'Step' ? STEP_WIDTH : TRANSITION_WIDTH
      const height = item.nodeKind === 'Step' ? STEP_HEIGHT : TRANSITION_HEIGHT
      const centerX = (index - (keys.length - 1) / 2) * LANE_PITCH
      maxHeight = Math.max(maxHeight, height)
      centerXByKey.set(key, centerX)
      sizeByKey.set(key, { width, height })
      minimumX = Math.min(minimumX, centerX - width / 2)
      maximumX = Math.max(maximumX, centerX + width / 2)
    })
    rankMaxHeight.set(rank, maxHeight)
  }

  const maximumRank = Math.max(0, ...nodes.map(item => ranks.get(item.sourceKey) ?? 0))

  // —— 每层间隙的水平轨道需求（2026-09 连线重合修复：统一槽位错层）——
  // 旧实现 Split/Merge/跨列 Sequence 三组各自按「层隙中点 ± lane×14」锚定并钳制：
  // ① 分支 ≥4 条时 lane 被 max/min 钳到同一边界 Y，转换条件连线完全重合；
  // ② 三组公式独立计算，Transition→Step 层隙里 merge lane0 与走廊 lane0 恒同 Y。
  // 新实现：每个层间隙收集全部水平轨请求（并行组多条边共享一轨），槽位梯子统一分配；
  // 槽位放不下时按需加大该层 gapPitch，保证任何 x 区间交叠的两条水平轨不同 Y。
  const RAIL_PITCH = 14
  const RAIL_CLEARANCE = 10
  const RAIL_MIN_SEPARATION = 10
  interface RailRequest {
    keys: string[]
    orderX: number
    branchOrder: number | null
    groupKey: string
  }
  const railGroupOfEdge = new Map<string, string>()
  const gapRequests = new Map<number, RailRequest[]>()
  const requestByGroup = new Map<string, RailRequest>()
  for (const item of validEdges) {
    if (item.edgeKind === 'Jump') {
      continue
    }
    const fromRank = ranks.get(item.fromSourceKey) ?? 0
    const toRank = ranks.get(item.toSourceKey) ?? 0
    if (toRank < fromRank + 1) {
      continue // 同层/反向边（环内回边）走回退通道，参与跨轨避让
    }
    const sameColumn = centerXByKey.get(item.fromSourceKey) === centerXByKey.get(item.toSourceKey)
    const needsRail = item.edgeKind.endsWith('Split') || item.edgeKind.endsWith('Merge') || (item.edgeKind === 'Sequence' && !sameColumn)
    if (!needsRail) {
      continue
    }
    const gap = toRank - 1
    const shared = item.edgeKind.startsWith('Parallel')
    const groupKey = shared
      ? `${item.edgeKind}|${item.edgeKind === 'ParallelSplit' ? item.fromSourceKey : item.toSourceKey}|${gap}`
      : item.sourceKey
    railGroupOfEdge.set(item.sourceKey, groupKey)
    let request = requestByGroup.get(groupKey)
    if (!request) {
      request = {
        keys: [],
        orderX: item.edgeKind === 'SelectionMerge' || item.edgeKind === 'ParallelMerge'
          ? centerXByKey.get(item.toSourceKey)!
          : centerXByKey.get(item.fromSourceKey)!,
        branchOrder: item.branchOrder,
        groupKey,
      }
      requestByGroup.set(groupKey, request)
      if (!gapRequests.has(gap)) {
        gapRequests.set(gap, [])
      }
      gapRequests.get(gap)!.push(request)
    }
    request.keys.push(item.sourceKey)
  }
  for (const requests of gapRequests.values()) {
    requests.sort((left, right) => left.orderX - right.orderX
      || (left.branchOrder ?? Number.MAX_SAFE_INTEGER) - (right.branchOrder ?? Number.MAX_SAFE_INTEGER)
      || compareText(left.groupKey, right.groupKey))
  }

  // 层距：默认 RANK_PITCH；该层轨道多时 = 最大节点高 + 槽数×轨道间距 + 上下净空
  const gapPitch = new Map<number, number>()
  for (let gap = 0; gap < maximumRank; gap++) {
    const count = gapRequests.get(gap)?.length ?? 0
    const maxHeight = rankMaxHeight.get(gap) ?? STEP_HEIGHT
    gapPitch.set(gap, Math.max(RANK_PITCH, maxHeight + count * RAIL_PITCH + RAIL_CLEARANCE * 2))
  }
  const rankTop = new Map<number, number>()
  let cumulativeY = 0
  for (let rank = 0; rank <= maximumRank; rank++) {
    rankTop.set(rank, cumulativeY)
    cumulativeY += gapPitch.get(rank) ?? RANK_PITCH
  }

  for (const [key, size] of sizeByKey) {
    relativePositions.set(key, {
      x: centerXByKey.get(key)! - size.width / 2,
      y: rankTop.get(ranks.get(key) ?? 0) ?? 0,
      width: size.width,
      height: size.height,
    })
  }

  const offsetX = MARGIN_X - minimumX
  const layoutNodes: TiaLayoutNode[] = nodes.map((item) => {
    const position = relativePositions.get(item.sourceKey)!
    const label = item.displayName || item.sourceName || `${item.nodeKind} ${item.number}`
    return {
      id: item.sourceKey,
      kind: item.nodeKind as 'Step' | 'Transition',
      x: position.x + offsetX,
      y: position.y + MARGIN_Y,
      width: position.width,
      height: position.height,
      number: item.number,
      label,
      conditionSummary: item.nodeKind === 'Transition' ? (item.comment || label) : '',
      isInitial: item.isInitial,
      isFinal: item.isFinal,
      isMissing: item.isMissing === true,
    }
  })
  const layoutNodeById = new Map(layoutNodes.map(item => [item.id, item]))
  const jumpLaneBase = maximumX - minimumX + MARGIN_X + 48

  // 槽位梯子：每个层间隙的轨道从上往下按序分配（并行组多条边共享同一槽，
  // 保持 S7 同步分支的双轨画法；选择分支/汇合/跨列顺序线各占一槽，彼此互斥）
  const railYByGroup = new Map<string, number>()
  for (const [gap, requests] of gapRequests) {
    const top = MARGIN_Y + (rankTop.get(gap) ?? 0) + (rankMaxHeight.get(gap) ?? STEP_HEIGHT) + RAIL_CLEARANCE
    const bottom = MARGIN_Y + (rankTop.get(gap + 1) ?? 0) - RAIL_CLEARANCE
    const span = Math.max(0, bottom - top)
    const first = top + Math.max(0, (span - (requests.length - 1) * RAIL_PITCH) / 2)
    requests.forEach((request, index) => {
      railYByGroup.set(request.groupKey, first + index * RAIL_PITCH)
    })
  }

  // —— 跨轨避让：回退边（同层/反向跨列）与跳转线的水平段不得压在槽位轨/彼此上 ——
  interface RailSpan { y: number, x1: number, x2: number }
  const occupiedRails: RailSpan[] = []
  const validEdgeByKey = new Map(validEdges.map(item => [item.sourceKey, item]))
  for (const [groupKey, railY] of railYByGroup) {
    const request = requestByGroup.get(groupKey)!
    let x1 = Number.POSITIVE_INFINITY
    let x2 = Number.NEGATIVE_INFINITY
    for (const edgeKey of request.keys) {
      const item = validEdgeByKey.get(edgeKey)!
      x1 = Math.min(x1, centerXByKey.get(item.fromSourceKey)!, centerXByKey.get(item.toSourceKey)!)
      x2 = Math.max(x2, centerXByKey.get(item.fromSourceKey)!, centerXByKey.get(item.toSourceKey)!)
    }
    occupiedRails.push({ y: railY, x1, x2 })
  }
  function resolveFreeRailY(preferredY: number, lower: number, upper: number, x1: number, x2: number): number {
    const offsets = [0, RAIL_PITCH, -RAIL_PITCH, RAIL_PITCH * 2, -RAIL_PITCH * 2, RAIL_PITCH * 3, -RAIL_PITCH * 3, RAIL_PITCH * 4, -RAIL_PITCH * 4]
    for (const offset of offsets) {
      const y = preferredY + offset
      if (y < lower || y > upper) {
        continue
      }
      if (!occupiedRails.some(rail => Math.abs(rail.y - y) < RAIL_MIN_SEPARATION && rail.x1 < x2 && x1 < rail.x2)) {
        return y
      }
    }
    return preferredY
  }

  function nodeBottomY(sourceKey: string) {
    const item = uniqueNodes.get(sourceKey)!
    const height = item.nodeKind === 'Step' ? STEP_HEIGHT : TRANSITION_HEIGHT
    return MARGIN_Y + (rankTop.get(ranks.get(sourceKey) ?? 0) ?? 0) + height
  }
  function nodeTopY(sourceKey: string) {
    return MARGIN_Y + (rankTop.get(ranks.get(sourceKey) ?? 0) ?? 0)
  }

  // 回退边（同层/反向、跨列）：水平段取两锚点中点，撞轨时上下让位
  const fallbackRailY = new Map<string, number>()
  for (const item of validEdges) {
    if (item.edgeKind === 'Jump' || railGroupOfEdge.has(item.sourceKey)) {
      continue
    }
    if (centerXByKey.get(item.fromSourceKey) === centerXByKey.get(item.toSourceKey)) {
      continue
    }
    const sourceY = nodeBottomY(item.fromSourceKey)
    const targetY = nodeTopY(item.toSourceKey)
    const x1 = Math.min(centerXByKey.get(item.fromSourceKey)!, centerXByKey.get(item.toSourceKey)!)
    const x2 = Math.max(centerXByKey.get(item.fromSourceKey)!, centerXByKey.get(item.toSourceKey)!)
    const lower = Math.min(sourceY, targetY) + 8
    const upper = Math.max(sourceY, targetY) - 8
    const railY = resolveFreeRailY(sourceY + (targetY - sourceY) / 2, lower, upper, x1, x2)
    fallbackRailY.set(item.sourceKey, railY)
    occupiedRails.push({ y: railY, x1, x2 })
  }

  // 跳转边：两条水平段（源下方/目标上方）与其它轨避让；右侧跳转车道按出现序分配
  const jumpXByKey = new Map<string, number>()
  const jumpRailYByKey = new Map<string, { below: number, above: number }>()
  let jumpIndex = 0
  for (const item of validEdges) {
    if (item.edgeKind !== 'Jump') {
      continue
    }
    const jumpX = jumpLaneBase + jumpIndex * 24
    jumpIndex += 1
    jumpXByKey.set(item.sourceKey, jumpX)
    const sourceY = nodeBottomY(item.fromSourceKey)
    const targetY = nodeTopY(item.toSourceKey)
    const x1 = Math.min(centerXByKey.get(item.fromSourceKey)!, centerXByKey.get(item.toSourceKey)!, jumpX)
    const x2 = Math.max(centerXByKey.get(item.fromSourceKey)!, centerXByKey.get(item.toSourceKey)!, jumpX)
    const below = resolveFreeRailY(sourceY + 22, sourceY + 4, sourceY + 56, x1, x2)
    occupiedRails.push({ y: below, x1, x2 })
    const above = resolveFreeRailY(targetY - 22, targetY - 56, targetY - 4, x1, x2)
    occupiedRails.push({ y: above, x1, x2 })
    jumpRailYByKey.set(item.sourceKey, { below, above })
  }

  const layoutEdges: TiaLayoutEdge[] = validEdges.map((item) => {
    const source = layoutNodeById.get(item.fromSourceKey)!
    const target = layoutNodeById.get(item.toSourceKey)!
    const sourcePoint = { x: source.x + source.width / 2, y: source.y + source.height }
    const targetPoint = { x: target.x + target.width / 2, y: target.y }
    const vertices: TiaLayoutPoint[] = []

    if (item.edgeKind === 'Jump') {
      const jump = jumpRailYByKey.get(item.sourceKey) ?? { below: sourcePoint.y + 22, above: targetPoint.y - 22 }
      const jumpX = jumpXByKey.get(item.sourceKey) ?? sourcePoint.x
      vertices.push(
        { x: sourcePoint.x, y: jump.below },
        { x: jumpX, y: jump.below },
        { x: jumpX, y: jump.above },
        { x: targetPoint.x, y: jump.above },
      )
    }
    else {
      const groupKey = railGroupOfEdge.get(item.sourceKey)
      if (groupKey !== undefined) {
        // 前向边：使用所在层间隙的统一槽位（与同隙其它水平轨互斥错层）
        const railY = railYByGroup.get(groupKey)!
        vertices.push({ x: sourcePoint.x, y: railY }, { x: targetPoint.x, y: railY })
      }
      else if (sourcePoint.x !== targetPoint.x) {
        // 回退通道：同层/反向跨列边，水平段已做过跨轨避让
        const railY = fallbackRailY.get(item.sourceKey) ?? sourcePoint.y + (targetPoint.y - sourcePoint.y) / 2
        vertices.push({ x: sourcePoint.x, y: railY }, { x: targetPoint.x, y: railY })
      }
    }

    return {
      id: item.sourceKey,
      source: item.fromSourceKey,
      target: item.toSourceKey,
      kind: item.edgeKind,
      branchOrder: item.branchOrder,
      vertices,
      dashed: item.edgeKind === 'Jump',
      label: item.edgeKind === 'Jump' ? jumpLabel(uniqueNodes.get(item.toSourceKey)) : '',
    }
  })

  return {
    nodes: layoutNodes,
    edges: layoutEdges,
    width: Math.max(360, maximumX - minimumX + MARGIN_X * 2 + jumpIndex * 24),
    height: Math.max(260, MARGIN_Y * 2 + (rankTop.get(maximumRank) ?? 0) + STEP_HEIGHT),
    omittedEdgeCount: sequence.edges.length - validEdges.length,
  }
}

export function buildTiaParallelRails(layout: TiaLayoutResult): TiaParallelRail[] {
  const groups = new Map<string, TiaLayoutEdge[]>()
  for (const edge of layout.edges) {
    if (edge.kind !== 'ParallelSplit' && edge.kind !== 'ParallelMerge') {
      continue
    }
    const railY = edge.vertices[0]?.y
    if (railY === undefined) {
      continue
    }
    const anchor = edge.kind === 'ParallelSplit' ? edge.source : edge.target
    const key = `${edge.kind}:${anchor}:${railY}`
    const grouped = groups.get(key) ?? []
    grouped.push(edge)
    groups.set(key, grouped)
  }

  return [...groups.entries()].flatMap(([key, edges]) => {
    const horizontalPoints = edges.flatMap(edge => edge.vertices.filter(point => point.y === edge.vertices[0]?.y))
    const x1 = Math.min(...horizontalPoints.map(point => point.x))
    const x2 = Math.max(...horizontalPoints.map(point => point.x))
    if (!Number.isFinite(x1) || !Number.isFinite(x2) || x1 === x2) {
      return []
    }
    const kind = edges[0].kind as TiaParallelRail['kind']
    const primaryY = edges[0].vertices[0].y
    return [{
      key,
      kind,
      primaryY,
      secondaryY: primaryY + 6,
      x1,
      x2,
      edgeSourceKeys: edges.map(edge => edge.id),
    }]
  })
}
