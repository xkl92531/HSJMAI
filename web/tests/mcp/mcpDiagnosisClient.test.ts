import assert from 'node:assert/strict'
import test from 'node:test'

import {
  buildMcpToolCall,
  McpDiagnosisClientError,
  streamMcpDiagnosis,
} from '../../src/utils/mcp/mcpDiagnosisClient.ts'
import {
  DEFAULT_MCP_DIAGNOSIS_PRESET,
  MCP_DIAGNOSIS_PRESETS,
} from '../../src/views/mcp-progress-test/presets.ts'

const encoder = new TextEncoder()

function streamResponse(chunks: string[], init: ResponseInit = {}) {
  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      for (const chunk of chunks) {
        controller.enqueue(encoder.encode(chunk))
      }
      controller.close()
    },
  })

  return new Response(stream, {
    status: 200,
    headers: { 'Content-Type': 'text/event-stream; charset=utf-8' },
    ...init,
  })
}

function trackedOpenStreamResponse(
  chunk: string,
  options: { cancelError?: Error } = {},
) {
  const state = { cancelCalls: 0 }
  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      controller.enqueue(encoder.encode(chunk))
    },
    cancel() {
      state.cancelCalls++
      if (options.cancelError) {
        throw options.cancelError
      }
    },
  })

  return {
    response: new Response(stream, {
      status: 200,
      headers: { 'Content-Type': 'text/event-stream' },
    }),
    state,
    stream,
  }
}

function assertClientError(code: string, secret?: string) {
  return (error: unknown) => {
    assert.ok(error instanceof McpDiagnosisClientError)
    assert.equal(error.code, code)
    if (secret) {
      assert.equal(`${error.message} ${JSON.stringify(error.details)}`.includes(secret), false)
    }
    return true
  }
}

test('builds exact MCP tools/call headers and JSON-RPC body', () => {
  const request = buildMcpToolCall({
    endpoint: '/mcp',
    token: 'jwt-value',
    toolName: 'diagnose_line_stoppage',
    arguments: { includeConstraintAnalysis: true },
    requestId: 102,
    progressToken: 'progress-102',
  })

  assert.equal(request.endpoint, '/mcp')
  assert.deepEqual([...request.headers.entries()], [
    ['accept', 'application/json, text/event-stream'],
    ['authorization', 'Bearer jwt-value'],
    ['content-type', 'application/json'],
    ['mcp-method', 'tools/call'],
    ['mcp-name', 'diagnose_line_stoppage'],
    ['mcp-protocol-version', '2026-07-28'],
  ])
  assert.deepEqual(request.body, {
    jsonrpc: '2.0',
    id: 102,
    method: 'tools/call',
    params: {
      name: 'diagnose_line_stoppage',
      arguments: { includeConstraintAnalysis: true },
      _meta: {
        'progressToken': 'progress-102',
        'io.modelcontextprotocol/protocolVersion': '2026-07-28',
        'io.modelcontextprotocol/clientInfo': { name: 'hsjm-mcp-progress-test', version: '1.0.0' },
        'io.modelcontextprotocol/clientCapabilities': {},
      },
    },
  })

  const metadata = request.body.params._meta as Record<string, unknown>
  assert.equal('protocolVersion' in metadata, false)
  assert.equal('clientInfo' in metadata, false)
  assert.equal('capabilities' in metadata, false)
})

test('rejects missing JWT before fetch', async () => {
  let fetched = false

  await assert.rejects(
    streamMcpDiagnosis({
      endpoint: '/mcp',
      token: '   ',
      toolName: 'diagnose_energy_flow',
      arguments: {},
      fetch: async () => {
        fetched = true
        return new Response()
      },
    }),
    assertClientError('missing_token'),
  )
  assert.equal(fetched, false)
})

