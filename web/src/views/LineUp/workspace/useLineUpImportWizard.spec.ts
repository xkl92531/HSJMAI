import { afterEach, describe, expect, it, vi } from 'vitest'
import { createLineUpImportWizard } from './useLineUpImportWizard'

function preview(overrides: Record<string, unknown> = {}) {
  return {
    batchId: '9001',
    status: 'PreviewReady',
    previewSchemaVersion: 1,
    previewRevision: 3,
    previewHash: 'preview-hash',
    sourceFileName: 'FB260_CacheGraph.ml',
    sourceFileHash: 'source-hash',
    lineId: '10',
    deviceId: '20',
    documents: [{
      sourceFileName: 'FB260_CacheGraph.ml',
      sourceRelativePath: 'FB260_CacheGraph.ml',
      sourceSha256: 'source-hash',
      blockName: 'FB260_CacheGraph',
      blockNumber: 260,
      programmingLanguage: 'GRAPH',
      instanceDbHint: null,
      sequences: [{
        sourceKey: 'seq-a',
        sourceName: '上料滑台1',
        displayName: '上料滑台1',
        nodes: [],
        actions: [],
        edges: [],
      }],
      address: {
        structuralStatus: 0,
        canCommitStructure: true,
        canCommitForMonitoring: false,
        sequences: [{ sequenceSourceKey: 'seq-a', bindingStatus: 0 }],
        candidates: [],
        issues: [],
      },
      hierarchy: {
        canCommitStructure: true,
        sequences: [{
          sequenceSourceKey: 'seq-a',
          confidenceLevel: 0,
          processId: '300',
          stationId: '200',
          subStationId: null,
          selectedCandidateKey: 'candidate-a',
          candidates: [{
            candidateKey: 'candidate-a',
            candidateKind: 2,
            sequenceSourceKey: 'seq-a',
            processId: '300',
            stationId: '200',
            subStationId: null,
            processCode: 'P020',
            processName: '缓存上料工序',
            stationCode: 'OP020',
            stationName: '缓存上料工站',
            subStationCode: 'OP020-01',
            subStationName: '上料滑台1',
            proposedSubStationCode: 'OP020-01',
            proposedSubStationName: '上料滑台1',
            confidenceLevel: 0,
            evidenceCodes: ['ExactDeviceScopedStationCode'],
          }],
        }],
        issues: [],
      },
      issues: [],
    }],
    issues: [],
    summary: {
      documentCount: 1,
      sequenceCount: 1,
      stepCount: 77,
      transitionCount: 87,
      actionCount: 149,
      edgeCount: 182,
      blockingIssueCount: 0,
    },
    diffToken: 'diff-token',
    canCommitStructure: true,
    canCommitForMonitoring: false,
    ...overrides,
  }
}

