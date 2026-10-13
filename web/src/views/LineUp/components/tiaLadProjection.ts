import type { LineUpConditionRuntimeValue, LineUpRuntimeQuality } from '@/api/modules/LineUp/monitor'
import type { LineUpGraphLogicEndpoint, LineUpGraphLogicNetwork } from '@/api/modules/LineUp/types'

export type TiaLadSymbolKind
  = | 'contact'
    | 'eq'
    | 'ge'
    | 'gt'
    | 'le'
    | 'lt'
    | 'ne'
    | 'not'
    | 'or'
    | 'il-coil'
    | 'sv-coil'
    | 'tr-coil'
    | 'unknown'

export type TiaLadRuntimeState = 'true' | 'false' | 'unknown'
export type TiaLadRuntimeQuality = 'Good' | 'Bad' | 'Unknown' | 'Stale'

export interface TiaLadRuntimeProjection {
  state: TiaLadRuntimeState
  quality: TiaLadRuntimeQuality
  value: unknown
  qualityCode: string
  sourceTimestamp: string | null
}

export interface TiaLadSymbolProjection {
  key: string
  uId: number
  kind: TiaLadSymbolKind
  partName: string
  label: string
  scope: string
  symbolPath: string[]
  constantValue: string
  negated: boolean
  attributes: Array<{ name: string, value: string }>
  templateValues: Array<{ name: string, value: string }>
  pins: string[]
  operands: TiaLadOperandProjection[]
  runtime: TiaLadRuntimeProjection
}

export interface TiaLadOperandProjection {
  key: string
  uId: number
  targetPin: string
  label: string
  scope: string
  symbolPath: string[]
  constantValue: string
  runtime: TiaLadRuntimeProjection
}

export interface TiaLadWireProjection {
  key: string
  uId: number
  endpoints: LineUpGraphLogicEndpoint[]
}

export interface TiaLadProjection {
  key: string
  title: string
  programmingLanguage: string
  symbols: TiaLadSymbolProjection[]
  wires: TiaLadWireProjection[]
  accessFallbacks: TiaLadOperandProjection[]
}

export interface TiaLadWireGeometry {
  key: string
  uId: number
  points: string
  /** 能流状态：'true'=绿实线 / 'false'=蓝虚线 / 'unknown'=灰实线；null=离线无监控数据（黑色） */
  state: 'true' | 'false' | 'unknown' | null
}

export interface TiaLadEndpointMarker {
  key: string
  kind: 'open' | 'unknown'
  x: number
  y: number
  label: string
}

export interface TiaLadGeometry {
  hasPowerrail: boolean
  /** 右母线 x 坐标（博图 LAD：左火线 + 右零线，梯级终于右母线） */
  rightRailX: number
  wires: TiaLadWireGeometry[]
  markers: TiaLadEndpointMarker[]
}

const partKinds: Record<string, TiaLadSymbolKind> = {
  Contact: 'contact',
  Eq: 'eq',
  Ge: 'ge',
  Gt: 'gt',
  Le: 'le',
  Lt: 'lt',
  Ne: 'ne',
  Not: 'not',
  O: 'or',
  IlCoil: 'il-coil',
  SvCoil: 'sv-coil',
  TrCoil: 'tr-coil',
}

// 博图 FBD 盒内文字（IEC 61131）：OR 盒是 >=1，比较盒是 ==/>/>=/<=/<。
// 线圈（TrCoil/IlCoil/SvCoil）在博图里就是普通线圈 ()，内部不写字。
const partLabels: Partial<Record<TiaLadSymbolKind, string>> = {
  'eq': '==',
  'ge': '>=',
  'gt': '>',
  'ne': '<>',
  'le': '<=',
  'lt': '<',
  'not': 'NOT',
  'or': '>=1',
}

function normalizeQuality(quality: LineUpRuntimeQuality): TiaLadRuntimeQuality {
  return quality
}

function runtimeProjection(values: LineUpConditionRuntimeValue[]): TiaLadRuntimeProjection {
  if (values.length !== 1) {
    return { state: 'unknown', quality: 'Unknown', value: null, qualityCode: values.length > 1 ? 'AmbiguousRuntimeValue' : '', sourceTimestamp: null }
  }
  const [value] = values
  const quality = normalizeQuality(value.quality)
  const state = value.state === 'True'
    ? 'true'
    : value.state === 'False'
      ? 'false'
      : 'unknown'
  return {
    state: quality === 'Good' ? state : 'unknown',
    quality,
    value: value.value,
    qualityCode: value.qualityCode,
    sourceTimestamp: value.sourceTimestamp,
  }
}

function exactRuntimeValues(
  network: LineUpGraphLogicNetwork,
  partSourceKey: string,
  values: LineUpConditionRuntimeValue[],
) {
  return values.filter(value => value.ownerSourceKey === network.ownerSourceKey
    && value.networkSourceKey === network.sourceKey
    && value.partSourceKey === partSourceKey)
}

function symbolLabel(
  kind: TiaLadSymbolKind,
  partName: string,
  symbolPath: string[],
  scope?: string,
  localSymbolMap?: Record<string, string>,
) {
  if (kind === 'unknown') {
    return partName
  }
  if (symbolPath.length > 0) {
    return resolveSymbolDisplay(symbolPath, scope, localSymbolMap)
  }
  return partLabels[kind] ?? partName
}

function accessLabel(
  part: LineUpGraphLogicNetwork['parts'][number],
  localSymbolMap?: Record<string, string>,
) {
  if (part.symbolPath.length > 0) {
    return resolveSymbolDisplay(part.symbolPath, part.scope, localSymbolMap)
  }
  return part.constantValue || `Access UID ${part.uId}`
}

/**
 * 局部变量显示名解析（2026-10-07）：LocalVariable 部件（如 GRAPH 转换条件里的
 * #S_AutoRunMode / #I_Mode.DryRunMode）命中具体化映射时，保留原局部名并在括号内
 * 附上实际对应的全局符号路径（与 IGW 采集点位名一致）：
 *   S_AutoRunMode（IDB150_OP1030_电芯上线扫码枪自动流程.S_AutoRunMode）
 * 其余（全局变量/常量/未命中映射）按原样显示。
 */
function resolveSymbolDisplay(
  symbolPath: string[],
  scope: string | undefined,
  localSymbolMap: Record<string, string> | undefined,
) {
  const joined = symbolPath.join('.')
  if (localSymbolMap && scope === 'LocalVariable' && localSymbolMap[joined]) {
    return `${joined}（${localSymbolMap[joined]}）`
  }
  return joined
}

