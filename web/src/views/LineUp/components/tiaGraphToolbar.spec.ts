import type { LineUpGraphRuntimeProjection } from '../monitor/monitorProjection'
import type { LineUpGraphPreviewSequenceInput } from './tiaGraphLayout'
import { describe, expect, it } from 'vitest'
import { nextTiaGraphZoom, resolveTiaGraphToolbarState, resolveTiaGraphWheelPan } from './tiaGraphToolbar'

const sequence: LineUpGraphPreviewSequenceInput = {
  sourceKey: 'sequence:a',
  sourceName: 'Sequence A',
  displayName: 'Sequence A',
  nodes: [
    { nodeKind: 'Step', sourceKey: 'step:10', sourceName: 'S10', displayName: 'S10', number: 10, isInitial: true, isFinal: false, actionGroupTitle: '', logicNetworks: [] },
    { nodeKind: 'Step', sourceKey: 'step:20', sourceName: 'S20', displayName: 'S20', number: 20, isInitial: false, isFinal: false, actionGroupTitle: '', logicNetworks: [] },
  ],
  actions: [],
  edges: [],
}

function runtime(): LineUpGraphRuntimeProjection {
  return {
    nodes: {
      'step:10': { sourceKey: 'step:10', tone: 'idle', label: 'Idle', quality: 'Good', activeDurationMs: 0, activeDurationText: '0' },
      'step:20': { sourceKey: 'step:20', tone: 'active', label: 'Active', quality: 'Good', activeDurationMs: 1000, activeDurationText: '1000' },
      'step:foreign': { sourceKey: 'step:foreign', tone: 'active', label: 'Active', quality: 'Good', activeDurationMs: 1000, activeDurationText: '1000' },
    },
    edges: {},
    connectionTone: 'online',
  }
}

describe('tIA GRAPH readonly toolbar state', () => {
  it('enables locate only for an active Step owned by the visible Sequence', () => {
    expect(resolveTiaGraphToolbarState(sequence, runtime())).toEqual({
      activeStepSourceKey: 'step:20',
      canLocateActiveStep: true,
      onlineLabel: '在线',
      onlineTone: 'online',
    })
    expect(resolveTiaGraphToolbarState(sequence, {
      ...runtime(),
      nodes: { 'step:foreign': runtime().nodes['step:foreign'] },
    }).canLocateActiveStep).toBe(false)
  })

  it('does not invent a live current step for static or offline data', () => {
    expect(resolveTiaGraphToolbarState(sequence, null)).toMatchObject({
      activeStepSourceKey: '',
      canLocateActiveStep: false,
      onlineLabel: '静态预览',
      onlineTone: 'static',
    })
    expect(resolveTiaGraphToolbarState(sequence, { ...runtime(), connectionTone: 'offline' })).toMatchObject({
      activeStepSourceKey: '',
      canLocateActiveStep: false,
      onlineLabel: '离线',
      onlineTone: 'offline',
    })
  })

  it('steps absolute zoom within the X6 readonly limits', () => {
    expect(nextTiaGraphZoom(1, 'in')).toBe(1.15)
    expect(nextTiaGraphZoom(1, 'out')).toBe(0.85)
    expect(nextTiaGraphZoom(1.95, 'in')).toBe(2)
    expect(nextTiaGraphZoom(0.25, 'out')).toBe(0.2)
  })

  it('reserves modified wheel input for zoom and converts plain wheel input to bounded vertical pan', () => {
    expect(resolveTiaGraphWheelPan({ deltaY: 80, deltaMode: 0, ctrlKey: true, metaKey: false }, 600)).toBeNull()
    expect(resolveTiaGraphWheelPan({ deltaY: -80, deltaMode: 0, ctrlKey: false, metaKey: true }, 600)).toBeNull()
    expect(resolveTiaGraphWheelPan({ deltaY: 64, deltaMode: 0, ctrlKey: false, metaKey: false }, 600)).toBe(64)
    expect(resolveTiaGraphWheelPan({ deltaY: 2, deltaMode: 1, ctrlKey: false, metaKey: false }, 600)).toBe(72)
    expect(resolveTiaGraphWheelPan({ deltaY: 1, deltaMode: 2, ctrlKey: false, metaKey: false }, 600)).toBe(160)
    expect(resolveTiaGraphWheelPan({ deltaY: 0, deltaMode: 0, ctrlKey: false, metaKey: false }, 600)).toBe(0)
  })
})
