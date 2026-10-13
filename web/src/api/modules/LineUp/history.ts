import type { EntityId } from './types'
import lineUpMonitorApi from './monitor'

export function getLineUpHistory(bindingId: EntityId, sequenceSourceKey: string, beforeEventId?: EntityId, limit = 50) {
  return lineUpMonitorApi.getEvents(bindingId, sequenceSourceKey, beforeEventId, limit)
}

export default { getEvents: getLineUpHistory }
