import type { McpStreamEnvelope } from '../../src/utils/mcp/mcpStreamParser.ts'
import assert from 'node:assert/strict'
import test from 'node:test'
import { mcpProgressTestRoute } from '../../src/router/mcpProgressTestRoute.ts'
import { mcpProgressTestSystemChildren } from '../../src/router/mcpProgressTestSystemChildren.ts'
import { McpDiagnosisClientError, streamMcpDiagnosis } from '../../src/utils/mcp/mcpDiagnosisClient.ts'
import {
  appendMcpEnvelope,
  applyFinalEnvelope,
  beginResultStream,
  createMcpProgressState,
  createProgressDisplay,
  DEFAULT_MCP_PROGRESS_TAB,
  parseArgumentsJson,
  presentMcpPageError,
  RESULT_STREAM_EMPTY_TEXT,
  stopResultStream,
} from '../../src/views/mcp-progress-test/viewModel.ts'

function envelope(rawBlock: string, value: McpStreamEnvelope['envelope']): McpStreamEnvelope {
  return { rawBlock, envelope: value }
}

test('route exports a hidden authenticated MCP progress page', () => {
  assert.equal(mcpProgressTestRoute.path, '/mcp-progress-test')
  assert.equal(mcpProgressTestRoute.name, 'McpProgressTest')
  assert.equal(mcpProgressTestRoute.meta?.title, 'MCP 流式诊断测试')
  assert.equal(mcpProgressTestRoute.meta?.sidebar, false)
  assert.equal(mcpProgressTestRoute.meta?.whiteList, undefined)
  assert.equal(mcpProgressTestSystemChildren.includes(mcpProgressTestRoute), true)
  assert.equal(mcpProgressTestSystemChildren[0], mcpProgressTestRoute)
})

test('progress display stays empty until an actual progress notification arrives', () => {
  const state = createMcpProgressState({
    toolName: 'diagnose_line_stoppage',
    progressToken: 'progress-token',
  })

  assert.equal(state.hasProgress, false)
  assert.equal(state.progress, null)
  assert.equal(state.total, null)
  assert.deepEqual(createProgressDisplay(state), {
    valueLabel: '—',
    percentage: null,
  })
})

test('line diagnosis renders readable business progress while retaining raw JSON separately', () => {
  const state = createMcpProgressState({
    toolName: 'diagnose_line_stoppage',
    progressToken: 'result-token-123456',
  })
  beginResultStream(state)

  const pushProgress = (raw: string, progressToken: string, message: string) => {
    appendMcpEnvelope(state, envelope(raw, {
      jsonrpc: '2.0',
      method: 'notifications/progress',
      params: { progressToken, progress: 75, total: 100, message },
    }), { expectedProgressToken: 'result-token-123456' })
  }

  const events = [
    { type: 'start', taskId: 'task-1', stationCodes: ['OP010', 'OP020', 'OP030'] },
    { type: 'station_done', taskId: 'task-1', stationCode: 'OP010', completedCount: 1, totalCount: 3 },
    { type: 'station_done', taskId: 'task-1', stationCode: 'OP020', completedCount: 2, totalCount: 3 },
    { type: 'station_done', taskId: 'task-1', stationCode: 'OP030', completedCount: 3, totalCount: 3 },
    { type: 'complete', taskId: 'task-1', completedCount: 3, totalCount: 3, payload: { totalStations: 3 } },
  ]
  pushProgress('raw-wrong-token', 'other-token-123456', JSON.stringify(events[0]))
  events.forEach((event, index) => {
    pushProgress(`raw-event-${index + 1}`, 'result-token-123456', JSON.stringify(event))
  })

  assert.equal(state.resultChunks.length, 5)
  assert.deepEqual(state.resultChunks.map(item => item.eventType), [
    'start',
    'station_done',
    'station_done',
    'station_done',
    'complete',
  ])
  assert.deepEqual(state.resultChunks.map(item => item.stationCode), [undefined, 'OP010', 'OP020', 'OP030', undefined])
  assert.deepEqual(state.resultChunks.map(item => JSON.parse(item.rawContent ?? '')), events)
  assert.deepEqual(state.resultChunks.map(item => item.content), [
    '开始产线停机诊断，共 3 个工站',
    '工站 OP010 诊断完成（1/3）',
    '工站 OP020 诊断完成（2/3）',
    '工站 OP030 诊断完成（3/3）',
    '产线诊断完成：3 个工站',
  ])
  assert.equal(state.resultChunks.some(item => item.content.trimStart().startsWith('{')), false)
  assert.equal(state.timeline[0]?.message, '开始产线停机诊断，共 3 个工站')
  assert.equal(state.timeline[1]?.message, '工站 OP010 诊断完成（1/3）')
  assert.equal(state.rawBlocks.length, 6)

  stopResultStream(state)
  pushProgress('raw-after-cancel', 'result-token-123456', JSON.stringify({ type: 'station_done', stationCode: 'OP040' }))
  assert.equal(state.resultChunks.length, 5)
  assert.equal(state.rawBlocks.length, 7)
})

