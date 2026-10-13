import type { McpJsonRpcEnvelope, McpRequestId, McpStreamEnvelope } from './mcpStreamParser'
import {
  McpSseParser,
  McpStreamParserError,
  parseMcpJsonRpcEnvelope,
} from './mcpStreamParser'

export const MCP_PROTOCOL_VERSION = '2026-07-28' as const

export type McpDiagnosisToolName = 'diagnose_energy_flow' | 'diagnose_line_stoppage'

export interface McpToolCallBody {
  jsonrpc: '2.0'
  id: Exclude<McpRequestId, null>
  method: 'tools/call'
  params: {
    name: McpDiagnosisToolName
    arguments: Record<string, unknown>
    _meta: {
      'progressToken': string
      'io.modelcontextprotocol/protocolVersion': typeof MCP_PROTOCOL_VERSION
      'io.modelcontextprotocol/clientInfo': {
        name: 'hsjm-mcp-progress-test'
        version: '1.0.0'
      }
      'io.modelcontextprotocol/clientCapabilities': Record<string, never>
    }
  }
}

export interface BuildMcpToolCallOptions {
  endpoint: string
  token: string
  toolName: McpDiagnosisToolName
  arguments: Record<string, unknown>
  requestId: Exclude<McpRequestId, null>
  progressToken: string
}

export interface McpToolCallRequest {
  endpoint: string
  headers: Headers
  body: McpToolCallBody
}

export function buildMcpToolCall(options: BuildMcpToolCallOptions): McpToolCallRequest {
  const headers = new Headers()
  headers.set('Content-Type', 'application/json')
  headers.set('Accept', 'application/json, text/event-stream')
  headers.set('Authorization', `Bearer ${options.token}`)
  headers.set('MCP-Protocol-Version', MCP_PROTOCOL_VERSION)
  headers.set('Mcp-Method', 'tools/call')
  headers.set('Mcp-Name', options.toolName)

  return {
    endpoint: options.endpoint,
    headers,
    body: {
      jsonrpc: '2.0',
      id: options.requestId,
      method: 'tools/call',
      params: {
        name: options.toolName,
        arguments: options.arguments,
        _meta: {
          'progressToken': options.progressToken,
          'io.modelcontextprotocol/protocolVersion': MCP_PROTOCOL_VERSION,
          'io.modelcontextprotocol/clientInfo': {
            name: 'hsjm-mcp-progress-test',
            version: '1.0.0',
          },
          'io.modelcontextprotocol/clientCapabilities': {},
        },
      },
    },
  }
}

export type McpDiagnosisClientErrorCode
  = | 'missing_token'
    | 'unauthorized'
    | 'http_error'
    | 'json_rpc_error'
    | 'missing_body'
    | 'invalid_response'
    | 'unsupported_content_type'
    | 'callback_error'
    | 'uuid_unavailable'
    | 'aborted'
    | 'network_error'
    | 'missing_final_result'

export class McpDiagnosisClientError extends Error {
  readonly code: McpDiagnosisClientErrorCode
  readonly details?: Readonly<Record<string, unknown>>

  constructor(
    code: McpDiagnosisClientErrorCode,
    message: string,
    details?: Readonly<Record<string, unknown>>,
  ) {
    super(message)
    this.name = 'McpDiagnosisClientError'
    this.code = code
    this.details = details
  }
}

export interface StreamMcpDiagnosisOptions {
  endpoint: string
  token: string
  toolName: McpDiagnosisToolName
  arguments: Record<string, unknown>
  requestId?: Exclude<McpRequestId, null>
  progressToken?: string
  signal?: AbortSignal
  onEnvelope?: (item: McpStreamEnvelope) => void
  fetch?: typeof globalThis.fetch
}

