import type { TiaGraphSelection } from '../components/tiaGraphSelection'
import type { LineUpImageWorkspace } from '@/api/modules/LineUp/imageMarks'
import type { LineUpConditionRuntimeValue, LineUpMonitorDefinition, LineUpMonitorDelta, LineUpMonitorEvent, LineUpMonitorSnapshot } from '@/api/modules/LineUp/monitor'
import type { LineUpGraphLogicNetwork, LineUpImportAction, LineUpImportNode } from '@/api/modules/LineUp/types'
import { describe, expect, it } from 'vitest'
import * as monitorProjection from './monitorProjection'
import {
  applyMonitorDelta,
  conditionsForMonitorSelection,
  eventsForMonitorSelection,
  exceptionsForMonitorSelection,
  formatMonitorDuration,
  matchingMonitorSnapshot,
  monitorAvailabilityText,
  projectMonitorSnapshot,
  runtimeBindingActions,
  safeDurationNumber,
} from './monitorProjection'

function snapshot(): LineUpMonitorSnapshot {
  return {
    bindingId: '9001',
    publishedRevisionId: '7001',
    stationId: '501',
    sequenceSourceKey: 'sequence:a',
    definitionRevision: 'definition-1',
    snapshotRevision: '10',
    evaluatedAtUtc: '2026-08-21T10:00:00Z',
    connection: { status: 'Online', evaluatedAtUtc: '2026-08-21T10:00:00Z', lastGoodSampleAtUtc: '2026-08-21T10:00:00Z', errorCode: '' },
    steps: [
      { sourceKey: 's10', displayName: '上料', status: 'Active', lastValidValue: true, quality: 'Good', activeDurationMs: '1200', stateChangedAtUtc: '2026-08-21T09:59:59Z', lastGoodAtUtc: '2026-08-21T10:00:00Z', qualityCode: '' },
      { sourceKey: 's20', displayName: '夹紧', status: 'Active', lastValidValue: true, quality: 'Good', activeDurationMs: '800', stateChangedAtUtc: '2026-08-21T09:59:59Z', lastGoodAtUtc: '2026-08-21T10:00:00Z', qualityCode: '' },
    ],
    transitions: [{ sourceKey: 't1', displayName: '定位完成', status: 'Unsatisfied', condition: 'False', quality: 'Good', stateChangedAtUtc: '2026-08-21T10:00:00Z', qualityCode: '' }],
    conditions: [],
    edges: [{ sourceKey: 'e1', wasTraversed: false, traversedAtUtc: null }],
    exceptions: [],
    activeStepSourceKeys: ['s10', 's20'],
  }
}

function logicNetwork(sourceKey: string, ownerSourceKey: string, partSourceKey: string): LineUpGraphLogicNetwork {
  return {
    sourceKey,
    ownerSourceKey,
    role: 'TransitionCondition',
    ordinal: 1,
    programmingLanguage: 'LAD',
    title: '',
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

function graphNode(
  sourceKey: string,
  nodeKind: 'Step' | 'Transition',
  logicNetworks: LineUpGraphLogicNetwork[] = [],
): LineUpImportNode {
  return {
    sourceKey,
    sourceName: sourceKey,
    displayName: sourceKey,
    nodeKind,
    number: Number(sourceKey.split(':')[1]),
    isInitial: false,
    isFinal: false,
    actionGroupTitle: '',
    logicNetworks,
  }
}

function definition(viewerSchemaVersion = 2): LineUpMonitorDefinition {
  const transitionNetwork = logicNetwork('network:transition:2', 'transition:2', 'part:transition:2')
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
      viewerSchemaVersion,
      viewerCapabilities: [],
      viewerWarnings: [],
      sourceKey: 'sequence:a',
      sourceName: 'Sequence A',
      displayName: 'Sequence A',
      nodes: [
        graphNode('step:2', 'Step'),
        graphNode('step:20', 'Step'),
        graphNode('transition:2', 'Transition', [transitionNetwork]),
      ],
      actions: [],
      edges: [],
    },
  }
}