test('incrementally emits every SSE envelope and returns only the matching final id', async () => {
  const seen: unknown[] = []
  let capturedBody = ''

  const result = await streamMcpDiagnosis({
    endpoint: '/mcp',
    token: 'jwt-value',
    toolName: 'diagnose_energy_flow',
    arguments: { productionLineId: 1 },
    requestId: 'request-7',
    progressToken: 'progress-7',
    fetch: async (_input, init) => {
      capturedBody = String(init?.body)
      return streamResponse([
        'data: {"jsonrpc":"2.0","method":"notifications/pro',
        'gress","params":{"progressToken":"progress-7","progress":30,"total":100}}\n\n',
        'data: {"jsonrpc":"2.0","id":"other","result":{"ignored":true}}\n\n',
        'data: {"jsonrpc":"2.0","id":"request-7","result":{"content":[]}}',
      ])
    },
    onEnvelope: item => seen.push(item.envelope),
  })

  assert.equal(seen.length, 3)
  assert.deepEqual(result.result, { content: [] })
  assert.equal(JSON.parse(capturedBody).params._meta.progressToken, 'progress-7')
})

test('cancels an active SSE reader and releases its lock after a final result', async () => {
  const tracked = trackedOpenStreamResponse(
    'data: {"jsonrpc":"2.0","id":71,"result":{"ok":true}}\n\n',
  )

  const result = await streamMcpDiagnosis({
    endpoint: '/mcp',
    token: 'jwt-value',
    toolName: 'diagnose_energy_flow',
    arguments: {},
    requestId: 71,
    progressToken: 'p71',
    fetch: async () => tracked.response,
  })

  assert.deepEqual(result.result, { ok: true })
  assert.equal(tracked.state.cancelCalls, 1)
  assert.equal(tracked.stream.locked, false)
})

test('releases an SSE reader after EOF without cancelling an already closed stream', async () => {
  const response = streamResponse([
    'data: {"jsonrpc":"2.0","method":"notifications/progress","params":{"progress":50}}\n\n',
  ])
  assert.ok(response.body)
  const stream = response.body

  await assert.rejects(
    streamMcpDiagnosis({
      endpoint: '/mcp',
      token: 'jwt-value',
      toolName: 'diagnose_energy_flow',
      arguments: {},
      requestId: 72,
      progressToken: 'p72',
      fetch: async () => response,
    }),
    assertClientError('missing_final_result'),
  )

  assert.equal(stream.locked, false)
})

test('maps malformed SSE JSON and envelopes to safe invalid_response errors', async (context) => {
  const secret = 'parser-secret-jwt'
  const cases = [
    { name: 'malformed JSON', chunk: `data: {"jsonrpc":"2.0","secret":"${secret}"\n\n` },
    { name: 'invalid envelope', chunk: `data: {"jsonrpc":"2.0","id":73,"secret":"${secret}"}\n\n` },
  ]

  for (const item of cases) {
    await context.test(item.name, async () => {
      const tracked = trackedOpenStreamResponse(
        item.chunk,
        { cancelError: new Error('cancel secret') },
      )

      await assert.rejects(
        streamMcpDiagnosis({
          endpoint: '/mcp',
          token: secret,
          toolName: 'diagnose_energy_flow',
          arguments: {},
          requestId: 73,
          progressToken: 'p73',
          fetch: async () => tracked.response,
        }),
        assertClientError('invalid_response', secret),
      )

      assert.equal(tracked.state.cancelCalls, 1)
      assert.equal(tracked.stream.locked, false)
    })
  }
})

test('maps callback failure to a safe typed error and cleans up the reader', async () => {
  const secret = 'callback-secret-jwt'
  const tracked = trackedOpenStreamResponse(
    'data: {"jsonrpc":"2.0","method":"notifications/progress","params":{"progress":30}}\n\n',
  )

  await assert.rejects(
    streamMcpDiagnosis({
      endpoint: '/mcp',
      token: secret,
      toolName: 'diagnose_energy_flow',
      arguments: {},
      requestId: 74,
      progressToken: 'p74',
      fetch: async () => tracked.response,
      onEnvelope: () => {
        throw new Error(`unsafe ${secret}`)
      },
    }),
    assertClientError('callback_error', secret),
  )

  assert.equal(tracked.state.cancelCalls, 1)
  assert.equal(tracked.stream.locked, false)
})