describe('createLineUpImportWizard', () => {
  afterEach(() => {
    vi.useRealTimers()
  })

  it('allows structure commit but not monitoring when runtime binding is unresolved', () => {
    const wizard = createLineUpImportWizard()

    wizard.acceptPreview(preview() as never)

    expect(wizard.canCommitStructure.value).toBe(true)
    expect(wizard.canCommitForMonitoring.value).toBe(false)
    expect(wizard.runtimeStatusText.value).toBe('结构已解析，实时地址待绑定')
  })

  it('disables every commit when any nested issue is Blocking', () => {
    const blocked = preview()
    blocked.documents[0].hierarchy.issues.push({
      severity: 2,
      issueType: 'HierarchyBlocked',
      sourceKey: 'seq-a',
      message: 'Hierarchy blocked.',
    } as never)
    const wizard = createLineUpImportWizard()

    wizard.acceptPreview(blocked as never)

    expect(wizard.hasBlockingIssues.value).toBe(true)
    expect(wizard.canCommitStructure.value).toBe(false)
    expect(wizard.canCommitForMonitoring.value).toBe(false)
  })

  it('polls after upload until PreviewReady and then loads the preview', async () => {
    vi.useFakeTimers()
    const gateway = {
      startPreview: vi.fn().mockResolvedValue({ batchId: '9001', status: 'Uploaded' }),
      getProgress: vi.fn()
        .mockResolvedValueOnce({ batchId: '9001', status: 'Parsing', errorCode: null, previewRevision: 0, canLoadPreview: false })
        .mockResolvedValueOnce({ batchId: '9001', status: 'PreviewReady', errorCode: null, previewRevision: 3, canLoadPreview: true }),
      getPreview: vi.fn().mockResolvedValue(preview()),
      commit: vi.fn(),
    }
    const wizard = createLineUpImportWizard({ gateway })

    await wizard.startPreview(new File(['graph'], 'FB260.ml'), '10', '20')
    await vi.advanceTimersByTimeAsync(0)
    expect(gateway.getProgress).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(1000)

    expect(gateway.getProgress).toHaveBeenCalledTimes(2)
    expect(gateway.getPreview).toHaveBeenCalledWith('9001')
    expect(wizard.preview.value?.batchId).toBe('9001')
    expect(wizard.isPolling.value).toBe(false)
  })

  it('builds a narrow commit request from candidate and display-name decisions', () => {
    const wizard = createLineUpImportWizard()
    wizard.acceptPreview(preview() as never)
    wizard.selectHierarchyCandidate('seq-a', 'candidate-a')
    wizard.rename('seq-a', '缓存上料新名称')

    expect(wizard.buildCommitRequest()).toEqual({
      commitMode: 'StructureOnly',
      previewRevision: 3,
      previewHash: 'preview-hash',
      diffToken: 'diff-token',
      hierarchyDecisions: [{ sequenceSourceKey: 'seq-a', candidateKey: 'candidate-a' }],
      displayNameChanges: [{ sourceKey: 'seq-a', displayName: '缓存上料新名称' }],
    })
  })

  it('projects display-name changes into the visible graph without mutating SourceKey', () => {
    const wizard = createLineUpImportWizard()
    wizard.acceptPreview(preview() as never)

    wizard.rename('seq-a', '缓存上料新名称')

    expect(wizard.displaySequences.value[0]).toMatchObject({
      sourceKey: 'seq-a',
      sourceName: '上料滑台1',
      displayName: '缓存上料新名称',
    })
    expect(wizard.sequences.value[0]).toMatchObject({
      sourceKey: 'seq-a',
      displayName: '上料滑台1',
    })
  })

  it('preserves zoom translation and node selection independently per Sequence', () => {
    const wizard = createLineUpImportWizard()
    wizard.acceptPreview(preview() as never)
    wizard.updateSequenceView('seq-a', { zoom: 0.8, translateX: 14, translateY: 22, selectedNodeKey: 'step-a' })
    wizard.selectSequence('seq-b')
    wizard.updateSequenceView('seq-b', { zoom: 1.2, translateX: 2, translateY: 3, selectedNodeKey: 'step-b' })
    wizard.selectSequence('seq-a')

    expect(wizard.currentSequenceView.value).toEqual({
      zoom: 0.8,
      translateX: 14,
      translateY: 22,
      selectedNodeKey: 'step-a',
    })
  })

  it('does not leak rename or canvas state into a newly accepted import batch', () => {
    const wizard = createLineUpImportWizard()
    wizard.acceptPreview(preview() as never)
    wizard.rename('seq-a', '上一批显示名')
    wizard.updateSequenceView('seq-a', {
      zoom: 0.7,
      translateX: 10,
      translateY: 20,
      selectedNodeKey: 'step-a',
    })

    wizard.acceptPreview(preview({ batchId: '9002', previewHash: 'next-hash' }) as never)

    expect(wizard.displayNameChanges.value.size).toBe(0)
    expect(wizard.currentSequenceView.value).toBeNull()
  })
})
