import assert from 'node:assert/strict'
import test from 'node:test'

import {
  McpSseParser,
  McpStreamParserError,
} from '../../src/utils/mcp/mcpStreamParser.ts'

test('parses progress and final result across arbitrary chunks and line endings', () => {
  const parser = new McpSseParser()
  const output = [
    ...parser.push(': keep-alive\r\nevent: message\r\ndata: {"jsonrpc":"2.0","method":"notifications/pro'),
    ...parser.push('gress","params":{"progressToken":"p1","progress":70,"total":100}}\r\n\r\n'),
    ...parser.push('data: {"jsonrpc":"2.0","id":7,"result":{"isSuccess":true}}'),
    ...parser.finish(),
  ]

  assert.equal(output.length, 2)
  assert.equal(output[0]?.envelope.method, 'notifications/progress')
  assert.deepEqual(output[0]?.envelope.params, {
    progressToken: 'p1',
    progress: 70,
    total: 100,
  })
  assert.equal(output[1]?.envelope.id, 7)
  assert.deepEqual(output[1]?.envelope.result, { isSuccess: true })
})

test('joins multiline data, ignores comments, and parses multiple events in one chunk', () => {
  const parser = new McpSseParser()
  const output = parser.push([
    ': heartbeat',
    'event: message',
    'data: {"jsonrpc":"2.0",',
    'data: "method":"notifications/progress","params":{"progress":25}}',
    '',
    'data: {"jsonrpc":"2.0","id":"request-2","result":{"ok":true}}',
    '',
    '',
  ].join('\n'))

  assert.equal(output.length, 2)
  assert.equal(output[0]?.event, 'message')
  assert.equal(output[0]?.envelope.method, 'notifications/progress')
  assert.equal(output[1]?.envelope.id, 'request-2')
})

test('ignores comment-only and empty SSE blocks', () => {
  const parser = new McpSseParser()

  assert.deepEqual(parser.push(': heartbeat\n\n\n\n'), [])
  assert.deepEqual(parser.finish(), [])
})

test('throws typed parser error retaining the malformed raw block', () => {
  const parser = new McpSseParser()
  const rawBlock = 'event: message\ndata: {"jsonrpc":"2.0",broken}'

  assert.throws(
    () => parser.push(`${rawBlock}\n\n`),
    (error: unknown) => {
      assert.ok(error instanceof McpStreamParserError)
      assert.equal(error.code, 'invalid_json')
      assert.equal(error.rawBlock, rawBlock)
      assert.equal(error.message.includes('broken'), false)
      return true
    },
  )
})

test('rejects invalid JSON-RPC versions and incomplete response or notification shapes', () => {
  const invalidEnvelopes = [
    '{"jsonrpc":"1.0","id":5,"result":{}}',
    '{"jsonrpc":"2.0","id":5}',
    '{"jsonrpc":"2.0","id":5,"result":{},"error":{"code":-1,"message":"bad"}}',
    '{"jsonrpc":"2.0","method":""}',
    '{"jsonrpc":"2.0","method":"notifications/progress","result":{}}',
    '{"jsonrpc":"2.0","method":"notifications/progress","params":1}',
    '{"jsonrpc":"2.0","method":"notifications/progress","params":null}',
  ]

  for (const envelope of invalidEnvelopes) {
    const parser = new McpSseParser()
    assert.throws(
      () => parser.push(`data: ${envelope}\n\n`),
      (error: unknown) => {
        assert.ok(error instanceof McpStreamParserError)
        assert.equal(error.code, 'invalid_envelope')
        return true
      },
    )
  }
})

test('accepts notification params when they are objects or arrays', () => {
  const parser = new McpSseParser()
  const output = parser.push([
    'data: {"jsonrpc":"2.0","method":"notifications/progress","params":{"progress":25}}',
    '',
    'data: {"jsonrpc":"2.0","method":"notifications/list_changed","params":[]}',
    '',
    '',
  ].join('\n'))

  assert.deepEqual(output.map(item => item.envelope.params), [
    { progress: 25 },
    [],
  ])
})