export function projectLadNetwork(
  network: LineUpGraphLogicNetwork,
  runtimeValues: LineUpConditionRuntimeValue[],
  localSymbolMap?: Record<string, string>,
): TiaLadProjection {
  const accessByUid = new Map(network.parts
    .filter(part => part.partName === 'Access')
    .map(part => [part.uId, part]))
  const functionalPartsByUid = new Map(network.parts
    .filter(part => part.partName !== 'Access')
    .map(part => [part.uId, part]))
  const operandsByTargetUid = new Map<number, TiaLadOperandProjection[]>()
  const attachedAccessKeys = new Set<string>()
  const accessWireKeys = new Set<string>()

  for (const wire of network.wires) {
    const accesses = wire.endpoints
      .filter(endpoint => endpoint.kind === 'IdentCon' && endpoint.uId !== null)
      .map(endpoint => accessByUid.get(endpoint.uId!))
      .filter(part => part !== undefined)
    const targets = wire.endpoints.filter(endpoint => endpoint.kind === 'NameCon'
      && endpoint.uId !== null
      && functionalPartsByUid.has(endpoint.uId))
    if (accesses.length === 0 || targets.length === 0) {
      continue
    }
    accessWireKeys.add(wire.sourceKey)
    for (const access of accesses) {
      attachedAccessKeys.add(access.sourceKey)
      for (const target of targets) {
        const operands = operandsByTargetUid.get(target.uId!) ?? []
        operands.push({
          key: access.sourceKey,
          uId: access.uId,
          targetPin: target.name,
          label: accessLabel(access, localSymbolMap),
          scope: access.scope,
          symbolPath: [...access.symbolPath],
          constantValue: access.constantValue,
          runtime: runtimeProjection(exactRuntimeValues(network, access.sourceKey, runtimeValues)),
        })
        operandsByTargetUid.set(target.uId!, operands)
      }
    }
  }

  const functionalParts = network.parts.filter(part => part.partName !== 'Access')
  return {
    key: network.sourceKey,
    title: network.title,
    programmingLanguage: network.programmingLanguage,
    symbols: functionalParts.map((part) => {
      const kind = partKinds[part.partName] ?? 'unknown'
      const matchingRuntimeValues = exactRuntimeValues(network, part.sourceKey, runtimeValues)
      const operands = operandsByTargetUid.get(part.uId) ?? []
      return {
        key: part.sourceKey,
        uId: part.uId,
        kind,
        partName: part.partName,
        label: kind === 'contact' && operands.length > 0
          ? operands[0].label
          : symbolLabel(kind, part.partName, part.symbolPath, part.scope, localSymbolMap),
        scope: part.scope,
        symbolPath: [...part.symbolPath],
        constantValue: part.constantValue,
        negated: part.negated,
        attributes: part.attributes.map(attribute => ({ ...attribute })),
        templateValues: part.templateValues.map(templateValue => ({ ...templateValue })),
        pins: [...part.pinNames],
        operands,
        runtime: runtimeProjection(matchingRuntimeValues),
      }
    }),
    wires: network.wires.filter(wire => !accessWireKeys.has(wire.sourceKey)).map(wire => ({
      key: wire.sourceKey,
      uId: wire.uId,
      endpoints: wire.endpoints.map(endpoint => ({ ...endpoint })),
    })),
    accessFallbacks: network.parts
      .filter(part => part.partName === 'Access' && !attachedAccessKeys.has(part.sourceKey))
      .map(part => ({
        key: part.sourceKey,
        uId: part.uId,
        targetPin: '',
        label: accessLabel(part, localSymbolMap),
        scope: part.scope,
        symbolPath: [...part.symbolPath],
        constantValue: part.constantValue,
        runtime: runtimeProjection(exactRuntimeValues(network, part.sourceKey, runtimeValues)),
      })),
  }
}

// ---------------------------------------------------------------------------
// 博图 LAD 视图语义：Or(>=1) 是 FBD 画法；同一网络切到 LAD 视图时，
// 博图把它画成并联支路——几路条件上下并排、汇合后进完结节点（线圈）。
// expandLadOrGates 把每个 Or 盒"收缩"掉：每路输入驱动 × 每路输出负载直连，
// 级联 Or 迭代收缩，布局引擎随后自然把支路上下堆叠并合流。
// ---------------------------------------------------------------------------

export function expandLadOrGates(projection: TiaLadProjection): TiaLadProjection {
  if (!projection.symbols.some(symbol => symbol.kind === 'or')) {
    return projection
  }
  let symbols = [...projection.symbols]
  let wires = [...projection.wires]
  let guard = 0
  while (guard++ < 128) {
    const orSymbol = symbols.find(symbol => symbol.kind === 'or')
    if (!orSymbol) {
      break
    }
    const uid = orSymbol.uId
    const touching = wires.filter(wire => wire.endpoints.some(
      endpoint => endpoint.kind === 'NameCon' && endpoint.uId === uid,
    ))
    const drivers: LineUpGraphLogicEndpoint[] = []
    const sinks: LineUpGraphLogicEndpoint[] = []
    for (const wire of touching) {
      for (const endpoint of wire.endpoints) {
        if (endpoint.kind === 'Powerrail') {
          drivers.push(endpoint)
          continue
        }
        if (endpoint.kind !== 'NameCon' || endpoint.uId === uid) {
          continue
        }
        if (isOutputPin(endpoint.name)) {
          drivers.push(endpoint)
        }
        else {
          sinks.push(endpoint)
        }
      }
    }
    wires = wires.filter(wire => !touching.includes(wire))
    const seen = new Set(wires.map(wire => wire.endpoints.map(endpoint => endpoint.kind + ':' + endpoint.uId + ':' + endpoint.name).join('->')))
    for (const driver of drivers) {
      for (const sink of sinks) {
        const signature = driver.kind + ':' + driver.uId + ':' + driver.name
          + '->' + sink.kind + ':' + sink.uId + ':' + sink.name
        if (seen.has(signature)) {
          continue
        }
        seen.add(signature)
        wires.push({
          key: 'or:' + uid + ':' + signature,
          uId: uid,
          endpoints: [driver, sink],
        })
      }
    }
    symbols = symbols.filter(symbol => symbol.key !== orSymbol.key)
  }
  return { ...projection, symbols, wires }
}
// ---------------------------------------------------------------------------
// 纯文本布尔表达式（与 LAD 图完全等价）：并联分支 = OR，串联 = AND，
// 常闭触点 = NOT(...)，Not 部件 = NOT(...)，比较器 = "左 操作符 右"。
// ---------------------------------------------------------------------------

