export type McpRequestId = string | number | null

export interface McpJsonRpcError {
  code: number
  message: string
  data?: unknown
}

export interface McpJsonRpcEnvelope {
  jsonrpc: '2.0'
  id?: McpRequestId
  method?: string
  params?: unknown
  result?: unknown
  error?: McpJsonRpcError
  [key: string]: unknown
}

export interface McpStreamEnvelope {
  event?: string
  rawBlock: string
  envelope: McpJsonRpcEnvelope
}

export type McpStreamParserErrorCode = 'invalid_json' | 'invalid_envelope'

export class McpStreamParserError extends Error {
  readonly code: McpStreamParserErrorCode
  readonly rawBlock: string

  constructor(code: McpStreamParserErrorCode, rawBlock: string) {
    super(code === 'invalid_json'
      ? 'The MCP event contains invalid JSON.'
      : 'The MCP event is not a JSON-RPC object.')
    this.name = 'McpStreamParserError'
    this.code = code
    this.rawBlock = rawBlock
  }
}

function hasOwn(value: object, key: string) {
  return Object.prototype.hasOwnProperty.call(value, key)
}

function isValidId(value: unknown): value is McpRequestId {
  return value === null
    || typeof value === 'string'
    || (typeof value === 'number' && Number.isFinite(value))
}

function isValidError(value: unknown): value is McpJsonRpcError {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    return false
  }

  const error = value as Record<string, unknown>
  return Number.isInteger(error.code) && typeof error.message === 'string'
}

export function parseMcpJsonRpcEnvelope(
  parsed: unknown,
  rawBlock: string,
): McpJsonRpcEnvelope {
  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
    throw new McpStreamParserError('invalid_envelope', rawBlock)
  }

  const value = parsed as Record<string, unknown>
  if (value.jsonrpc !== '2.0') {
    throw new McpStreamParserError('invalid_envelope', rawBlock)
  }

  const hasId = hasOwn(value, 'id')
  const hasMethod = hasOwn(value, 'method')
  const hasParams = hasOwn(value, 'params')
  const hasResult = hasOwn(value, 'result')
  const hasError = hasOwn(value, 'error')

  if (hasId) {
    const isResponse = isValidId(value.id)
      && !hasMethod
      && !hasOwn(value, 'params')
      && hasResult !== hasError
      && (!hasError || isValidError(value.error))
    if (!isResponse) {
      throw new McpStreamParserError('invalid_envelope', rawBlock)
    }
  }
  else {
    const isNotification = hasMethod
      && typeof value.method === 'string'
      && value.method.length > 0
      && (!hasParams || (value.params !== null && typeof value.params === 'object'))
      && !hasResult
      && !hasError
    if (!isNotification) {
      throw new McpStreamParserError('invalid_envelope', rawBlock)
    }
  }

  return value as unknown as McpJsonRpcEnvelope
}

function parseEventBlock(rawBlock: string): McpStreamEnvelope[] {
  const data: string[] = []
  let event: string | undefined

  for (const line of rawBlock.split(/\r?\n/)) {
    if (!line || line.startsWith(':')) {
      continue
    }

    const separatorIndex = line.indexOf(':')
    const field = separatorIndex < 0 ? line : line.slice(0, separatorIndex)
    let value = separatorIndex < 0 ? '' : line.slice(separatorIndex + 1)
    if (value.startsWith(' ')) {
      value = value.slice(1)
    }

    if (field === 'event') {
      event = value
    }
    else if (field === 'data') {
      data.push(value)
    }
  }

  if (data.length === 0) {
    return []
  }

  let parsed: unknown
  try {
    parsed = JSON.parse(data.join('\n'))
  }
  catch {
    throw new McpStreamParserError('invalid_json', rawBlock)
  }

  return [{
    event,
    rawBlock,
    envelope: parseMcpJsonRpcEnvelope(parsed, rawBlock),
  }]
}

export class McpSseParser {
  private buffer = ''

  push(chunk: string): McpStreamEnvelope[] {
    this.buffer += chunk
    const blocks = this.buffer.split(/\r?\n\r?\n/)
    this.buffer = blocks.pop() ?? ''
    return blocks.flatMap(parseEventBlock)
  }

  finish(): McpStreamEnvelope[] {
    const tail = this.buffer
    this.buffer = ''
    return tail.trim() ? parseEventBlock(tail) : []
  }
}
