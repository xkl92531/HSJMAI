import type { LineUpMonitorDefinition, LineUpMonitorSnapshot } from '@/api/modules/LineUp/monitor'
import type { LineUpGraphLogicNetwork, LineUpImportNode, LineUpImportSequence } from '@/api/modules/LineUp/types'
import { describe, expect, it } from 'vitest'
import { conditionsForSelection, selectTiaGraphElement } from './tiaGraphSelection'

function network(
  sourceKey: string,
  ownerSourceKey: string,
  role: string,
  ordinal: number,
  partSourceKey: string,
): LineUpGraphLogicNetwork {
  return {
    sourceKey,
    ownerSourceKey,
    role,
    ordinal,
    programmingLanguage: 'LAD',
    title: role,
    parts: [{
      sourceKey: partSourceKey,
      uId: 1,
      partName: 'Contact',
      scope: 'GlobalVariable',
      symbolPath: ['DB1', 'Ready'],
      constantValue: '',
      negated: false,
      attributes: [],
      templateValues: [],
      pinNames: ['in', 'out'],
    }],
    wires: [],
  }
}

function node(
  sourceKey: string,
  nodeKind: 'Step' | 'Transition',
  logicNetworks: LineUpGraphLogicNetwork[],
): LineUpImportNode {
  return {
    nodeKind,
    sourceKey,
    sourceName: sourceKey,
    displayName: sourceKey === 'step:20' ? '相同显示名' : sourceKey,
    number: Number(sourceKey.split(':')[1]),
    isInitial: false,
    isFinal: false,
    actionGroupTitle: '',
    logicNetworks,
  }
}

const step20Networks = [
  network('network:step:20:supervision', 'step:20', 'StepSupervision', 2, 'part:step:20:supervision'),
  network('network:step:2:leaked', 'step:2', 'StepCondition', 1, 'part:step:2:leaked'),
  network('network:step:20:interlock', 'step:20', 'StepInterlock', 1, 'part:step:20:interlock'),
]

const sequence: LineUpImportSequence = {
  sourceKey: 'sequence:a',
  sourceName: 'Sequence A',
  displayName: 'Sequence A',
  nodes: [
    node('step:2', 'Step', [network('network:step:2', 'step:2', 'StepInterlock', 1, 'part:step:2')]),
    node('step:20', 'Step', step20Networks),
    node('transition:2', 'Transition', [network('network:transition:2', 'transition:2', 'TransitionCondition', 1, 'part:transition:2')]),
  ],
  actions: [
    { sourceKey: 'action:step:20:2', stepSourceKey: 'step:20', sourceName: 'A2', displayName: 'A2', ordinal: 2, stepNumber: 20, qualifier: 'N', event: null, interlock: null, tokens: [], actionText: '' },
    { sourceKey: 'action:step:2:1', stepSourceKey: 'step:2', sourceName: 'A1', displayName: '相同显示名', ordinal: 1, stepNumber: 2, qualifier: 'N', event: null, interlock: null, tokens: [], actionText: '' },
    { sourceKey: 'action:step:20:1', stepSourceKey: 'step:20', sourceName: 'A1', displayName: '相同显示名', ordinal: 1, stepNumber: 20, qualifier: 'N', event: null, interlock: null, tokens: [], actionText: '' },
  ],
  edges: [],
}

function definition(): LineUpMonitorDefinition {
  return {
    bindingId: '9001',
    publishedRevisionId: '7001',
    stationId: '501',
    sequenceSourceKey: 'sequence:a',
    sequenceDisplayName: 'Sequence A',
    definitionRevision: 'definition-1',
    steps: [],
    transitions: [],
    edges: [],
    viewer: {
      viewerSchemaVersion: 2,
      viewerCapabilities: ['LogicNetworks'],
      viewerWarnings: [],
      ...sequence,
    },
  }
}