export function tiaLadProjectionExpression(projection: TiaLadProjection): string | null {
  const expanded = expandLadOrGates(projection)
  const symbolByUid = new Map(expanded.symbols.map(symbol => [symbol.uId, symbol]))
  const symbolByKey = new Map(expanded.symbols.map(symbol => [symbol.key, symbol]))
  const driversByPin = new Map<string, LadPinRef[]>()
  const hasDriverSource = new Set<string>()
  for (const wire of expanded.wires) {
    for (const connection of connectionsForWire(wire, symbolByUid)) {
      if (!connection.from) {
        continue
      }
      const pinKey = connection.to.symbolKey + ':' + connection.to.pin
      const drivers = driversByPin.get(pinKey) ?? []
      drivers.push(connection.from)
      driversByPin.set(pinKey, drivers)
      hasDriverSource.add(connection.from.symbolKey)
    }
  }

  const visiting = new Set<string>()
  function exprForPin(symbolKey: string, pin: string): string | null {
    const parts = (driversByPin.get(symbolKey + ':' + pin) ?? [])
      .map(driver => exprForSymbol(driver.symbolKey))
      .filter((expr): expr is string => expr !== null)
    if (parts.length === 0) {
      return null
    }
    return parts.length === 1 ? parts[0] : '(' + parts.join(' OR ') + ')'
  }

  function exprForSymbol(symbolKey: string): string | null {
    if (visiting.has(symbolKey)) {
      return null
    }
    visiting.add(symbolKey)
    try {
      const symbol = symbolByKey.get(symbolKey)
      if (!symbol) {
        return null
      }
      if (symbol.kind === 'not') {
        const inPin = symbol.pins.find(pin => !isOutputPin(pin)) ?? 'in'
        const inner = exprForPin(symbol.key, inPin)
        return inner ? 'NOT(' + inner + ')' : 'NOT'
      }
      const inputPin = ladEnergyInputPin(symbol)
      const upstream = inputPin ? exprForPin(symbol.key, inputPin) : null
      const local = localExpression(symbol)
      if (local === null) {
        return upstream
      }
      if (upstream === null) {
        return local
      }
      return '(' + upstream + ' AND ' + local + ')'
    }
    finally {
      visiting.delete(symbolKey)
    }
  }

  function localExpression(symbol: TiaLadSymbolProjection): string | null {
    if (isLadComparatorKind(symbol.kind)) {
      return tiaLadComparisonExpression(symbol)
    }
    if (symbol.kind === 'contact') {
      const operand = symbol.operands.find(item => item.targetPin === 'operand') ?? symbol.operands[0]
      const label = operand?.label ?? symbol.label
      return symbol.negated ? 'NOT(' + label + ')' : label
    }
    if (symbol.kind.endsWith('-coil')) {
      return null
    }
    return symbol.label
  }

  const coil = expanded.symbols.find(symbol => symbol.kind.endsWith('-coil'))
  if (coil) {
    return exprForPin(coil.key, 'in')
  }
  // 无线圈网络：取最末端（没有驱动过别人）的部件表达式
  const terminals = expanded.symbols.filter(symbol => !hasDriverSource.has(symbol.key))
  for (const terminal of terminals) {
    const expr = exprForSymbol(terminal.key)
    if (expr) {
      return expr
    }
  }
  return null
}

export function tiaLadNetworkExpression(
  network: LineUpGraphLogicNetwork,
  runtimeValues: LineUpConditionRuntimeValue[],
  localSymbolMap?: Record<string, string>,
): string | null {
  return tiaLadProjectionExpression(projectLadNetwork(network, runtimeValues, localSymbolMap))
}
// ---------------------------------------------------------------------------
// 博图式梯形图布局：横向按信号流推进（母线 → 触点/部件 → 线圈），
// 并联分支在同一列纵向堆叠并整体居中，连线用正交折线（横-竖-横）。
// ---------------------------------------------------------------------------

export interface TiaLadLayoutPosition {
  x: number
  y: number
}

export interface TiaLadLayout {
  /** symbol.key -> symbol 左上角坐标 */
  positions: Map<string, TiaLadLayoutPosition>
  columnCount: number
  rowCount: number
  width: number
  height: number
  /** 每列的实际宽度（列宽自适应：按列内最长文字加宽，下限 154 / 上限 280） */
  columnWidths: number[]
  /** 每列居中标签（触点/比较器/无操作数盒）的每行字符预算：按本列与相邻列宽的最小值折算，
   *  宽列的长折行文字不越过窄邻列元件 —— 左右文字净空由此保证（2026-09 二次修复） */
  columnMaxChars: number[]
  /** 盒式部件左侧引脚标签的每行字符预算（走线走廊更窄，预算更紧） */
  columnPinMaxChars: number[]
  /** symbol.key -> 所在列号（前端按列预算决定折行宽度） */
  symbolColumns: Map<string, number>
}

export const LAD_POWERRAIL_X = 28
export const LAD_MARGIN_LEFT = 78
export const LAD_MARGIN_TOP = 40
export const LAD_COLUMN_WIDTH = 154
export const LAD_ROW_HEIGHT = 96
const LAD_SYMBOL_RIGHT = 112
const LAD_CONTACT_PIN_Y = 26
const LAD_COIL_PIN_Y = 26
const LAD_PIN_PITCH = 14
const LAD_PIN_OFFSET = 12
/** 11px 加粗标签的单字符宽度估算（ASCII/CJK）——列宽自适应与折行预算共用同一套 */
const LAD_CHAR_WIDTH_ASCII = 6.9
const LAD_CHAR_WIDTH_CJK = 11.5
/** 相邻列文字之间的最小净空（列宽自适应加宽量与文字预算扣除量共用） */
const LAD_TEXT_GAP = 44
/** 盒式引脚标签向左可占的走线走廊净空（走廊=本列与左邻列之间的空隙，比居中标签更紧） */
const LAD_PIN_GAP = 72
/** 行内容（盒底/常量行）与下一行向上堆叠文字之间的净空 */
const LAD_ROW_GAP = 12
/** 向上堆叠文字的顶部高度：首行基线 -9 + 字形上伸约 9 + 余量 */
const LAD_TEXT_ASCENT = 20

const comparisonOperators: Record<string, string> = {
  eq: '==',
  ge: '>=',
  gt: '>',
  le: '<=',
  lt: '<',
  ne: '<>',
}

/** 比较器（Eq/Ge/Gt/Le/Lt）不是盒，是一个内联表达式 */
export function isLadComparatorKind(kind: string): boolean {
  return Object.prototype.hasOwnProperty.call(comparisonOperators, kind)
}