test('maps an SSE reader AbortError and still cancels and releases the reader', async () => {
  const state = { cancelCalls: 0, releaseCalls: 0 }
  const reader = {
    async read() {
      const error = new Error('unsafe abort detail')
      error.name = 'AbortError'
      throw error
    },
    async cancel() {
      state.cancelCalls++
    },
    releaseLock() {
      state.releaseCalls++
    },
  }
  const response = {
    status: 200,
    ok: true,
    headers: new Headers({ 'Content-Type': 'text/event-stream' }),
    body: { getReader: () => reader },
  } as unknown as Response

  await assert.rejects(
    streamMcpDiagnosis({
      endpoint: '/mcp',
      token: 'jwt-value',
      toolName: 'diagnose_energy_flow',
      arguments: {},
      requestId: 741,
      progressToken: 'p741',
      fetch: async () => response,
    }),
    assertClientError('aborted'),
  )

  assert.equal(state.cancelCalls, 1)
  assert.equal(state.releaseCalls, 1)
})

test('cancel failure cannot replace a successfully received final result', async () => {
  const tracked = trackedOpenStreamResponse(
    'data: {"jsonrpc":"2.0","id":75,"result":{"ok":true}}\n\n',
    { cancelError: new Error('cleanup failed') },
  )

  const result = await streamMcpDiagnosis({
    endpoint: '/mcp',
    token: 'jwt-value',
    toolName: 'diagnose_energy_flow',
    arguments: {},
    requestId: 75,
    progressToken: 'p75',
    fetch: async () => tracked.response,
  })

  assert.deepEqual(result.result, { ok: true })
  assert.equal(tracked.state.cancelCalls, 1)
  assert.equal(tracked.stream.locked, false)
})

test('uses a UUID progress token by default', async () => {
  const originalRandomUUID = globalThis.crypto.randomUUID
  let body: Record<string, any> | undefined
  Object.defineProperty(globalThis.crypto, 'randomUUID', {
    configurable: true,
    value: () => '00000000-0000-4000-8000-000000000001',
  })

  try {
    await streamMcpDiagnosis({
      endpoint: '/mcp',
      token: 'jwt-value',
      toolName: 'diagnose_energy_flow',
      arguments: {},
      requestId: 9,
      fetch: async (_input, init) => {
        body = JSON.parse(String(init?.body))
        return Response.json({ jsonrpc: '2.0', id: 9, result: { ok: true } })
      },
    })
  }
  finally {
    Object.defineProperty(globalThis.crypto, 'randomUUID', {
      configurable: true,
      value: originalRandomUUID,
    })
  }

  assert.equal(body?.params._meta.progressToken, '00000000-0000-4000-8000-000000000001')
})

test('falls back to crypto.getRandomValues with an RFC4122 v4 UUID', async () => {
  const cryptoDescriptor = Object.getOwnPropertyDescriptor(globalThis, 'crypto')
  let body: Record<string, any> | undefined
  Object.defineProperty(globalThis, 'crypto', {
    configurable: true,
    value: {
      getRandomValues(array: Uint8Array) {
        array.forEach((_value, index) => {
          array[index] = index
        })
        return array
      },
    },
  })

  try {
    await streamMcpDiagnosis({
      endpoint: '/mcp',
      token: 'jwt-value',
      toolName: 'diagnose_energy_flow',
      arguments: {},
      requestId: 10,
      fetch: async (_input, init) => {
        body = JSON.parse(String(init?.body))
        return Response.json({ jsonrpc: '2.0', id: 10, result: { ok: true } })
      },
    })
  }
  finally {
    if (cryptoDescriptor) {
      Object.defineProperty(globalThis, 'crypto', cryptoDescriptor)
    }
  }

  assert.equal(body?.params._meta.progressToken, '00010203-0405-4607-8809-0a0b0c0d0e0f')
})