test('line llm_done exposes the complete page-visible AI diagnosis conclusion in the result stream', () => {
  const state = createMcpProgressState({
    toolName: 'diagnose_line_stoppage',
    progressToken: 'llm-token-123456',
  })
  beginResultStream(state)
  const llmReport = {
    success: true,
    rootCauseStation: 'OP020',
    rootCauseDescription: '机器人同步结束信号为 False，Graph 步骤卡在 Transition。',
    confidence: 0.95,
    secondaryCauses: [{ stationCode: 'OP010', description: '上游流程尚未结束。' }],
    impactChain: '根因:OP020 → 受影响:[OP030,OP040]',
    suggestedActions: ['检查机器人通信状态。', '检查 FirstWork 变量。'],
    reasoning: '根据工站状态与 GRAPH 转移条件定位根因。',
  }

  appendMcpEnvelope(state, envelope('raw-llm-done', {
    jsonrpc: '2.0',
    method: 'notifications/progress',
    params: {
      progressToken: 'llm-token-123456',
      progress: 98,
      total: 100,
      message: JSON.stringify({ type: 'llm_done', taskId: 'task-1', llmReport }),
    },
  }))

  const chunk = state.resultChunks[0] as any
  assert.equal(chunk.content, 'AI 诊断完成：根因工位 OP020，置信度 95%')
  assert.equal(chunk.content.trimStart().startsWith('{'), false)
  assert.deepEqual(chunk.llmReport, llmReport)
  assert.equal(chunk.llmReport.rootCauseStation, 'OP020')
  assert.equal(chunk.llmReport.confidence, 0.95)
  assert.equal(chunk.llmReport.secondaryCauses[0].stationCode, 'OP010')
  assert.equal(chunk.llmReport.suggestedActions.length, 2)
  assert.match(chunk.llmReport.reasoning, /GRAPH/)
})

test('energy diagnosis keeps supporting legacy result fragment messages', () => {
  const state = createMcpProgressState({
    toolName: 'diagnose_energy_flow',
    progressToken: 'energy-token-123456',
  })
  beginResultStream(state)

  for (const message of ['正在分析', '结果片段：第一段', '结果片段：第二段']) {
    appendMcpEnvelope(state, envelope(message, {
      jsonrpc: '2.0',
      method: 'notifications/progress',
      params: { progressToken: 'energy-token-123456', progress: 75, total: 100, message },
    }))
  }

  assert.deepEqual(state.resultChunks, [
    { sequence: 1, content: '第一段', progressTokenSuffix: '123456' },
    { sequence: 2, content: '第二段', progressTokenSuffix: '123456' },
  ])
})

test('result stream is the default tab and has an explicit empty state without fake fragments', () => {
  const state = createMcpProgressState({
    toolName: 'diagnose_energy_flow',
    progressToken: 'empty-token',
  })

  assert.equal(DEFAULT_MCP_PROGRESS_TAB, 'stream')
  assert.equal(RESULT_STREAM_EMPTY_TEXT, '等待服务端诊断过程事件')
  assert.deepEqual(state.resultChunks, [])
})

test('progress without total preserves the actual value without inventing a percentage or nested badge', () => {
  const state = createMcpProgressState({
    toolName: 'diagnose_line_stoppage',
    progressToken: 'progress-token',
  })

  appendMcpEnvelope(state, envelope('raw-without-total', {
    jsonrpc: '2.0',
    method: 'notifications/progress',
    params: {
      progressToken: 'progress-token',
      progress: 76,
      message: 'Actual message',
    },
  }))

  assert.equal(state.hasProgress, true)
  assert.equal(state.progress, 76)
  assert.equal(state.total, null)
  assert.deepEqual(createProgressDisplay(state), {
    valueLabel: '76',
    percentage: null,
  })
  assert.deepEqual(state.timeline[0], {
    sequence: 1,
    progress: 76,
    total: null,
    percentage: null,
    message: 'Actual message',
    progressTokenSuffix: '-token',
    nestedIo: false,
  })
})