/** 按引脚顺序拼比较表达式：in1 操作符 in2（如 RT_DATA.MOP.AUTO > 5） */
export function tiaLadComparisonExpression(symbol: TiaLadSymbolProjection): string {
  const op = comparisonOperators[symbol.kind]
  if (!op) {
    return symbol.label
  }
  const pinOrder = ['in1', 'in2', 'in', 'operand']
  const ordered = [...symbol.operands].sort((a, b) => {
    const ai = pinOrder.indexOf(a.targetPin)
    const bi = pinOrder.indexOf(b.targetPin)
    return (ai === -1 ? 9 : ai) - (bi === -1 ? 9 : bi)
  })
  const left = ordered[0]?.label
  const right = ordered[1]?.label
  if (left !== undefined && right !== undefined) {
    return left + ' ' + op + ' ' + right
  }
  return left ?? symbol.label
}

/**
 * 长文本换行（博图也在触点上方对长操作数名折行）：优先在空格/点/下划线处断开。
 * 不截断不加省略号 —— 内容全部展示，需要多少行就折多少行（悬浮 title 另有全文）。
 */
export function wrapLadLabelLines(text: string, maxChars = 24): string[] {
  if (text.length <= maxChars) {
    return [text]
  }
  const lines: string[] = []
  let rest = text
  while (rest.length > maxChars) {
    const window = rest.slice(0, maxChars + 1)
    let cut = Math.max(window.lastIndexOf(' '), window.lastIndexOf('.'), window.lastIndexOf('_'))
    if (cut < 6) {
      cut = maxChars
    }
    lines.push(rest.slice(0, cut + 1).trimEnd())
    rest = rest.slice(cut + 1).trimStart()
  }
  if (rest.length > 0) {
    lines.push(rest)
  }
  return lines
}

/** 估算标签渲染宽度（11px 加粗字体：ASCII ≈6.9px/字符，CJK ≈11.5px/字符） */
export function ladLabelTextWidth(text: string): number {
  let width = 0
  for (const char of text) {
    width += char.charCodeAt(0) > 0x2e7f ? LAD_CHAR_WIDTH_CJK : LAD_CHAR_WIDTH_ASCII
  }
  return width
}

/**
 * 每行可容纳的字符数（按列宽折算的兜底公式）：
 * 列宽 - 40px 保底间隙，相邻列的文字永远不贴在一起。布局与前端折行共用同一公式；
 * 布局产物的 columnMaxChars / columnPinMaxChars 进一步按相邻列宽收紧。
 */
export function ladColumnMaxChars(columnWidth: number): number {
  return Math.max(20, Math.floor((columnWidth - 40) / LAD_CHAR_WIDTH_ASCII))
}

function ladSymbolLabelText(symbol: TiaLadSymbolProjection): string {
  if (isLadComparatorKind(symbol.kind)) {
    return tiaLadComparisonExpression(symbol)
  }
  if (symbol.kind === 'contact') {
    const operand = symbol.operands.find(item => item.targetPin === 'operand') ?? symbol.operands[0]
    if (operand) {
      const suffix = operand.runtime.quality === 'Good' ? ' [True]' : ''
      return operand.label + suffix
    }
  }
  return symbol.label
}

function ladSymbolLabelWidth(symbol: TiaLadSymbolProjection): number {
  return ladLabelTextWidth(ladSymbolLabelText(symbol))
}

/**
 * 部件的"能流"输入引脚（区别于操作数引脚）：
 * 比较器（Eq/Ge/Gt/Le/Lt）的能流走 'pre'（使能），操作数走 in1/in2；
 * 触点/Not 走 'in'，操作数走 'operand'。优先级：pre > in > 其它非操作数输入。
 */
export function ladEnergyInputPin(symbol: TiaLadSymbolProjection): string | null {
  const pins = symbol.pins
  return pins.find(pin => pin === 'pre')
    ?? pins.find(pin => pin === 'in')
    ?? pins.find(pin => !isOutputPin(pin) && pin !== 'operand')
    ?? null
}

export function tiaLadSymbolHeight(symbol: TiaLadSymbolProjection, maxChars: number = ladColumnMaxChars(LAD_COLUMN_WIDTH)) {
  if (isLadComparatorKind(symbol.kind)) {
    // 比较器与触点同高（内联表达式形态，不占盒高度）
    return 54
  }
  if (symbol.pins.length <= 3) {
    return 54
  }
  // 多引脚盒：高度必须包住最后一根引脚（旧实现固定 76，第 5 根起引脚被钳出盒外）
  const pinBottom = Math.max(...ladPinLayout(symbol, maxChars).pinY.values())
  return Math.max(76, pinBottom + 14)
}

/**
 * 监控态附加行数：触点/比较器/无操作数盒在标签折行之上还会堆叠
 * 判断行（>True/False）与 值状态行。行距计算必须与前端渲染计入相同行数，
 * 否则监控态多行标签的顶部文字会压进上一行元件（2026-09 文字重合修复）。
 */
export function ladRuntimeExtraLines(symbol: TiaLadSymbolProjection): number {
  if (symbol.runtime.quality === 'Unknown' || symbol.kind.endsWith('-coil')) {
    return 0
  }
  if (isLadComparatorKind(symbol.kind)) {
    return symbol.operands.length > 0 ? 2 : 1
  }
  if (symbol.kind === 'contact') {
    const operand = symbol.operands.find(item => item.targetPin === 'operand') ?? symbol.operands[0]
    return operand && operand.runtime.quality !== 'Unknown' ? 2 : 1
  }
  // 盒式部件：只有无操作数（symbol-label 分支）才画判断行
  return symbol.operands.length === 0 ? 1 : 0
}

/**
 * 盒式部件引脚纵排几何：每根引脚的操作数标签按列宽折行，
 * 上一根标签占几行就把下一根引脚往下推几格 —— 替代旧 min(pinIndex,3) 钳制
 * （旧实现第 5 根及以后的引脚标签全部落在同一 y=54，完全重叠）。
 */
export interface TiaLadPinLayout {
  /** 引脚 -> 操作数标签折行（无操作数的引脚为空数组） */
  lines: Map<string, string[]>
  /** 引脚 -> 标签末行基线 y（相对盒左上角；导线接入点与标签共用同一 y） */
  pinY: Map<string, number>
}

const LAD_OPERAND_LINE_PITCH = 12