export function createMcpClientToken() {
  const cryptoApi = globalThis.crypto
  if (typeof cryptoApi?.randomUUID === 'function') {
    try {
      return cryptoApi.randomUUID()
    }
    catch {
      // Fall through to getRandomValues without retaining an unsafe platform error.
    }
  }

  if (typeof cryptoApi?.getRandomValues === 'function') {
    try {
      const bytes = new Uint8Array(16)
      cryptoApi.getRandomValues(bytes)
      bytes[6] = (bytes[6]! & 0x0F) | 0x40
      bytes[8] = (bytes[8]! & 0x3F) | 0x80
      const hex = [...bytes].map(value => value.toString(16).padStart(2, '0'))
      return [
        hex.slice(0, 4).join(''),
        hex.slice(4, 6).join(''),
        hex.slice(6, 8).join(''),
        hex.slice(8, 10).join(''),
        hex.slice(10, 16).join(''),
      ].join('-')
    }
    catch {
      // Convert platform failures to the same safe typed capability error below.
    }
  }

  throw new McpDiagnosisClientError(
    'uuid_unavailable',
    'A secure UUID generator is unavailable in this browser.',
  )
}

function isMatchingId(envelope: McpJsonRpcEnvelope, requestId: Exclude<McpRequestId, null>) {
  return envelope.id === requestId
}

function throwJsonRpcError(envelope: McpJsonRpcEnvelope): never {
  throw new McpDiagnosisClientError(
    'json_rpc_error',
    'The MCP server returned a JSON-RPC error.',
    typeof envelope.error?.code === 'number' ? { rpcCode: envelope.error.code } : undefined,
  )
}

function inspectEnvelope(
  item: McpStreamEnvelope,
  requestId: Exclude<McpRequestId, null>,
  onEnvelope?: (item: McpStreamEnvelope) => void,
) {
  try {
    onEnvelope?.(item)
  }
  catch {
    throw new McpDiagnosisClientError(
      'callback_error',
      'The MCP envelope callback failed.',
    )
  }

  if (!isMatchingId(item.envelope, requestId)) {
    return undefined
  }
  if (item.envelope.error) {
    throwJsonRpcError(item.envelope)
  }
  if (Object.hasOwn(item.envelope, 'result')) {
    return item.envelope
  }
  return undefined
}

function isAbortError(error: unknown) {
  if (error === null || typeof error !== 'object') {
    return false
  }
  try {
    return 'name' in error && error.name === 'AbortError'
  }
  catch {
    return false
  }
}

function safeNetworkError(error: unknown, signal?: AbortSignal): McpDiagnosisClientError {
  if (signal?.aborted || isAbortError(error)) {
    return new McpDiagnosisClientError('aborted', 'The MCP request was cancelled.')
  }
  return new McpDiagnosisClientError('network_error', 'The MCP request failed because of a network error.')
}

async function parseJsonResponse(
  response: Response,
  requestId: Exclude<McpRequestId, null>,
  onEnvelope?: (item: McpStreamEnvelope) => void,
) {
  const raw = await response.text()
  if (!raw.trim()) {
    throw new McpDiagnosisClientError('missing_body', 'The MCP response body is empty.')
  }

  let parsed: unknown
  try {
    parsed = JSON.parse(raw)
  }
  catch {
    throw new McpDiagnosisClientError('invalid_response', 'The MCP JSON response is invalid.')
  }

  let envelope: McpJsonRpcEnvelope
  try {
    envelope = parseMcpJsonRpcEnvelope(parsed, raw)
  }
  catch (error) {
    if (error instanceof McpStreamParserError) {
      throw new McpDiagnosisClientError('invalid_response', 'The MCP JSON response is not a valid JSON-RPC envelope.')
    }
    throw error
  }

  const item: McpStreamEnvelope = {
    rawBlock: raw,
    envelope,
  }
  return inspectEnvelope(item, requestId, onEnvelope)
}