function selection(sourceKey: 'step:2' | 'step:20' | 'transition:2'): TiaGraphSelection {
  const selectedNode = definition().viewer.nodes.find(item => item.sourceKey === sourceKey)!
  return sourceKey.startsWith('transition:')
    ? { kind: 'transition', node: selectedNode, actions: [], networks: selectedNode.logicNetworks, detailUnavailable: false }
    : { kind: 'step', node: selectedNode, actions: [], networks: selectedNode.logicNetworks, detailUnavailable: false }
}

describe('lineUp monitor projection', () => {
  it('extracts exact PLC references from the selected step and prioritizes bad runtime operands', () => {
    const network = logicNetwork('network:step:4:supervision', 'step:4', 'part:bad')
    network.parts[0].symbolPath = ['DB100', 'Sensor[2]', 'Ready']
    const action: LineUpImportAction = {
      sourceKey: 'step:4/action:1',
      stepSourceKey: 'step:4',
      sourceName: '',
      displayName: '',
      ordinal: 1,
      stepNumber: 4,
      qualifier: 'N',
      event: null,
      interlock: null,
      tokens: [
        '"DB240_OP20Drive".V90[3].Cmd.Auto.StartTask',
        ':=',
        '"DB280_Parameter".SRV_V90[3].WorkPositionSET[1]',
        'WSTRING#\'运行\'',
        '\n',
      ],
      actionText: '',
    }
    const selected: TiaGraphSelection = {
      kind: 'step',
      node: graphNode('step:4', 'Step', [network]),
      actions: [action],
      networks: [network],
      detailUnavailable: false,
    }
    const conditions: LineUpConditionRuntimeValue[] = [{
      canonicalKey: 'bad',
      ownerSourceKey: 'step:4',
      networkSourceKey: network.sourceKey,
      partSourceKey: 'part:bad',
      state: 'Unknown',
      quality: 'Bad',
      value: null,
      sourceTimestamp: null,
      qualityCode: 'ReadFailed',
    }]
    const project = (monitorProjection as typeof monitorProjection & {
      plcReferencesForMonitorSelection?: (selection: TiaGraphSelection, conditions: LineUpConditionRuntimeValue[]) => unknown
    }).plcReferencesForMonitorSelection

    const result = typeof project === 'function' ? project(selected, conditions) : []

    expect(result).toEqual([
      { code: 'DB100.Sensor[2].Ready', abnormal: true },
      { code: 'DB240_OP20Drive.V90[3].Cmd.Auto.StartTask', abnormal: false },
      { code: 'DB280_Parameter.SRV_V90[3].WorkPositionSET[1]', abnormal: false },
    ])
  })

  it('matches the selected step references exactly to current-substation BOM images and markers', () => {
    const workspace: LineUpImageWorkspace = {
      subStation: {
        id: '50',
        code: 'OP020-01',
        name: 'OP020 主工位',
        workStationId: '30',
        workStationCode: 'OP020',
        workStationName: 'OP020 缓存上料工站',
        processId: '20',
        processCode: 'PROC-FEED',
        processName: '缓存上料工序',
        productionLineId: '10',
        productionLineCode: 'LINE-LOCAL',
        productionLineName: '本机演示产线',
      },
      boms: [
        { id: '1', stationId: '50', refNo: 'A', name: '异常传感器', tagCode: 'db100.sensor[2].ready', dbAddress: null },
        { id: '2', stationId: '50', refNo: 'B', name: '滑台轴', tagCode: null, dbAddress: '"DB240_OP20Drive".V90[3].Cmd.Auto.StartTask' },
        { id: '3', stationId: '50', refNo: 'C', name: '相似但不同', tagCode: 'DB100.Sensor[2].ReadyExtra', dbAddress: null },
      ],
      images: [
        { id: '100', subStationId: '50', pictureName: 'sensor.png', contentUrl: '/100', currentContentUrl: null, contentSha256: null, byteLength: 1, pixelWidth: 100, pixelHeight: 100, detectedMimeType: 'image/png', rowVersion: 1 },
        { id: '200', subStationId: '50', pictureName: 'axis.png', contentUrl: '/200', currentContentUrl: null, contentSha256: null, byteLength: 1, pixelWidth: 100, pixelHeight: 100, detectedMimeType: 'image/png', rowVersion: 1 },
        { id: '300', subStationId: '50', pictureName: 'other.png', contentUrl: '/300', currentContentUrl: null, contentSha256: null, byteLength: 1, pixelWidth: 100, pixelHeight: 100, detectedMimeType: 'image/png', rowVersion: 1 },
      ],
      markers: [
        { id: '11', imageId: '100', markType: 'Point', isSingleMark: true, point: null, billboard: null, line: null, bomIds: ['1'], issues: [] },
        { id: '12', imageId: '200', markType: 'Point', isSingleMark: true, point: null, billboard: null, line: null, bomIds: ['2'], issues: [] },
        { id: '13', imageId: '300', markType: 'Point', isSingleMark: true, point: null, billboard: null, line: null, bomIds: ['3'], issues: [] },
      ],
    }
    const project = (monitorProjection as typeof monitorProjection & {
      projectBomForMonitorSelection?: (workspace: LineUpImageWorkspace, references: Array<{ code: string, abnormal: boolean }>) => unknown
    }).projectBomForMonitorSelection

    const result = typeof project === 'function'
      ? project(workspace, [
          { code: 'DB100.Sensor[2].Ready', abnormal: true },
          { code: 'DB240_OP20Drive.V90[3].Cmd.Auto.StartTask', abnormal: false },
        ])
      : null

    expect(result).toEqual({
      boms: [workspace.boms[0], workspace.boms[1]],
      images: [workspace.images[0], workspace.images[1]],
      markers: [workspace.markers[0], workspace.markers[1]],
      abnormalBomIds: ['1'],
    })
  })

  it('describes why automatic BOM positioning has no visible result', () => {
    const describe = (monitorProjection as typeof monitorProjection & {
      describeMonitorBomLocation?: (input: {
        loaded: boolean
        stationBomCount: number
        referenceCount: number
        matchedBomCount: number
        markerCount: number
      }) => string
    }).describeMonitorBomLocation
    const value = (input: Parameters<NonNullable<typeof describe>>[0]) => typeof describe === 'function' ? describe(input) : ''

    expect(value({ loaded: true, stationBomCount: 0, referenceCount: 2, matchedBomCount: 0, markerCount: 0 }))
      .toBe('当前工位尚未维护 BOM 图片')
    expect(value({ loaded: true, stationBomCount: 8, referenceCount: 0, matchedBomCount: 0, markerCount: 0 }))
      .toBe('当前步骤没有可用于 BOM 定位的 PLC 变量')
    expect(value({ loaded: true, stationBomCount: 8, referenceCount: 2, matchedBomCount: 0, markerCount: 0 }))
      .toBe('当前步骤引用了 2 个 PLC 变量，但尚未关联 BOM 点位')
    expect(value({ loaded: true, stationBomCount: 8, referenceCount: 2, matchedBomCount: 1, markerCount: 0 }))
      .toBe('已匹配当前步骤的 BOM，但尚未维护图片点位')
  })

  it('does not claim GRAPH was imported before a line and device have data', () => {
    expect(monitorAvailabilityText({ hasSelection: false, hasImport: false })).toBe('请选择产线与 PLC 设备')
    expect(monitorAvailabilityText({ hasSelection: true, hasImport: false })).toBe('当前 PLC 设备尚无 GRAPH 结构')
  })

  it('projects parallel active steps, quality and clear TIA-style tones', () => {
    const projection = projectMonitorSnapshot(snapshot())

    expect(projection.nodes.s10.tone).toBe('active')
    expect(projection.nodes.s20.tone).toBe('active')
    expect(projection.nodes.t1.tone).toBe('idle')
    expect(projection.connectionTone).toBe('online')
  })

  it('parses wire durations with BigInt and never converts unsafe integers to Number', () => {
    expect(safeDurationNumber('1500')).toBe(1500)
    expect(formatMonitorDuration('1500')).toBe('1.5 s')
    expect(safeDurationNumber('9007199254740993')).toBeNull()
    expect(formatMonitorDuration('9007199254740993')).toBe('9007199254740993 ms')
    expect(safeDurationNumber('-1')).toBeNull()
    expect(formatMonitorDuration('not-a-duration')).toBe('—')
  })

  it('retains the original decimal duration when it is unsafe to convert to Number', () => {
    const value = snapshot()
    value.steps[0].activeDurationMs = '9007199254740993'

    const projection = projectMonitorSnapshot(value)

    expect(projection.nodes.s10.activeDurationMs).toBeNull()
    expect(projection.nodes.s10.activeDurationText).toBe('9007199254740993')
    expect(formatMonitorDuration(projection.nodes.s10.activeDurationText)).toBe('9007199254740993 ms')
  })

  it('uses explicit warning, error and unknown tones instead of treating bad quality as false', () => {
    const value = snapshot()
    value.steps[0] = { ...value.steps[0], status: 'Warning', quality: 'Good' }
    value.steps[1] = { ...value.steps[1], status: 'Unknown', quality: 'Unknown' }

    const projection = projectMonitorSnapshot(value)

    expect(projection.nodes.s10.tone).toBe('warning')
    expect(projection.nodes.s20.tone).toBe('unknown')
  })

  it('applies only contiguous increasing deltas and reports a sequence gap', () => {
    const value = snapshot()
    const contiguous: LineUpMonitorDelta = {
      bindingId: '9001',
      publishedRevisionId: '7001',
      stationId: '501',
      sequenceSourceKey: 'sequence:a',
      definitionRevision: 'definition-1',
      fromSnapshotRevision: '10',
      toSnapshotRevision: '11',
      serverTimestamp: '2026-08-21T10:00:01Z',
      connection: value.connection,
      changedSteps: [{ ...value.steps[0], status: 'Completed', lastValidValue: false }],
      changedTransitions: [],
      changedConditions: [],
      changedEdges: [{ sourceKey: 'e1', wasTraversed: true, traversedAtUtc: '2026-08-21T10:00:01Z' }],
      exceptions: [],
    }
    const applied = applyMonitorDelta(value, contiguous)
    expect(applied.kind).toBe('applied')
    expect(applied.snapshot?.snapshotRevision).toBe('11')
    expect(applied.snapshot?.steps.find(item => item.sourceKey === 's10')?.status).toBe('Completed')

    expect(applyMonitorDelta(applied.snapshot!, { ...contiguous, fromSnapshotRevision: '12', toSnapshotRevision: '13' }).kind).toBe('gap')
    expect(applyMonitorDelta(applied.snapshot!, { ...contiguous, fromSnapshotRevision: '9', toSnapshotRevision: '10' }).kind).toBe('ignored')
    expect(applyMonitorDelta(applied.snapshot!, { ...contiguous, definitionRevision: 'definition-2', fromSnapshotRevision: '11' }).kind).toBe('definition-mismatch')
  })

  it('requires prepare, online validation and explicit enable as separate actions', () => {
    expect(runtimeBindingActions({ bindingStatus: 'Unresolved', runtimeBindingReady: false, monitoringEnabled: false }))
      .toEqual({ canPrepare: true, canValidate: false, canEnable: false, canDisable: false })
    expect(runtimeBindingActions({ bindingStatus: 'NeedsReview', runtimeBindingReady: true, monitoringEnabled: false }))
      .toEqual({ canPrepare: true, canValidate: true, canEnable: false, canDisable: false })
    expect(runtimeBindingActions({ bindingStatus: 'NeedsReview', runtimeBindingReady: false, monitoringEnabled: false }))
      .toEqual({ canPrepare: true, canValidate: true, canEnable: false, canDisable: false })
    expect(runtimeBindingActions({ bindingStatus: 'Resolved', runtimeBindingReady: true, monitoringEnabled: false }))
      .toEqual({ canPrepare: true, canValidate: false, canEnable: true, canDisable: false })
    expect(runtimeBindingActions({ bindingStatus: 'Resolved', runtimeBindingReady: true, monitoringEnabled: true }))
      .toEqual({ canPrepare: false, canValidate: false, canEnable: false, canDisable: true })
  })

  it('accepts a snapshot only when the current binding, revision, station, sequence, and definition identities match', () => {
    const value = snapshot()
    const expected = { bindingId: '9001', publishedRevisionId: '7001', stationId: '501', sequenceSourceKey: 'sequence:a' }

    expect(matchingMonitorSnapshot(definition(), value, expected)).toBe(value)
    expect(matchingMonitorSnapshot(definition(), { ...value, bindingId: '9002' }, expected)).toBeNull()
    expect(matchingMonitorSnapshot(definition(), { ...value, publishedRevisionId: '7002' }, expected)).toBeNull()
    expect(matchingMonitorSnapshot(definition(), { ...value, stationId: '502' }, expected)).toBeNull()
    expect(matchingMonitorSnapshot(definition(), { ...value, sequenceSourceKey: 'sequence:b' }, expected)).toBeNull()
    expect(matchingMonitorSnapshot({ ...definition(), definitionRevision: 'definition-2' }, value, expected)).toBeNull()
    expect(matchingMonitorSnapshot(definition(), value, { ...expected, stationId: '' })).toBeNull()
  })

  it('keeps step activity separate from LAD conditions even when the owner matches', () => {
    const value = {
      ...snapshot(),
      conditions: [
        { canonicalKey: 'step-2', ownerSourceKey: 'step:2', networkSourceKey: '', partSourceKey: '', state: 'True' as const, quality: 'Good' as const, value: true, sourceTimestamp: null, qualityCode: '' },
        { canonicalKey: 'step-20', ownerSourceKey: 'step:20', networkSourceKey: '', partSourceKey: '', state: 'False' as const, quality: 'Good' as const, value: false, sourceTimestamp: null, qualityCode: '' },
      ],
    }

    expect(conditionsForMonitorSelection(definition(), value, selection('step:2'))).toEqual([])
  })

  it('keeps only the selected Transition owner, network, and part condition values', () => {
    const value = {
      ...snapshot(),
      conditions: [
        { canonicalKey: 'transition-network', ownerSourceKey: 'transition:2', networkSourceKey: 'network:transition:2', partSourceKey: '', state: 'True' as const, quality: 'Good' as const, value: true, sourceTimestamp: null, qualityCode: '' },
        { canonicalKey: 'transition-part', ownerSourceKey: 'transition:2', networkSourceKey: '', partSourceKey: 'part:transition:2', state: 'True' as const, quality: 'Good' as const, value: true, sourceTimestamp: null, qualityCode: '' },
        { canonicalKey: 'transition-both-exact', ownerSourceKey: 'transition:2', networkSourceKey: 'network:transition:2', partSourceKey: 'part:transition:2', state: 'True' as const, quality: 'Good' as const, value: true, sourceTimestamp: null, qualityCode: '' },
        { canonicalKey: 'foreign-network', ownerSourceKey: 'transition:2', networkSourceKey: 'network:transition:20', partSourceKey: 'part:transition:2', state: 'False' as const, quality: 'Good' as const, value: false, sourceTimestamp: null, qualityCode: '' },
        { canonicalKey: 'foreign-part', ownerSourceKey: 'transition:2', networkSourceKey: 'network:transition:2', partSourceKey: 'part:transition:20', state: 'False' as const, quality: 'Good' as const, value: false, sourceTimestamp: null, qualityCode: '' },
        { canonicalKey: 'exact-network-foreign-part', ownerSourceKey: 'transition:2', networkSourceKey: 'network:transition:2', partSourceKey: 'part:transition:20', state: 'False' as const, quality: 'Good' as const, value: false, sourceTimestamp: null, qualityCode: '' },
        { canonicalKey: 'foreign-network-exact-part', ownerSourceKey: 'transition:2', networkSourceKey: 'network:transition:20', partSourceKey: 'part:transition:2', state: 'False' as const, quality: 'Good' as const, value: false, sourceTimestamp: null, qualityCode: '' },
        { canonicalKey: 'conflicting-owner', ownerSourceKey: 'transition:20', networkSourceKey: 'network:transition:2', partSourceKey: 'part:transition:2', state: 'False' as const, quality: 'Good' as const, value: false, sourceTimestamp: null, qualityCode: '' },
        { canonicalKey: 'other-network', ownerSourceKey: 'transition:20', networkSourceKey: 'network:transition:20', partSourceKey: 'part:transition:20', state: 'False' as const, quality: 'Good' as const, value: false, sourceTimestamp: null, qualityCode: '' },
      ],
    }

    expect(conditionsForMonitorSelection(definition(), value, selection('transition:2')).map(item => item.canonicalKey))
      .toEqual(['transition-both-exact'])
  })

  it('returns no node conditions for v1 definitions, missing owner keys, or mismatched live identities', () => {
    const owned = {
      ...snapshot(),
      conditions: [{ canonicalKey: 'owned', ownerSourceKey: 'step:2', networkSourceKey: '', partSourceKey: '', state: 'True' as const, quality: 'Good' as const, value: true, sourceTimestamp: null, qualityCode: '' }],
    }
    const unowned = {
      ...snapshot(),
      conditions: [{ canonicalKey: 'legacy', ownerSourceKey: '', networkSourceKey: '', partSourceKey: '', state: 'True' as const, quality: 'Good' as const, value: true, sourceTimestamp: null, qualityCode: '' }],
    }

    expect(conditionsForMonitorSelection(definition(1), owned, selection('step:2'))).toEqual([])
    expect(conditionsForMonitorSelection(definition(), unowned, selection('step:2'))).toEqual([])
    expect(conditionsForMonitorSelection(definition(), { ...owned, definitionRevision: 'definition-2' }, selection('step:2'))).toEqual([])
  })

  it('keeps only exact selected owner events and excludes ownerless or conflicting node events', () => {
    const event = (id: string, ownerSourceKey: string): LineUpMonitorEvent => ({
      id,
      bindingId: '9001',
      sequenceSourceKey: 'sequence:a',
      ownerSourceKey,
      eventType: 'StateChanged',
      previousState: '',
      currentState: '',
      occurredAtUtc: '2026-08-25T00:00:00Z',
      durationMs: null,
      quality: 'Good',
      qualityCode: '',
      definitionRevision: 'definition-1',
      gatewayStreamId: '',
      gatewaySequence: '1',
      eventOrdinal: 1,
      detailsJson: '',
    })
    const events = [event('global', ''), event('step-2', 'step:2'), event('step-20', 'step:20')]

    expect(eventsForMonitorSelection(events, selection('step:2')).map(item => item.id)).toEqual(['step-2'])
    expect(eventsForMonitorSelection(events, selection('step:20')).map(item => item.id)).toEqual(['step-20'])
    expect(eventsForMonitorSelection(events, null)).toEqual([])
  })

  it('keeps only exceptions owned by the exact selected node', () => {
    const exceptions = [
      { code: 'GLOBAL', ownerSourceKey: '', severity: 'Error', safeMessage: 'global', occurredAtUtc: '2026-08-25T00:00:00Z' },
      { code: 'STEP2', ownerSourceKey: 'step:2', severity: 'Error', safeMessage: 'step 2', occurredAtUtc: '2026-08-25T00:00:00Z' },
      { code: 'STEP20', ownerSourceKey: 'step:20', severity: 'Error', safeMessage: 'step 20', occurredAtUtc: '2026-08-25T00:00:00Z' },
    ]

    expect(exceptionsForMonitorSelection(exceptions, selection('step:2')).map(item => item.code)).toEqual(['STEP2'])
    expect(exceptionsForMonitorSelection(exceptions, selection('step:20')).map(item => item.code)).toEqual(['STEP20'])
    expect(exceptionsForMonitorSelection(exceptions, null)).toEqual([])
  })
})
