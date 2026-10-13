import { describe, expect, it } from 'vitest'
import {
  graphSummaryPresentation,
  hasRunningPostProcess,
  postProcessStatusText,
  postProcessTagType,
  uploadPollAction
} from './projectUploadPresentation'

describe('PLC ProjectUpload GRAPH summary', () => {
  it('renders a compact safe summary for a completed upload', () => {
    const presentation = graphSummaryPresentation({
      detectedBlockCount: 2,
      draftCreatedCount: 1,
      reusedRevisionCount: 1,
      failedCount: 0,
      issueCount: 0,
    })

    expect(presentation.text).toContain('GRAPH 草稿 1')
    expect(presentation.text).toContain('复用 1')
    expect(presentation.type).toBe('success')
    expect(JSON.stringify(presentation)).not.toMatch(/path|previewJson|sourceXml/i)
  })

  it('uses warning presentation without changing the PLC upload status', () => {
    const presentation = graphSummaryPresentation({
      detectedBlockCount: 2,
      draftCreatedCount: 1,
      reusedRevisionCount: 0,
      failedCount: 1,
      issueCount: 2,
    })

    expect(presentation.type).toBe('warning')
    expect(presentation.text).toContain('失败 1')
    expect(presentation.text).toContain('问题 2')
  })
})

describe('PLC ProjectUpload post-process presentation', () => {
  it('keeps polling while a post-process entry is Running', () => {
    expect(hasRunningPostProcess([
      { key: 'tag-registration', title: '标签增量注册', status: 'Succeeded', detail: '变更块 2 个', finishTime: '2026-09-11T12:00:00' },
      { key: 'other', title: '其它', status: 'Running', detail: null, finishTime: null },
    ])).toBe(true)

    expect(hasRunningPostProcess([
      { key: 'tag-registration', title: '标签增量注册', status: 'Skipped', detail: '无变更', finishTime: '2026-09-11T12:00:00' },
    ])).toBe(false)

    expect(hasRunningPostProcess(null)).toBe(false)
    expect(hasRunningPostProcess(undefined)).toBe(false)
  })

  it('maps post-process statuses to display text and tag types', () => {
    expect(postProcessStatusText('Running')).toBe('进行中')
    expect(postProcessStatusText('Succeeded')).toBe('成功')
    expect(postProcessStatusText('Failed')).toBe('失败')
    expect(postProcessStatusText('Skipped')).toBe('跳过')
    // 未知状态原样透出，不吞掉后端新增的状态
    expect(postProcessStatusText('Cancelled')).toBe('Cancelled')
    expect(postProcessStatusText(null)).toBe('')

    expect(postProcessTagType('Running')).toBe('primary')
    expect(postProcessTagType('Succeeded')).toBe('success')
    expect(postProcessTagType('Failed')).toBe('danger')
    expect(postProcessTagType('Skipped')).toBe('info')
    expect(postProcessTagType('whatever')).toBe('info')
  })
})

describe('PLC ProjectUpload poll stop conditions', () => {
  it('keeps polling while parsing, saving or generating constraints (status 0/1/2)', () => {
    expect(uploadPollAction(0, null)).toBe('continue')
    expect(uploadPollAction(1, null)).toBe('continue')
    // 后端在 XMLV2 约束发布期间保持 Saving（2），发布成功后才 Completed
    expect(uploadPollAction(2, null)).toBe('continue')
  })

  it('stops on failure regardless of post-process entries', () => {
    expect(uploadPollAction(4, null)).toBe('failed')
    expect(uploadPollAction(4, [{ key: 'x', title: 'x', status: 'Running', detail: null, finishTime: null }])).toBe('failed')
  })

  it('waits for running post-processes after completion, otherwise completes', () => {
    expect(uploadPollAction(3, [{ key: 'x', title: 'x', status: 'Running', detail: null, finishTime: null }])).toBe('completed-wait')
    expect(uploadPollAction(3, [{ key: 'x', title: 'x', status: 'Succeeded', detail: null, finishTime: null }])).toBe('completed')
    expect(uploadPollAction(3, null)).toBe('completed')
  })

  it('treats unknown statuses as still running', () => {
    expect(uploadPollAction(undefined, null)).toBe('continue')
    expect(uploadPollAction(9, null)).toBe('continue')
  })
})