export function ladPinLayout(symbol: TiaLadSymbolProjection, maxChars: number): TiaLadPinLayout {
  const lines = new Map<string, string[]>()
  const pinY = new Map<string, number>()
  let cursorY = LAD_PIN_OFFSET
  symbol.pins.forEach((pin, index) => {
    if (index > 0) {
      const previousLines = lines.get(symbol.pins[index - 1])?.length ?? 0
      cursorY += Math.max(LAD_PIN_PITCH, previousLines * LAD_OPERAND_LINE_PITCH + 2)
    }
    pinY.set(pin, cursorY)
    const operand = symbol.operands.find(item => item.targetPin === pin)
    lines.set(pin, operand ? wrapLadLabelLines(operand.label, maxChars) : [])
  })
  return { lines, pinY }
}

/**
 * 部件内部文字（博图风格）：触点不写字（形状即语义）；比较/逻辑盒写运算符
 * （==、>=、>、<、<=、NOT、OR）；特殊线圈写 TR/IL/SV；未知部件保留原始名。
 * 引脚名（in/out/operand…）不渲染 —— 线段上只画线不写字。
 */
export function tiaLadPartGlyph(symbol: TiaLadSymbolProjection): string | null {
  if (symbol.kind === 'contact' || symbol.kind.endsWith('-coil')) {
    return null
  }
  if (symbol.kind === 'unknown') {
    return symbol.partName
  }
  return partLabels[symbol.kind] ?? symbol.partName
}

interface LadPinRef {
  symbolKey: string
  pin: string
  output: boolean
}

interface LadConnection {
  from: LadPinRef | null
  to: LadPinRef
}

function isOutputPin(pin: string) {
  return pin.toLowerCase().includes('out')
}

function wirePinRefs(
  wire: TiaLadWireProjection,
  symbolByUid: Map<number, TiaLadSymbolProjection>,
) {
  const pins: LadPinRef[] = []
  const dangling: Array<{ endpoint: LineUpGraphLogicEndpoint, index: number }> = []
  let hasPowerrail = false
  wire.endpoints.forEach((endpoint, index) => {
    if (endpoint.kind === 'Powerrail') {
      hasPowerrail = true
      return
    }
    if (endpoint.kind === 'OpenCon' || endpoint.uId === null) {
      dangling.push({ endpoint, index })
      return
    }
    const symbol = symbolByUid.get(endpoint.uId)
    if (!symbol) {
      dangling.push({ endpoint, index })
      return
    }
    pins.push({
      symbolKey: symbol.key,
      pin: endpoint.name,
      output: isOutputPin(endpoint.name),
    })
  })
  return { pins, dangling, hasPowerrail }
}

function connectionsForWire(
  wire: TiaLadWireProjection,
  symbolByUid: Map<number, TiaLadSymbolProjection>,
): LadConnection[] {
  const { pins, hasPowerrail } = wirePinRefs(wire, symbolByUid)
  const connections: LadConnection[] = []
  if (hasPowerrail) {
    for (const pin of pins) {
      connections.push({ from: null, to: pin })
    }
    return connections
  }
  const sources = pins.filter(pin => pin.output)
  const targets = pins.filter(pin => !pin.output)
  if (sources.length > 0 && targets.length > 0) {
    for (const from of sources) {
      for (const to of targets) {
        connections.push({ from, to })
      }
    }
    return connections
  }
  for (let index = 1; index < pins.length; index++) {
    connections.push({ from: pins[index - 1], to: pins[index] })
  }
  return connections
}

/**
 * 博图式梯级布局（串并联树分解）：
 * - 串联链：元件从左到右排同一梯级线；
 * - 并联组：每条支路占一整条连续横带（按线圈入线的编写顺序上下排列），
 *   支路长短不一时短支路水平并入汇合点，支路内元件绝不交错；
 * - 梯级主线（并联组前后的串联元件）垂直居中于并联组 —— 与 TIA 画法一致。
 */