test('rejects with uuid_unavailable before fetch when secure UUID APIs are absent', async () => {
  const cryptoDescriptor = Object.getOwnPropertyDescriptor(globalThis, 'crypto')
  let fetched = false
  Object.defineProperty(globalThis, 'crypto', {
    configurable: true,
    value: {},
  })

  try {
    await assert.rejects(
      streamMcpDiagnosis({
        endpoint: '/mcp',
        token: 'jwt-value',
        toolName: 'diagnose_energy_flow',
        arguments: {},
        requestId: 11,
        fetch: async () => {
          fetched = true
          return Response.json({ jsonrpc: '2.0', id: 11, result: {} })
        },
      }),
      assertClientError('uuid_unavailable'),
    )
  }
  finally {
    if (cryptoDescriptor) {
      Object.defineProperty(globalThis, 'crypto', cryptoDescriptor)
    }
  }

  assert.equal(fetched, false)
})

test('accepts application/json fallback and emits its actual envelope', async () => {
  const seen: unknown[] = []
  const result = await streamMcpDiagnosis({
    endpoint: '/mcp',
    token: 'jwt-value',
    toolName: 'diagnose_line_stoppage',
    arguments: {},
    requestId: 44,
    progressToken: 'p44',
    fetch: async () => Response.json({ jsonrpc: '2.0', id: 44, result: { value: 3 } }),
    onEnvelope: item => seen.push(item.envelope),
  })

  assert.equal(seen.length, 1)
  assert.deepEqual(result.result, { value: 3 })
})

test('forwards the caller AbortSignal and authenticated MCP headers to fetch', async () => {
  const controller = new AbortController()
  let fetched = false

  await streamMcpDiagnosis({
    endpoint: '/mcp',
    token: 'jwt-value',
    toolName: 'diagnose_energy_flow',
    arguments: {},
    requestId: 45,
    progressToken: 'p45',
    signal: controller.signal,
    fetch: async (_input, init) => {
      fetched = true
      assert.equal(init?.signal, controller.signal)
      const headers = new Headers(init?.headers)
      assert.equal(headers.get('Authorization'), 'Bearer jwt-value')
      assert.equal(headers.get('Mcp-Name'), 'diagnose_energy_flow')
      return Response.json({ jsonrpc: '2.0', id: 45, result: { ok: true } })
    },
  })

  assert.equal(fetched, true)
})

test('maps HTTP, protocol, empty-body, and incomplete-stream failures to typed errors', async (t) => {
  const secret = 'very-secret-jwt'
  const cases: Array<{
    name: string
    code: string
    response: () => Response
  }> = [
    { name: '401', code: 'unauthorized', response: () => new Response('token rejected', { status: 401 }) },
    { name: 'non-2xx', code: 'http_error', response: () => new Response('bad gateway', { status: 502 }) },
    { name: 'JSON-RPC error', code: 'json_rpc_error', response: () => Response.json({ jsonrpc: '2.0', id: 5, error: { code: -32602, message: 'invalid params' } }) },
    { name: 'missing body', code: 'missing_body', response: () => new Response(null, { status: 200, headers: { 'Content-Type': 'text/event-stream' } }) },
    { name: 'stream end without final', code: 'missing_final_result', response: () => streamResponse(['data: {"jsonrpc":"2.0","method":"notifications/progress","params":{"progress":50}}\n\n']) },
  ]

  for (const item of cases) {
    await t.test(item.name, async () => {
      await assert.rejects(
        streamMcpDiagnosis({
          endpoint: '/mcp',
          token: secret,
          toolName: 'diagnose_energy_flow',
          arguments: {},
          requestId: 5,
          progressToken: 'p5',
          fetch: async () => item.response(),
        }),
        assertClientError(item.code, secret),
      )
    })
  }
})

test('rejects malformed application/json envelopes before callback delivery', async () => {
  const invalidEnvelopes = [
    { jsonrpc: '1.0', id: 5, result: {} },
    { jsonrpc: '2.0', id: 5 },
    { jsonrpc: '2.0', method: '' },
    { jsonrpc: '2.0', method: 'notifications/progress', params: 1 },
    { jsonrpc: '2.0', method: 'notifications/progress', params: null },
  ]

  for (const envelope of invalidEnvelopes) {
    let callbackCalls = 0
    await assert.rejects(
      streamMcpDiagnosis({
        endpoint: '/mcp',
        token: 'jwt-value',
        toolName: 'diagnose_energy_flow',
        arguments: {},
        requestId: 5,
        progressToken: 'p5',
        fetch: async () => Response.json(envelope),
        onEnvelope: () => callbackCalls++,
      }),
      assertClientError('invalid_response'),
    )
    assert.equal(callbackCalls, 0)
  }
})

