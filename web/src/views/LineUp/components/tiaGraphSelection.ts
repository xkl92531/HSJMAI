import type { LineUpConditionRuntimeValue, LineUpMonitorDefinition, LineUpMonitorSnapshot } from '@/api/modules/LineUp/monitor'
import type { LineUpGraphLogicNetwork, LineUpImportAction, LineUpImportNode, LineUpImportSequence } from '@/api/modules/LineUp/types'

export type TiaGraphSelection
  = | { kind: 'step', node: LineUpImportNode, actions: LineUpImportAction[], networks: LineUpGraphLogicNetwork[], detailUnavailable: boolean }
    | { kind: 'transition', node: LineUpImportNode, actions: [], networks: LineUpGraphLogicNetwork[], detailUnavailable: boolean }

type TiaGraphSelectableSequence = LineUpImportSequence & {
  viewerSchemaVersion?: number
  viewerWarnings?: string[]
}

function compareText(left: string, right: string) {
  return left < right ? -1 : left > right ? 1 : 0
}

function sortedNetworks(networks: LineUpGraphLogicNetwork[]) {
  return [...networks].sort((left, right) =>
    compareText(left.role, right.role)
    || left.ordinal - right.ordinal
    || compareText(left.sourceKey, right.sourceKey),
  )
}

export function selectTiaGraphElement(
  sequence: TiaGraphSelectableSequence,
  sourceKey: string,
): TiaGraphSelection | null {
  const node = sequence.nodes.find(item => item.sourceKey === sourceKey)
  if (!node) {
    return null
  }
  const networks = sortedNetworks((node.logicNetworks ?? []).filter(item => item.ownerSourceKey === node.sourceKey))
  const detailUnavailable = (sequence.viewerSchemaVersion !== undefined && sequence.viewerSchemaVersion < 2)
    || sequence.viewerWarnings?.includes('GraphViewerDetailUnavailable') === true
  if (node.nodeKind.toLowerCase() === 'step') {
    const actions = sequence.actions
      .filter(item => item.stepSourceKey === node.sourceKey)
      .sort((left, right) => left.ordinal - right.ordinal || compareText(left.sourceKey, right.sourceKey))
    return { kind: 'step', node, actions, networks, detailUnavailable }
  }
  if (node.nodeKind.toLowerCase() === 'transition') {
    return { kind: 'transition', node, actions: [], networks, detailUnavailable }
  }
  return null
}

/**
 * 工步选中时聚合相邻转换的 TransitionCondition 网络（出边+入边都算），
 * 供静态 BOM 定位提取"当前工步转换条件中的变量"；转换选中时原样返回。
 */
export function withAdjacentTransitionNetworks(
  sequence: TiaGraphSelectableSequence,
  selection: TiaGraphSelection,
): TiaGraphSelection {
  if (selection.kind !== 'step') {
    return selection
  }
  const stepKey = selection.node.sourceKey
  const adjacent = new Set<string>()
  for (const edge of sequence.edges) {
    const fromIsStep = edge.fromNodeKind.toLowerCase() === 'step'
    const toIsStep = edge.toNodeKind.toLowerCase() === 'step'
    const fromIsTransition = edge.fromNodeKind.toLowerCase() === 'transition'
    const toIsTransition = edge.toNodeKind.toLowerCase() === 'transition'
    if (fromIsStep && toIsTransition && edge.fromSourceKey === stepKey) {
      adjacent.add(edge.toSourceKey)
    }
    if (toIsStep && fromIsTransition && edge.toSourceKey === stepKey) {
      adjacent.add(edge.fromSourceKey)
    }
  }
  const networks = [...selection.networks]
  for (const node of sequence.nodes) {
    if (adjacent.has(node.sourceKey) && node.nodeKind.toLowerCase() === 'transition') {
      networks.push(...sortedNetworks((node.logicNetworks ?? []).filter(item =>
        item.ownerSourceKey === node.sourceKey && item.role === 'TransitionCondition')))
    }
  }
  return { ...selection, networks }
}

/**
 * 提取选中节点 networks+actions 的全部符号名（含单段裸名 + 完整路径 + 末段），
 * 供 lup_bomdirectory."TagCode"（裸信号名，如 OP020_CYL01_1_H）匹配用。
 */
export function symbolNamesForSelection(selection: TiaGraphSelection): string[] {
  const names = new Set<string>()
  for (const network of selection.networks) {
    for (const part of network.parts) {
      if (part.symbolPath.length) {
        names.add(part.symbolPath.join('.'))
        names.add(part.symbolPath[part.symbolPath.length - 1])
      }
    }
  }
  for (const action of selection.actions) {
    for (const token of action.tokens) {
      names.add(token)
    }
  }
  return [...names]
}

function liveIdentityMatches(definition: LineUpMonitorDefinition, snapshot: LineUpMonitorSnapshot) {
  return definition.bindingId === snapshot.bindingId
    && definition.publishedRevisionId === snapshot.publishedRevisionId
    && definition.stationId === snapshot.stationId
    && definition.sequenceSourceKey === snapshot.sequenceSourceKey
    && definition.definitionRevision === snapshot.definitionRevision
}

export function conditionsForSelection(
  definition: LineUpMonitorDefinition | null,
  snapshot: LineUpMonitorSnapshot | null,
  selection: TiaGraphSelection | null,
): LineUpConditionRuntimeValue[] {
  if (!definition || !snapshot || !selection || !liveIdentityMatches(definition, snapshot)) {
    return []
  }
  if (!definition.viewer.nodes.some(item => item.sourceKey === selection.node.sourceKey)) {
    return []
  }
  const partSourceKeysByNetwork = new Map(selection.networks.map(network => [
    network.sourceKey,
    new Set(network.parts.map(part => part.sourceKey)),
  ]))
  return snapshot.conditions.filter(condition => condition.ownerSourceKey === selection.node.sourceKey
    && partSourceKeysByNetwork.get(condition.networkSourceKey)?.has(condition.partSourceKey) === true)
}