export function layoutLadNetwork(projection: TiaLadProjection): TiaLadLayout {
  const positions = new Map<string, TiaLadLayoutPosition>()
  const symbols = projection.symbols
  if (symbols.length === 0) {
    return {
      positions,
      columnCount: 0,
      rowCount: 0,
      width: 360,
      height: 0,
      columnWidths: [],
      columnMaxChars: [],
      columnPinMaxChars: [],
      symbolColumns: new Map<string, number>(),
    }
  }

  const symbolByUid = new Map(symbols.map(symbol => [symbol.uId, symbol]))
  const symbolByKey = new Map(symbols.map(symbol => [symbol.key, symbol]))
  // pin -> 驱动者列表（保持导线编写顺序，即博图支路的上下顺序）
  const driversByPin = new Map<string, string[]>()
  for (const wire of projection.wires) {
    for (const connection of connectionsForWire(wire, symbolByUid)) {
      if (!connection.from || !symbolByKey.has(connection.from.symbolKey) || !symbolByKey.has(connection.to.symbolKey)) {
        continue
      }
      const pinKey = connection.to.symbolKey + ':' + connection.to.pin
      const drivers = driversByPin.get(pinKey) ?? []
      drivers.push(connection.from.symbolKey)
      driversByPin.set(pinKey, drivers)
    }
  }

  // ---- 串并联树分解：从线圈往上游递归（多驱动 = 并联，前后元件 = 串联）----
  type LadRungNode =
    | { kind: 'element', key: string }
    | { kind: 'series', items: LadRungNode[] }
    | { kind: 'parallel', branches: LadRungNode[] }

  const visiting = new Set<string>()
  function inputPinOf(symbol: TiaLadSymbolProjection): string | null {
    return ladEnergyInputPin(symbol)
  }
  // 喂入 (key, pin) 的上游树；多个驱动 = 并联组（保持导线编写顺序）
  function upstreamTree(key: string, pin: string): LadRungNode | null {
    const drivers = driversByPin.get(key + ':' + pin) ?? []
    const branches: LadRungNode[] = []
    for (const driver of drivers) {
      const tree = outputTree(driver)
      if (tree) {
        branches.push(tree)
      }
    }
    if (branches.length === 0) {
      return null
    }
    return branches.length === 1 ? branches[0] : { kind: 'parallel', branches }
  }
  // 信号经过 key 元件后的整棵子树 = 上游 + 本元件（串联）
  function outputTree(key: string): LadRungNode | null {
    if (visiting.has(key)) {
      return null // 环保护
    }
    visiting.add(key)
    try {
      const symbol = symbolByKey.get(key)
      if (!symbol) {
        return null
      }
      const element: LadRungNode = { kind: 'element', key }
      const inputPin = inputPinOf(symbol)
      const upstream = inputPin !== null ? upstreamTree(key, inputPin) : null
      if (!upstream) {
        return element
      }
      return { kind: 'series', items: [upstream, element] }
    }
    finally {
      visiting.delete(key)
    }
  }

  // 每个线圈一条梯级（线圈是终点元件，串在梯级末尾）；无线圈时取最末端部件
  const coils = symbols.filter(symbol => symbol.kind.endsWith('-coil')).sort((left, right) => left.uId - right.uId)
  const rungs: LadRungNode[] = []
  for (const coil of coils) {
    const upstream = upstreamTree(coil.key, 'in')
    rungs.push(upstream
      ? { kind: 'series', items: [upstream, { kind: 'element', key: coil.key }] }
      : { kind: 'element', key: coil.key })
  }
  if (coils.length === 0) {
    const drivenSomewhere = new Set([...driversByPin.keys()].map(pinKey => pinKey.split(':')[0]))
    const terminals = symbols.filter(symbol => !drivenSomewhere.has(symbol.key)).sort((left, right) => left.uId - right.uId)
    for (const terminal of terminals) {
      rungs.push(outputTree(terminal.key) ?? { kind: 'element', key: terminal.key })
    }
  }

  // 公共前缀提取：Or 展开后各并联支路常共享相同的上游链（如公共条件一分多路），
  // 把公共前缀提到并联组之前 —— 与 TIA 画法一致（公共链画一次、垂直居中于支路组）。
  function rungSignature(node: LadRungNode): string {
    if (node.kind === 'element') {
      return node.key
    }
    if (node.kind === 'series') {
      return '(' + node.items.map(rungSignature).join('&') + ')'
    }
    return '(' + node.branches.map(rungSignature).join('|') + ')'
  }
  function factorRung(node: LadRungNode): LadRungNode {
    if (node.kind === 'element') {
      return node
    }
    if (node.kind === 'series') {
      const items: LadRungNode[] = []
      for (const item of node.items) {
        const factored = factorRung(item)
        if (factored.kind === 'series') {
          items.push(...factored.items)
        } else {
          items.push(factored)
        }
      }
      return { kind: 'series', items }
    }
    const branches = node.branches.map(factorRung)
    const seriesBranches = branches.filter(
      (branch): branch is Extract<LadRungNode, { kind: 'series' }> => branch.kind === 'series' && branch.items.length >= 2,
    )
    if (branches.length > 1 && seriesBranches.length === branches.length) {
      const prefixSignature = rungSignature(seriesBranches[0].items[0])
      if (seriesBranches.every(branch => rungSignature(branch.items[0]) === prefixSignature)) {
        const prefix = seriesBranches[0].items[0]
        const rests = seriesBranches.map(branch =>
          branch.items.length === 2 ? branch.items[1] : ({ kind: 'series', items: branch.items.slice(1) } as LadRungNode))
        const merged: LadRungNode = rests.length === 1 ? rests[0] : { kind: 'parallel', branches: rests }
        return { kind: 'series', items: [prefix, factorRung(merged)] }
      }
    }
    return { kind: 'parallel', branches }
  }
  const factoredRungs = rungs.map(rung => factorRung(rung))

  // 尺寸（列/行数）与摆放：串联横向铺开 + 垂直居中；并联纵向叠支路带
  const colOf = new Map<string, number>()
  const rowOf = new Map<string, number>()
  function sizeOf(node: LadRungNode): { w: number, h: number } {
    if (node.kind === 'element') {
      return { w: 1, h: 1 }
    }
    if (node.kind === 'series') {
      let w = 0
      let h = 1
      for (const item of node.items) {
        const size = sizeOf(item)
        w += size.w
        h = Math.max(h, size.h)
      }
      return { w, h }
    }
    let w = 1
    let h = 0
    for (const branch of node.branches) {
      const size = sizeOf(branch)
      w = Math.max(w, size.w)
      h += size.h
    }
    return { w, h }
  }
  function place(node: LadRungNode, col0: number, row0: number) {
    if (node.kind === 'element') {
      if (!colOf.has(node.key)) {
        colOf.set(node.key, col0)
        rowOf.set(node.key, row0)
      }
      return
    }
    if (node.kind === 'series') {
      const total = sizeOf(node)
      let x = col0
      for (const item of node.items) {
        const size = sizeOf(item)
        place(item, x, row0 + (total.h - size.h) / 2)
        x += size.w
      }
      return
    }
    let y = row0
    for (const branch of node.branches) {
      const size = sizeOf(branch)
      place(branch, col0, y)
      y += size.h
    }
  }
  let rowCount = 0
  for (const rung of factoredRungs) {
    const size = sizeOf(rung)
    place(rung, 0, rowCount)
    rowCount += size.h
  }
  // 树未覆盖的孤件兜底：单独一行
  for (const symbol of symbols) {
    if (!colOf.has(symbol.key)) {
      colOf.set(symbol.key, 0)
      rowOf.set(symbol.key, rowCount)
      rowCount += 1
    }
  }
  let columnCount = 1
  for (const col of colOf.values()) {
    columnCount = Math.max(columnCount, col + 1)
  }
  if (rowCount === 0) {
    rowCount = 1
  }

  // 列宽自适应：按列内最长标签（触点操作数 / 比较表达式 / 部件名）估算宽度加宽列距，
  // +LAD_TEXT_GAP 的余量保证相邻列标签之间的视觉间隙，长文字不贴在一起、不压导线；
  // 下限保持标准列距 154，上限 280（更长的标签交给折行预算处理）。
  const widestPerColumn = new Map<number, number>()
  for (const symbol of symbols) {
    const column = colOf.get(symbol.key) ?? 0
    widestPerColumn.set(column, Math.max(widestPerColumn.get(column) ?? 0, ladSymbolLabelWidth(symbol)))
  }
  const columnWidths: number[] = []
  for (let column = 0; column < columnCount; column++) {
    columnWidths.push(Math.min(280, Math.max(LAD_COLUMN_WIDTH, Math.ceil(widestPerColumn.get(column) ?? 0) + LAD_TEXT_GAP)))
  }
  // 每列文字预算（2026-09 二次修复）：旧实现只按本列宽折行，宽列的长折行文字会向左
  // 探过列界、压到左侧窄列（典型：第一个串行触点右侧间隔被第二个宽列的文字吃掉）。
  // 居中标签的像素预算 = 本列与左右相邻列宽的最小值 - 净空：宽列在窄邻列旁宁可多折
  // 一行，也不横向越界；第 0 列另受左母线墙约束（文字不越过母线左侧）。
  // 盒式引脚标签画在盒左侧、向左延伸，走廊更窄，用更紧的 LAD_PIN_GAP。
  const columnMaxChars: number[] = []
  const columnPinMaxChars: number[] = []
  for (let column = 0; column < columnCount; column++) {
    const neighborMin = Math.min(
      columnWidths[column],
      column > 0 ? columnWidths[column - 1] : Number.MAX_SAFE_INTEGER,
      column + 1 < columnCount ? columnWidths[column + 1] : Number.MAX_SAFE_INTEGER,
    )
    const centeredBudget = column === 0
      ? Math.min(neighborMin - LAD_TEXT_GAP, 2 * (LAD_MARGIN_LEFT + 55 - LAD_POWERRAIL_X - 8))
      : neighborMin - LAD_TEXT_GAP
    columnMaxChars.push(Math.max(12, Math.floor(centeredBudget / LAD_CHAR_WIDTH_ASCII)))
    const pinBudget = column === 0
      ? Math.min(neighborMin - LAD_PIN_GAP, LAD_MARGIN_LEFT - 20)
      : neighborMin - LAD_PIN_GAP
    columnPinMaxChars.push(Math.max(8, Math.floor(Math.max(60, pinBudget) / LAD_CHAR_WIDTH_ASCII)))
  }
  const columnStarts: number[] = []
  let cursorX = LAD_MARGIN_LEFT
  for (let column = 0; column < columnCount; column++) {
    columnStarts.push(cursorX)
    cursorX += columnWidths[column]
  }

  // 行距自适应（2026-09 二次修复：上下层间距按"下一行文字的向上堆叠高度"精确计算）。
  // 每行记录两组尺寸：
  //   down[row]：行内容向下的高度（符号盒高 + 常量行）；
  //   up[row]：行内触点/比较器标签向上堆叠的文字高度（首行基线 -9 + 上伸 9，每多一行 +12），
  //            含监控态附加的判断行/值状态行。
  // 行距必须满足 down[row] + 净空 + up[row+1] —— 下一行向上堆叠的文字顶进的是本行的竖带。
  // 旧公式用"本行行数"近似"下一行行数"，行数/盒高错位时短缺 6~54px → 上下层文字重合。
  // 主线元件可落在半行（0.5）上做垂直居中：其向上文字顶到上一带内容、其盒底压到下一带
  // 文字，两条半程约束各乘 2 补偿。线圈不画本体与文字，不参与占位。
  const rowUp = new Map<number, number>()
  const rowDown = new Map<number, number>()
  const fracUp = new Map<number, number>()
  const fracDown = new Map<number, number>()
  for (const symbol of symbols) {
    const row = rowOf.get(symbol.key)
    if (row === undefined || symbol.kind.endsWith('-coil')) {
      continue
    }
    const base = Math.floor(row)
    const column = colOf.get(symbol.key) ?? 0
    const centered = symbol.kind === 'contact' || isLadComparatorKind(symbol.kind) || symbol.operands.length === 0
    const maxChars = centered ? columnMaxChars[column] : columnPinMaxChars[column]
    let up = 0
    if (centered) {
      const lines = wrapLadLabelLines(ladSymbolLabelText(symbol), maxChars).length + ladRuntimeExtraLines(symbol)
      up = LAD_TEXT_ASCENT + LAD_OPERAND_LINE_PITCH * Math.max(0, lines - 1)
    }
    const down = tiaLadSymbolHeight(symbol, maxChars) + (symbol.constantValue ? 18 : 0)
    if (row - base >= 0.5) {
      fracUp.set(base, Math.max(fracUp.get(base) ?? 0, up))
      fracDown.set(base, Math.max(fracDown.get(base) ?? 0, down))
    }
    else {
      rowUp.set(base, Math.max(rowUp.get(base) ?? 0, up))
      rowDown.set(base, Math.max(rowDown.get(base) ?? 0, down))
    }
  }
  const rowPitches: number[] = []
  for (let row = 0; row < rowCount; row++) {
    const nextUp = row + 1 < rowCount ? rowUp.get(row + 1) ?? 0 : 0
    const bandDown = rowDown.get(row) ?? 0
    const fracTop = fracUp.get(row) ?? 0
    const fracBottom = fracDown.get(row) ?? 0
    rowPitches.push(Math.max(
      LAD_ROW_HEIGHT,
      bandDown + LAD_ROW_GAP + nextUp,
      fracTop > 0 ? 2 * (bandDown + 8 + fracTop) : 0,
      fracBottom > 0 ? 2 * (fracBottom + 8 + nextUp) : 0,
    ))
  }
  const rowTops: number[] = []
  // 首行顶边：向上堆叠的文字（含半行主线元件的文字）不得探出画布顶端
  let cursorY = Math.max(
    LAD_MARGIN_TOP,
    (rowUp.get(0) ?? 0) + 8,
    (fracUp.get(0) ?? 0) > 0 ? (fracUp.get(0) ?? 0) + 8 - rowPitches[0] / 2 : 0,
  )
  for (let row = 0; row < rowCount; row++) {
    rowTops.push(cursorY)
    cursorY += rowPitches[row]
  }

  const symbolColumns = new Map<string, number>()
  for (const symbol of symbols) {
    const column = colOf.get(symbol.key) ?? 0
    const row = rowOf.get(symbol.key) ?? 0
    symbolColumns.set(symbol.key, column)
    const base = Math.min(Math.floor(row), rowCount - 1)
    const frac = row - base
    positions.set(symbol.key, {
      x: columnStarts[column],
      y: (rowTops[base] ?? LAD_MARGIN_TOP) + frac * (rowPitches[base] ?? LAD_ROW_HEIGHT),
    })
  }

  return {
    positions,
    columnCount,
    rowCount,
    width: cursorX + 48,
    height: cursorY + 32,
    columnWidths,
    columnMaxChars,
    columnPinMaxChars,
    symbolColumns,
  }
}

