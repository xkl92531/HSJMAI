import type { LineUpMonitorNavigationNode, LineUpMonitorNodeType } from '@/api/modules/LineUp/monitor'

export interface LineUpMonitorTreeIssue {
  code: 'MonitorTreeCycle' | 'MonitorTreeDuplicateNodeId' | 'MonitorTreeEntityConflict' | 'MonitorTreeNodeTypeInvalid' | 'MonitorTreeParentInvalid' | 'MonitorTreeParentMissing'
  nodeId: string
}

export interface LineUpMonitorTreeNode extends LineUpMonitorNavigationNode {
  children: readonly LineUpMonitorTreeNode[]
  isLeaf: boolean
  showStatus: boolean
}

const nodeTypes: readonly LineUpMonitorNodeType[] = ['Line', 'Process', 'Station', 'SubStation', 'Graph']
const parentType: Record<LineUpMonitorNodeType, LineUpMonitorNodeType | null> = {
  Line: null,
  Process: 'Line',
  Station: 'Process',
  SubStation: 'Station',
  Graph: 'SubStation',
}

export function validateMonitorTreeNodes(nodes: readonly LineUpMonitorNavigationNode[]): readonly LineUpMonitorTreeIssue[] {
  const issues: LineUpMonitorTreeIssue[] = []
  const invalid = new Set<string>()
  const byNodeId = new Map<string, LineUpMonitorNavigationNode[]>()
  const byEntity = new Map<string, LineUpMonitorNavigationNode[]>()

  for (const node of nodes) {
    const nodeRows = byNodeId.get(node.nodeId) ?? []
    nodeRows.push(node)
    byNodeId.set(node.nodeId, nodeRows)
    const entityKey = `${node.nodeType}:${node.entityId}`
    const entityRows = byEntity.get(entityKey) ?? []
    entityRows.push(node)
    byEntity.set(entityKey, entityRows)
    if (!nodeTypes.includes(node.nodeType)) {
      reject(issues, invalid, node.nodeId, 'MonitorTreeNodeTypeInvalid')
    }
  }

  for (const rows of byNodeId.values()) {
    if (rows.length > 1) {
      rows.forEach(row => reject(issues, invalid, row.nodeId, 'MonitorTreeDuplicateNodeId'))
    }
  }
  for (const rows of byEntity.values()) {
    const identities = new Set(rows.map(row => `${row.nodeId}\u0000${row.parentNodeId ?? ''}`))
    if (identities.size > 1) {
      rows.forEach(row => reject(issues, invalid, row.nodeId, 'MonitorTreeEntityConflict'))
    }
  }

  const unique = new Map<string, LineUpMonitorNavigationNode>()
  for (const [nodeId, rows] of byNodeId) {
    if (rows.length === 1 && !invalid.has(nodeId)) {
      unique.set(nodeId, rows[0])
    }
  }
  for (const node of unique.values()) {
    const expected = parentType[node.nodeType]
    if (expected === null) {
      if (node.parentNodeId !== null) {
        reject(issues, invalid, node.nodeId, 'MonitorTreeParentInvalid')
      }
      continue
    }
    if (!node.parentNodeId || !unique.has(node.parentNodeId)) {
      reject(issues, invalid, node.nodeId, 'MonitorTreeParentMissing')
      continue
    }
    if (unique.get(node.parentNodeId)?.nodeType !== expected) {
      reject(issues, invalid, node.nodeId, 'MonitorTreeParentInvalid')
    }
  }

  const visitState = new Map<string, 'visiting' | 'visited'>()
  const stack: string[] = []
  const visit = (nodeId: string) => {
    if (visitState.get(nodeId) === 'visited') {
      return
    }
    if (visitState.get(nodeId) === 'visiting') {
      const cycleStart = stack.indexOf(nodeId)
      for (const cycleNodeId of stack.slice(Math.max(0, cycleStart))) {
        reject(issues, invalid, cycleNodeId, 'MonitorTreeCycle')
      }
      return
    }
    visitState.set(nodeId, 'visiting')
    stack.push(nodeId)
    const parent = unique.get(nodeId)?.parentNodeId
    if (parent && unique.has(parent)) {
      visit(parent)
    }
    stack.pop()
    visitState.set(nodeId, 'visited')
  }
  unique.forEach((_, nodeId) => visit(nodeId))

  let changed = true
  while (changed) {
    changed = false
    for (const node of unique.values()) {
      if (!invalid.has(node.nodeId) && node.parentNodeId && invalid.has(node.parentNodeId)) {
        reject(issues, invalid, node.nodeId, 'MonitorTreeParentInvalid')
        changed = true
      }
    }
  }
  return Object.freeze([...issues])
}