test('view-model appends actual raw envelopes and only timelines actual progress notifications', () => {
  const state = createMcpProgressState({
    toolName: 'diagnose_line_stoppage',
    progressToken: 'progress-token-abcdef',
  })

  appendMcpEnvelope(state, envelope(
    'event: message\ndata: {"jsonrpc":"2.0","method":"notifications/message","params":{"message":"hello"}}',
    { jsonrpc: '2.0', method: 'notifications/message', params: { message: 'hello' } },
  ))
  appendMcpEnvelope(state, envelope(
    'data: {"jsonrpc":"2.0","method":"notifications/progress","params":{"progressToken":"progress-token-abcdef","progress":76,"total":100,"message":"IO constraints"}}',
    {
      jsonrpc: '2.0',
      method: 'notifications/progress',
      params: {
        progressToken: 'progress-token-abcdef',
        progress: 76,
        total: 100,
        message: 'IO constraints',
      },
    },
  ))

  assert.equal(state.rawBlocks.length, 2)
  assert.equal(state.rawBlocks[0], 'event: message\ndata: {"jsonrpc":"2.0","method":"notifications/message","params":{"message":"hello"}}')
  assert.equal(state.timeline.length, 1)
  assert.deepEqual(state.timeline[0], {
    sequence: 1,
    progress: 76,
    total: 100,
    percentage: 76,
    message: 'IO constraints',
    progressTokenSuffix: 'abcdef',
    nestedIo: true,
  })
  assert.equal(state.progress, 76)
  assert.equal(state.total, 100)
  assert.equal(state.hasProgress, true)
  assert.deepEqual(createProgressDisplay(state), {
    valueLabel: '76 / 100',
    percentage: 76,
  })
  assert.equal(state.progressTokenSuffix, 'abcdef')
})

test('nested IO badge applies only to line progress in the inclusive 70-82 interval', () => {
  const line = createMcpProgressState({ toolName: 'diagnose_line_stoppage', progressToken: 'line-token' })
  const energy = createMcpProgressState({ toolName: 'diagnose_energy_flow', progressToken: 'energy-token' })

  for (const progress of [69, 70, 82, 83]) {
    const item = envelope(`raw-${progress}`, {
      jsonrpc: '2.0',
      method: 'notifications/progress',
      params: { progressToken: 'line-token', progress, total: 100, message: `p${progress}` },
    })
    appendMcpEnvelope(line, item)
    appendMcpEnvelope(energy, item)
  }

  assert.deepEqual(line.timeline.map(item => item.nestedIo), [false, true, true, false])
  assert.deepEqual(energy.timeline.map(item => item.nestedIo), [false, false, false, false])
})

test('response callbacks stay raw-only and only an explicitly applied matching final can set business result', () => {
  const state = createMcpProgressState({ toolName: 'diagnose_energy_flow', progressToken: 'final-token' })
  const otherRaw = '{"jsonrpc":"2.0","id":"other","result":{"structuredContent":{"isSuccess":true}}}'

  appendMcpEnvelope(state, envelope(otherRaw, {
    jsonrpc: '2.0',
    id: 'other',
    result: { structuredContent: { isSuccess: true } },
  }))

  assert.equal(state.rawBlocks.length, 1)
  assert.equal(state.rawBlocks[0], otherRaw)
  assert.equal(state.timeline.length, 0)
  assert.equal(state.finalResult, undefined)
  assert.equal(state.businessFailure, undefined)

  applyFinalEnvelope(state, {
    jsonrpc: '2.0',
    id: 'mcp-1',
    result: {
      structuredContent: {
        isSuccess: false,
        errorMessage: 'No station data',
      },
    },
  })

  assert.deepEqual(state.finalResult, {
    structuredContent: {
      isSuccess: false,
      errorMessage: 'No station data',
    },
  })
  const businessFailure = state.businessFailure as { message: string } | undefined
  assert.equal(businessFailure?.message, 'No station data')
})