interface ResolvedEndpointPosition {
  x: number
  y: number
}

function pinPoint(
  projection: TiaLadProjection,
  layout: TiaLadLayout,
  ref: LadPinRef,
): ResolvedEndpointPosition | null {
  const position = layout.positions.get(ref.symbolKey)
  const symbol = projection.symbols.find(item => item.key === ref.symbolKey)
  if (!position || !symbol) {
    return null
  }
  if (symbol.kind === 'contact' || symbol.kind === 'not' || isLadComparatorKind(symbol.kind)) {
    // 触点、|NOT| 与比较表达式都在水平中轴 y=26 贯穿，引脚落在左右两端
    return {
      x: position.x + (ref.output ? LAD_SYMBOL_RIGHT : 0),
      y: position.y + LAD_CONTACT_PIN_Y,
    }
  }
  if (symbol.kind.endsWith('-coil')) {
    // 博图线圈 ()：导线接到左弧的垂直中心
    return {
      x: position.x + (ref.output ? LAD_SYMBOL_RIGHT : 0),
      y: position.y + LAD_COIL_PIN_Y,
    }
  }
  // 盒式部件：导线接入 y 与操作数标签共用 ladPinLayout 的引脚位（不钳制第 5 根起引脚）；
  // 折行预算取本列的引脚走廊预算（columnPinMaxChars），与前端渲染共用同一组数字
  const pinMaxChars = layout.columnPinMaxChars[layout.symbolColumns.get(ref.symbolKey) ?? 0] ?? ladColumnMaxChars(LAD_COLUMN_WIDTH)
  const pinY = ladPinLayout(symbol, pinMaxChars).pinY.get(ref.pin) ?? LAD_PIN_OFFSET
  return {
    x: position.x + (ref.output ? LAD_SYMBOL_RIGHT : 0),
    y: position.y + pinY,
  }
}

