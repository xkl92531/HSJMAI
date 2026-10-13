export interface ValveAlarmSignal {
  name: string
  address?: string | null
  value?: boolean | null
}

export interface ActiveValveAlarm {
  alarmVariable: string
  kind: string
  direction?: string
  text?: string | null
  valveInstance?: string
  blockName?: string
  networkId?: number | null
  outputSignal?: ValveAlarmSignal | null
  feedbacks?: ValveAlarmSignal[]
}

const kindKeys: Record<string, string> = {
  FwdError: 'alarmKindFwdError',
  BkdError: 'alarmKindBkdError',
  FwdLock: 'alarmKindFwdLock',
  BkdLock: 'alarmKindBkdLock',
  Timeout: 'alarmKindTimeout',
  PositionConflict: 'alarmKindPositionConflict',
  ExpectedState: 'alarmKindExpectedState',
  Latched: 'alarmKindLatched',
}

/** 报警清单（发布时生成）与阀门 FB 引脚提取里的「报警」种类：红色；条件不满足警告（FwdLock/BkdLock）：橙色 */
const dangerKinds = new Set(['FwdError', 'BkdError', 'Timeout', 'PositionConflict', 'ExpectedState', 'Latched'])

export function alarmTagType(kind: string): 'danger' | 'warning' {
  return dangerKinds.has(kind) ? 'danger' : 'warning'
}

export function alarmKindKey(kind: string): string {
  return kindKeys[kind] ?? 'alarmKindOther'
}

/** 位置：实例 · 块名 N网络号；锁存报警位没有实例，空的部分不写 */
export function alarmLocationText(alarm: ActiveValveAlarm): string {
  const head = [alarm.valveInstance, alarm.blockName].filter(part => !!part).join(' · ')
  return alarm.networkId ? `${head} N${alarm.networkId}` : head
}

export function formatAlarmSignal(signal: ValveAlarmSignal, unreadLabel = '?'): string {
  const address = signal.address ? `(${signal.address})` : ''
  const value = signal.value === true ? 'true' : signal.value === false ? 'false' : unreadLabel
  return `${signal.name}${address}=${value}`
}

export function alarmSignalsText(alarm: ActiveValveAlarm, unreadLabel = '?'): string {
  const signals = [alarm.outputSignal, ...(alarm.feedbacks ?? [])].filter((s): s is ValveAlarmSignal => !!s)
  return signals.map(signal => formatAlarmSignal(signal, unreadLabel)).join('，')
}

export function totalActiveAlarms(stations: Array<{ activeAlarms?: unknown[] }> | undefined): number {
  return (stations ?? []).reduce((sum, station) => sum + (station.activeAlarms?.length ?? 0), 0)
}