function snapshot(): LineUpMonitorSnapshot {
  return {
    bindingId: '9001',
    publishedRevisionId: '7001',
    stationId: '501',
    sequenceSourceKey: 'sequence:a',
    definitionRevision: 'definition-1',
    snapshotRevision: '1',
    evaluatedAtUtc: '2026-08-25T00:00:00Z',
    connection: { status: 'Online', evaluatedAtUtc: '2026-08-25T00:00:00Z', lastGoodSampleAtUtc: '2026-08-25T00:00:00Z', errorCode: '' },
    steps: [],
    transitions: [],
    conditions: [
      { canonicalKey: 'exact-20', ownerSourceKey: 'step:20', networkSourceKey: 'network:step:20:interlock', partSourceKey: 'part:step:20:interlock', state: 'True', quality: 'Good', value: true, sourceTimestamp: null, qualityCode: '' },
      { canonicalKey: 'owner-20', ownerSourceKey: 'step:20', networkSourceKey: '', partSourceKey: '', state: 'True', quality: 'Good', value: true, sourceTimestamp: null, qualityCode: '' },
      { canonicalKey: 'network-20', ownerSourceKey: '', networkSourceKey: 'network:step:20:interlock', partSourceKey: '', state: 'True', quality: 'Good', value: true, sourceTimestamp: null, qualityCode: '' },
      { canonicalKey: 'part-20', ownerSourceKey: '', networkSourceKey: '', partSourceKey: 'part:step:20:supervision', state: 'False', quality: 'Good', value: false, sourceTimestamp: null, qualityCode: '' },
      { canonicalKey: 'conflicting-owner', ownerSourceKey: 'step:2', networkSourceKey: 'network:step:20:interlock', partSourceKey: 'part:step:20:interlock', state: 'True', quality: 'Good', value: true, sourceTimestamp: null, qualityCode: '' },
      { canonicalKey: 'cross-pair', ownerSourceKey: 'step:20', networkSourceKey: 'network:step:20:interlock', partSourceKey: 'part:step:20:supervision', state: 'True', quality: 'Good', value: true, sourceTimestamp: null, qualityCode: '' },
      { canonicalKey: 'unowned', ownerSourceKey: '', networkSourceKey: '', partSourceKey: '', state: 'Unknown', quality: 'Unknown', value: null, sourceTimestamp: null, qualityCode: '' },
    ],
    edges: [],
    exceptions: [],
    activeStepSourceKeys: [],
  }
}

describe('tia graph exact selection', () => {
  it('selects a step by exact SourceKey and sorts only networks owned by that node', () => {
    const selected = selectTiaGraphElement(sequence, 'step:20')!

    expect(selected.kind).toBe('step')
    expect(selected.node.sourceKey).toBe('step:20')
    expect(selected.actions.map(item => item.ordinal)).toEqual([1, 2])
    expect(selected.networks.map(item => item.role)).toEqual(['StepInterlock', 'StepSupervision'])
  })

  it('does not use SourceKey prefixes or DisplayName to attach actions and transition networks', () => {
    const step = selectTiaGraphElement(sequence, 'step:2')!
    const transition = selectTiaGraphElement(sequence, 'transition:2')!

    expect(step.actions.map(item => item.sourceKey)).toEqual(['action:step:2:1'])
    expect(transition.kind).toBe('transition')
    expect(transition.actions).toEqual([])
    expect(transition.networks).toHaveLength(1)
  })

  it('carries authoritative legacy viewer detail availability into the exact selection', () => {
    const legacyBySchema = selectTiaGraphElement({
      ...sequence,
      viewerSchemaVersion: 1,
      viewerWarnings: [],
    }, 'step:2')!
    const legacyByWarning = selectTiaGraphElement({
      ...sequence,
      viewerSchemaVersion: 2,
      viewerWarnings: ['GraphViewerDetailUnavailable'],
    }, 'step:2')!
    const complete = selectTiaGraphElement({
      ...sequence,
      viewerSchemaVersion: 2,
      viewerWarnings: [],
    }, 'step:2')!

    expect(legacyBySchema.detailUnavailable).toBe(true)
    expect(legacyByWarning.detailUnavailable).toBe(true)
    expect(complete.detailUnavailable).toBe(false)
  })

  it('requires exact owner, network, and part SourceKeys and rejects partial fallback data', () => {
    const selected = selectTiaGraphElement(sequence, 'step:20')!

    expect(conditionsForSelection(definition(), snapshot(), selected).map(item => item.canonicalKey))
      .toEqual(['exact-20'])
  })

  it('discards live conditions when definition and snapshot identity do not match', () => {
    const selected = selectTiaGraphElement(sequence, 'step:20')!
    const mismatched = { ...snapshot(), publishedRevisionId: '7002' }

    expect(conditionsForSelection(definition(), mismatched, selected)).toEqual([])
    expect(conditionsForSelection(null, snapshot(), selected)).toEqual([])
  })
})