async function parseSseResponse(
  response: Response,
  requestId: Exclude<McpRequestId, null>,
  signal: AbortSignal | undefined,
  onEnvelope?: (item: McpStreamEnvelope) => void,
) {
  if (!response.body) {
    throw new McpDiagnosisClientError('missing_body', 'The MCP response body is empty.')
  }

  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  const parser = new McpSseParser()
  let reachedEof = false

  try {
    while (true) {
      const { done, value } = await reader.read()
      if (done) {
        reachedEof = true
        break
      }

      for (const item of parser.push(decoder.decode(value, { stream: true }))) {
        const final = inspectEnvelope(item, requestId, onEnvelope)
        if (final) {
          return final
        }
      }
    }

    const decodedTail = decoder.decode()
    const tailItems = [
      ...(decodedTail ? parser.push(decodedTail) : []),
      ...parser.finish(),
    ]
    for (const item of tailItems) {
      const final = inspectEnvelope(item, requestId, onEnvelope)
      if (final) {
        return final
      }
    }
  }
  catch (error) {
    if (error instanceof McpDiagnosisClientError) {
      throw error
    }
    if (error instanceof McpStreamParserError) {
      throw new McpDiagnosisClientError(
        'invalid_response',
        'The MCP SSE response contains an invalid JSON-RPC envelope.',
      )
    }
    throw safeNetworkError(error, signal)
  }
  finally {
    if (!reachedEof) {
      try {
        await reader.cancel()
      }
      catch {
        // Reader cleanup must never replace a final result or the original failure.
      }
    }
    try {
      reader.releaseLock()
    }
    catch {
      // Lock cleanup is best-effort and must not alter transport semantics.
    }
  }

  return undefined
}

export async function streamMcpDiagnosis(
  options: StreamMcpDiagnosisOptions,
): Promise<McpJsonRpcEnvelope> {
  const token = options.token.trim()
  if (!token) {
    throw new McpDiagnosisClientError('missing_token', 'A login token is required before calling MCP.')
  }
  if (options.signal?.aborted) {
    throw new McpDiagnosisClientError('aborted', 'The MCP request was cancelled.')
  }

  const requestId = options.requestId ?? `mcp-${createMcpClientToken()}`
  const progressToken = options.progressToken ?? createMcpClientToken()
  const request = buildMcpToolCall({
    endpoint: options.endpoint,
    token,
    toolName: options.toolName,
    arguments: options.arguments,
    requestId,
    progressToken,
  })

  let response: Response
  try {
    response = await (options.fetch ?? globalThis.fetch)(request.endpoint, {
      method: 'POST',
      headers: request.headers,
      body: JSON.stringify(request.body),
      signal: options.signal,
    })
  }
  catch (error) {
    throw safeNetworkError(error, options.signal)
  }

  if (response.status === 401) {
    throw new McpDiagnosisClientError(
      'unauthorized',
      'The MCP request is unauthorized.',
      { status: response.status },
    )
  }
  if (!response.ok) {
    throw new McpDiagnosisClientError(
      'http_error',
      'The MCP server returned an unsuccessful HTTP status.',
      { status: response.status },
    )
  }

  const contentType = response.headers.get('Content-Type')?.toLowerCase() ?? ''
  let final: McpJsonRpcEnvelope | undefined
  if (contentType.includes('text/event-stream')) {
    final = await parseSseResponse(response, requestId, options.signal, options.onEnvelope)
  }
  else if (contentType.includes('application/json')) {
    try {
      final = await parseJsonResponse(response, requestId, options.onEnvelope)
    }
    catch (error) {
      if (error instanceof McpDiagnosisClientError) {
        throw error
      }
      throw safeNetworkError(error, options.signal)
    }
  }
  else {
    throw new McpDiagnosisClientError(
      'unsupported_content_type',
      'The MCP response content type is unsupported.',
    )
  }

  if (!final) {
    throw new McpDiagnosisClientError(
      'missing_final_result',
      'The MCP stream ended without a matching final result.',
    )
  }
  return final
}