test('missing-final and JSON-RPC failures leave final state empty after raw callback delivery', async () => {
  const scenarios = [
    {
      code: 'missing_final_result',
      chunk: 'data: {"jsonrpc":"2.0","id":"other","result":{"notFinal":true}}\n\n',
    },
    {
      code: 'json_rpc_error',
      chunk: 'data: {"jsonrpc":"2.0","id":21,"error":{"code":-32000,"message":"failed"}}\n\n',
    },
  ] as const

  for (const scenario of scenarios) {
    const state = createMcpProgressState({
      toolName: 'diagnose_energy_flow',
      progressToken: 'progress-21',
    })
    let caught: unknown
    try {
      const finalEnvelope = await streamMcpDiagnosis({
        endpoint: '/mcp',
        token: 'jwt-value',
        toolName: 'diagnose_energy_flow',
        arguments: {},
        requestId: 21,
        progressToken: 'progress-21',
        fetch: async () => new Response(scenario.chunk, {
          status: 200,
          headers: { 'Content-Type': 'text/event-stream' },
        }),
        onEnvelope: item => appendMcpEnvelope(state, item),
      })
      applyFinalEnvelope(state, finalEnvelope)
    }
    catch (error) {
      caught = error
    }

    assert.ok(caught instanceof McpDiagnosisClientError)
    assert.equal(caught.code, scenario.code)
    assert.equal(state.rawBlocks.length, 1)
    assert.equal(state.finalResult, undefined)
    assert.equal(state.businessFailure, undefined)
  }
})

test('arguments JSON validation is deterministic and requires an object', () => {
  assert.deepEqual(parseArgumentsJson('{"projectIds":[1]}'), {
    ok: true,
    value: { projectIds: [1] },
  })
  assert.deepEqual(parseArgumentsJson('{oops'), {
    ok: false,
    message: '参数 JSON 无效，请检查语法。',
  })
  assert.deepEqual(parseArgumentsJson('[1,2]'), {
    ok: false,
    message: '参数 JSON 必须是对象。',
  })
  assert.deepEqual(parseArgumentsJson('null'), {
    ok: false,
    message: '参数 JSON 必须是对象。',
  })
})

test('typed errors have deterministic safe presentation', () => {
  const cases = [
    ['unauthorized', '登录凭据已失效或无权访问 MCP，请重新登录。'],
    ['json_rpc_error', 'MCP 返回了 JSON-RPC 错误。'],
    ['aborted', '请求已取消。'],
    ['network_error', '无法连接 MCP 服务，请检查网络和后端状态。'],
    ['missing_final_result', 'MCP 流已结束，但没有收到最终 Result。'],
    ['invalid_response', 'MCP 响应不是合法的 JSON-RPC 数据。'],
  ] as const

  for (const [code, expected] of cases) {
    const error = new McpDiagnosisClientError(code, 'unsafe implementation detail')
    assert.deepEqual(presentMcpPageError(error), { code, message: expected })
  }
  assert.deepEqual(presentMcpPageError(new Error('Bearer should-not-leak')), {
    code: 'unknown',
    message: 'MCP 请求发生未知错误。',
  })
})

test('malformed SSE JSON and envelopes reach the page as safe explicit invalid-response errors', async () => {
  const secret = 'unsafe-parser-payload'
  const chunks = [
    `data: {"jsonrpc":"2.0","payload":"${secret}"\n\n`,
    `data: {"jsonrpc":"2.0","id":9,"payload":"${secret}"}\n\n`,
  ]

  for (const chunk of chunks) {
    let caught: unknown
    try {
      await streamMcpDiagnosis({
        endpoint: '/mcp',
        token: 'jwt-value',
        toolName: 'diagnose_energy_flow',
        arguments: {},
        requestId: 9,
        progressToken: 'progress-9',
        fetch: async () => new Response(chunk, {
          status: 200,
          headers: { 'Content-Type': 'text/event-stream' },
        }),
      })
    }
    catch (error) {
      caught = error
    }

    assert.deepEqual(presentMcpPageError(caught), {
      code: 'invalid_response',
      message: 'MCP 响应不是合法的 JSON-RPC 数据。',
    })
    assert.equal(JSON.stringify(caught).includes(secret), false)
  }
})

test('safe display state never retains the platform JWT', () => {
  const jwt = 'eyJ-secret-platform-token'
  const state = createMcpProgressState({
    toolName: 'diagnose_line_stoppage',
    progressToken: 'token-123456',
    jwt,
  })

  assert.equal(JSON.stringify(state).includes(jwt), false)
  assert.equal(state.progressTokenSuffix, '123456')
})