// 2026-09-04：sequenceShortcuts 从树 data 中移除（点击工站不再替换整树 data，防 el-tree
// 整树重建丢滚动）。2026-09：顺控切换改由右侧 GraphPreviewPanel 的 tab 承担
// （带运行状态点），树内不再渲染顺控快捷区；Graph 子节点（导航数据自带）成为树的叶子层。

export function projectMonitorTree(
  nodes: readonly LineUpMonitorNavigationNode[],
  keyword = '',
): readonly LineUpMonitorTreeNode[] {
  const issues = validateMonitorTreeNodes(nodes)
  const invalid = new Set(issues.map(issue => issue.nodeId))
  const valid = nodes.filter(node => !invalid.has(node.nodeId))
  const children = new Map<string | null, LineUpMonitorNavigationNode[]>()
  valid.forEach((node) => {
    const siblings = children.get(node.parentNodeId) ?? []
    siblings.push(node)
    children.set(node.parentNodeId, siblings)
  })
  children.forEach(siblings => siblings.sort(compareNodes))
  const normalizedKeyword = keyword.trim().toLocaleLowerCase()

  const build = (node: LineUpMonitorNavigationNode): LineUpMonitorTreeNode | null => {
    const projectedChildren = (children.get(node.nodeId) ?? [])
      .map(build)
      .filter((child): child is LineUpMonitorTreeNode => child !== null)
    const matches = normalizedKeyword.length === 0
      || `${node.code}\u0000${node.name}`.toLocaleLowerCase().includes(normalizedKeyword)
    if (!matches && projectedChildren.length === 0) {
      return null
    }
    return {
      ...node,
      children: projectedChildren,
      isLeaf: node.nodeType === 'Graph',
      showStatus: node.nodeType === 'SubStation' || node.nodeType === 'Graph',
    }
  }

  return (children.get(null) ?? []).map(build).filter(
    (node): node is LineUpMonitorTreeNode => node !== null,
  )
}

export function createLineUpMonitorTreeSearch(
  emit: (keyword: string) => void,
  delayMs = 300,
): { schedule: (keyword: string) => void, dispose: () => void } {
  let timer: ReturnType<typeof setTimeout> | null = null
  return {
    schedule(keyword: string) {
      if (timer !== null) {
        clearTimeout(timer)
      }
      timer = setTimeout(() => {
        timer = null
        emit(keyword.trim())
      }, Math.max(0, delayMs))
    },
    dispose() {
      if (timer !== null) {
        clearTimeout(timer)
      }
      timer = null
    },
  }
}

function reject(
  issues: LineUpMonitorTreeIssue[],
  invalid: Set<string>,
  nodeId: string,
  code: LineUpMonitorTreeIssue['code'],
) {
  if (!issues.some(issue => issue.nodeId === nodeId && issue.code === code)) {
    issues.push({ code, nodeId })
  }
  invalid.add(nodeId)
}

function compareNodes(left: LineUpMonitorNavigationNode, right: LineUpMonitorNavigationNode) {
  return left.sortOrder - right.sortOrder
    || ordinal(left.code, right.code)
    || ordinal(left.name, right.name)
    || ordinal(left.nodeId, right.nodeId)
}

function ordinal(left: string, right: string) {
  return left < right ? -1 : left > right ? 1 : 0
}
