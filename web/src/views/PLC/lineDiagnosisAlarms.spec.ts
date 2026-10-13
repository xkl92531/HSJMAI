import { describe, expect, it } from 'vitest'
import { alarmKindKey, alarmLocationText, alarmSignalsText, alarmTagType, formatAlarmSignal, totalActiveAlarms } from './lineDiagnosisAlarms'

describe('lineDiagnosisAlarms', () => {
  it('uses red for not-reached alarms and orange for condition warnings', () => {
    expect(alarmTagType('FwdError')).toBe('danger')
    expect(alarmTagType('BkdError')).toBe('danger')
    expect(alarmTagType('FwdLock')).toBe('warning')
    expect(alarmTagType('BkdLock')).toBe('warning')
    expect(alarmTagType('Timeout')).toBe('danger')
    expect(alarmTagType('PositionConflict')).toBe('danger')
  })

  it('maps kinds to i18n keys with a fallback', () => {
    expect(alarmKindKey('FwdError')).toBe('alarmKindFwdError')
    expect(alarmKindKey('BkdLock')).toBe('alarmKindBkdLock')
    expect(alarmKindKey('Timeout')).toBe('alarmKindTimeout')
    expect(alarmKindKey('PositionConflict')).toBe('alarmKindPositionConflict')
    expect(alarmKindKey('Other')).toBe('alarmKindOther')
  })

  it('formats signal values including unread ones', () => {
    expect(formatAlarmSignal({ name: 'YV_W', address: '%Q10.1', value: true })).toBe('YV_W(%Q10.1)=true')
    expect(formatAlarmSignal({ name: 'CYL_W', value: false })).toBe('CYL_W=false')
    expect(formatAlarmSignal({ name: 'X', value: null })).toBe('X=?')
  })

  it('uses the given label for unread signals', () => {
    expect(formatAlarmSignal({ name: 'X', value: null }, '未读到')).toBe('X=未读到')
    expect(formatAlarmSignal({ name: 'Y', value: true }, '未读到')).toBe('Y=true')
    expect(alarmSignalsText({
      alarmVariable: 'DB1.A.X',
      kind: 'FwdError',
      outputSignal: { name: 'YV_W', value: true },
      feedbacks: [{ name: 'CYL_W' }],
    }, '未读到')).toBe('YV_W=true，CYL_W=未读到')
  })

  it('joins output and feedback signals', () => {
    expect(alarmSignalsText({
      alarmVariable: 'DB1.A.X',
      kind: 'FwdError',
      outputSignal: { name: 'YV_W', value: true },
      feedbacks: [{ name: 'CYL_W', value: false }],
    })).toBe('YV_W=true，CYL_W=false')
    expect(alarmSignalsText({ alarmVariable: 'DB1.A.X', kind: 'FwdError' })).toBe('')
  })

  it('counts active alarms across stations', () => {
    expect(totalActiveAlarms([{ activeAlarms: [{}, {}] }, { activeAlarms: [] }, {}])).toBe(2)
    expect(totalActiveAlarms(undefined)).toBe(0)
  })

  it('maps HMI alarm kinds (expected state, latched bits)', () => {
    expect(alarmKindKey('ExpectedState')).toBe('alarmKindExpectedState')
    expect(alarmKindKey('Latched')).toBe('alarmKindLatched')
    expect(alarmTagType('ExpectedState')).toBe('danger')
    expect(alarmTagType('Latched')).toBe('danger')
  })

  it('writes the location without empty parts', () => {
    expect(alarmLocationText({ alarmVariable: 'A', kind: 'Timeout', valveInstance: 'Valve[4]', blockName: 'FB121', networkId: 17 }))
      .toBe('Valve[4] · FB121 N17')
    expect(alarmLocationText({ alarmVariable: 'A', kind: 'Latched', valveInstance: '', blockName: 'FB130_Sensor', networkId: 7 }))
      .toBe('FB130_Sensor N7')
    expect(alarmLocationText({ alarmVariable: 'A', kind: 'Latched', blockName: 'FB130_Sensor' })).toBe('FB130_Sensor')
  })
})