function routePoints(source: ResolvedEndpointPosition, target: ResolvedEndpointPosition) {
  if (Math.abs(source.y - target.y) < 0.5) {
    return `${source.x},${source.y} ${target.x},${target.y}`
  }
  const midX = Math.max(source.x + 24, target.x - 26)
  return `${source.x},${source.y} ${midX},${source.y} ${midX},${target.y} ${target.x},${target.y}`
}

function endpointLabel(endpoint: LineUpGraphLogicEndpoint) {
  if (endpoint.kind === 'OpenCon') {
    return 'OpenCon'
  }
  return endpoint.uId === null ? endpoint.kind : `${endpoint.kind} · UID ${endpoint.uId}`
}

export function projectLadGeometry(
  projection: TiaLadProjection,
  layout: TiaLadLayout,
): TiaLadGeometry {
  const markers: TiaLadEndpointMarker[] = []
  const wires: TiaLadWireGeometry[] = []
  const symbolByUid = new Map(projection.symbols.map(symbol => [symbol.uId, symbol]))
  const symbolByKey = new Map(projection.symbols.map(symbol => [symbol.key, symbol]))
  // 博图在线监控：绿实线=能流满足 / 蓝虚线=未满足 / 灰实线=未知；
  // 整网无监控数据（导入预览/离线）时一律 null → 前端保持黑色（离线观感）
  const hasRuntime = projection.symbols.some(symbol => symbol.runtime.quality !== 'Unknown')
  const wireStateOf = (symbol: TiaLadSymbolProjection | undefined): 'true' | 'false' | 'unknown' | null => {
    if (!hasRuntime) {
      return null
    }
    if (!symbol || symbol.runtime.quality === 'Unknown') {
      return 'unknown'
    }
    return symbol.runtime.state
  }
  const railSourceState = hasRuntime ? 'true' as const : null
  const consumedOutputs = new Set<string>()
  for (const wire of projection.wires) {
    const connections = connectionsForWire(wire, symbolByUid)
    connections.forEach((connection, connectionIndex) => {
      const targetPoint = pinPoint(projection, layout, connection.to)
      if (!targetPoint) {
        return
      }
      let sourcePoint: ResolvedEndpointPosition | null = null
      if (connection.from) {
        sourcePoint = pinPoint(projection, layout, connection.from)
        if (connection.from.output) {
          consumedOutputs.add(connection.from.symbolKey + ':' + connection.from.pin)
        }
      }
      else {
        sourcePoint = { x: LAD_POWERRAIL_X, y: targetPoint.y }
      }
      if (!sourcePoint) {
        return
      }
      wires.push({
        key: connections.length > 1 ? `${wire.key}:${connectionIndex}` : wire.key,
        uId: wire.uId,
        points: routePoints(sourcePoint, targetPoint),
        state: connection.from
          ? wireStateOf(symbolByKey.get(connection.from.symbolKey))
          : railSourceState,
      })
    })

    const { pins, dangling } = wirePinRefs(wire, symbolByUid)
    if (dangling.length > 0) {
      const anchorRef = connections.length > 0 ? connections[0].to : (pins[0] ?? null)
      const anchorPoint = anchorRef ? pinPoint(projection, layout, anchorRef) : null
      dangling.forEach(({ endpoint, index }, occurrence) => {
        const anchorX = anchorPoint ? anchorPoint.x - 30 : LAD_MARGIN_LEFT - 30
        const anchorY = anchorPoint ? anchorPoint.y : LAD_MARGIN_TOP
        const markerY = anchorY + (occurrence - (dangling.length - 1) / 2) * 12
        markers.push({
          key: `${wire.key}:${index}`,
          kind: endpoint.kind === 'OpenCon' ? 'open' : 'unknown',
          x: anchorX,
          y: markerY,
          label: endpointLabel(endpoint),
        })
        if (anchorPoint) {
          wires.push({
            key: `${wire.key}:dangling:${index}`,
            uId: wire.uId,
            points: `${anchorX},${markerY} ${anchorPoint.x},${anchorPoint.y}`,
            state: hasRuntime ? 'unknown' : null,
          })
        }
      })
    }
  }
  // 右母线：博图 LAD 每个梯级始于左母线、终于右母线。线圈与未被消费的输出引脚
  // 一律水平接到右母线（能流方向严格从左向右，输出元件靠最右侧）
  const rightRailX = layout.width + 36
  for (const symbol of projection.symbols) {
    const position = layout.positions.get(symbol.key)
    if (!position) {
      continue
    }
    if (symbol.kind.endsWith('-coil')) {
      // 线圈不画本体（只是逻辑完结点）：从汇合进线点直接拉一条水平线到右母线
      wires.push({
        key: `rail:${symbol.key}`,
        uId: symbol.uId,
        points: position.x + ',' + (position.y + LAD_COIL_PIN_Y) + ' ' + rightRailX + ',' + (position.y + LAD_COIL_PIN_Y),
        state: wireStateOf(symbol),
      })
      continue
    }
    for (const pin of symbol.pins) {
      if (!isOutputPin(pin) || consumedOutputs.has(symbol.key + ':' + pin)) {
        continue
      }
      const railPoint = pinPoint(projection, layout, { symbolKey: symbol.key, pin, output: true })
      if (!railPoint) {
        continue
      }
      wires.push({
        key: `rail:${symbol.key}:${pin}`,
        uId: symbol.uId,
        points: railPoint.x + ',' + railPoint.y + ' ' + rightRailX + ',' + railPoint.y,
        state: wireStateOf(symbol),
      })
    }
  }
  return {
    hasPowerrail: projection.wires.some(wire => wire.endpoints.some(endpoint => endpoint.kind === 'Powerrail')),
    rightRailX,
    wires,
    markers,
  }
}