test('application/json fallback delivers notifications with object or array params', async () => {
  const validParams = [
    { progress: 20 },
    [],
  ]

  for (const params of validParams) {
    const seen: unknown[] = []
    await assert.rejects(
      streamMcpDiagnosis({
        endpoint: '/mcp',
        token: 'jwt-value',
        toolName: 'diagnose_energy_flow',
        arguments: {},
        requestId: 5,
        progressToken: 'p5',
        fetch: async () => Response.json({
          jsonrpc: '2.0',
          method: 'notifications/progress',
          params,
        }),
        onEnvelope: item => seen.push(item.envelope.params),
      }),
      assertClientError('missing_final_result'),
    )
    assert.deepEqual(seen, [params])
  }
})

test('maps abort and network failures without exposing JWT', async (t) => {
  const secret = 'network-secret-jwt'

  await t.test('abort', async () => {
    await assert.rejects(
      streamMcpDiagnosis({
        endpoint: '/mcp',
        token: secret,
        toolName: 'diagnose_energy_flow',
        arguments: {},
        signal: AbortSignal.abort(),
        fetch: async () => {
          throw new DOMException('request aborted', 'AbortError')
        },
      }),
      assertClientError('aborted', secret),
    )
  })

  await t.test('network', async () => {
    await assert.rejects(
      streamMcpDiagnosis({
        endpoint: '/mcp',
        token: secret,
        toolName: 'diagnose_energy_flow',
        arguments: {},
        fetch: async () => {
          throw new TypeError(`failed with ${secret}`)
        },
      }),
      assertClientError('network_error', secret),
    )
  })

  await t.test('AbortError name without an already-aborted signal', async () => {
    await assert.rejects(
      streamMcpDiagnosis({
        endpoint: '/mcp',
        token: secret,
        toolName: 'diagnose_energy_flow',
        arguments: {},
        requestId: 12,
        progressToken: 'p12',
        fetch: async () => {
          const error = new Error(secret)
          error.name = 'AbortError'
          throw error
        },
      }),
      assertClientError('aborted', secret),
    )
  })
})

test('defines line diagnosis presets with the same switches and defaults as the production page', () => {
  assert.equal(DEFAULT_MCP_DIAGNOSIS_PRESET, 'lineWithTraditionalModel')
  assert.deepEqual(Object.keys(MCP_DIAGNOSIS_PRESETS), [
    'energyFlow',
    'lineWithConstraintNoModel',
    'lineWithTraditionalModel',
    'lineWithAgent',
  ])
  assert.equal(MCP_DIAGNOSIS_PRESETS.energyFlow.toolName, 'diagnose_energy_flow')
  assert.deepEqual(MCP_DIAGNOSIS_PRESETS.lineWithConstraintNoModel.arguments, {
    projectIds: [],
    stationCodes: [],
    useLlmAnalysis: false,
    forceLlmSummaryWhenNoAbnormal: false,
    useAgentDiagnosis: false,
    includeConstraintAnalysis: true,
  })
  assert.deepEqual(MCP_DIAGNOSIS_PRESETS.lineWithTraditionalModel.arguments, {
    projectIds: [],
    stationCodes: [],
    useLlmAnalysis: true,
    forceLlmSummaryWhenNoAbnormal: true,
    useAgentDiagnosis: false,
    includeConstraintAnalysis: true,
  })
  assert.deepEqual(MCP_DIAGNOSIS_PRESETS.lineWithAgent.arguments, {
    projectIds: [],
    stationCodes: [],
    useLlmAnalysis: true,
    forceLlmSummaryWhenNoAbnormal: true,
    useAgentDiagnosis: true,
    agentMaxIterations: 12,
    includeConstraintAnalysis: true,
  })
})
