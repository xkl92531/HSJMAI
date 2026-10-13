import type { LineUpImageWorkspace } from '@/api/modules/LineUp/imageMarks'
import type { LineUpMasterItem } from '@/api/modules/LineUp/masterData'
import type { EntityId } from '@/api/modules/LineUp/types'

export interface ImageMarkNavigationGateway {
  getStation: (subStationId: EntityId) => Promise<LineUpImageWorkspace>
}

export function buildImageMarkRoute(subStationId: EntityId) {
  return {
    path: '/lineup/image-marks',
    query: { subStationId },
  }
}

export async function loadImageMarkRouteContext(
  subStationId: EntityId,
  gateway: ImageMarkNavigationGateway,
) {
  const workspace = await gateway.getStation(subStationId)
  const hierarchy = workspace.subStation
  const item = (
    id: EntityId,
    level: LineUpMasterItem['level'],
    parentId: EntityId | null,
    code: string | null,
    name: string | null,
  ): LineUpMasterItem => ({ id, level, parentId, code, name, isEnabled: true })
  const lines = [item(
    hierarchy.productionLineId,
    'ProductionLine',
    null,
    hierarchy.productionLineCode,
    hierarchy.productionLineName,
  )]
  const processes = [item(
    hierarchy.processId,
    'Process',
    hierarchy.productionLineId,
    hierarchy.processCode,
    hierarchy.processName,
  )]
  const workStations = [item(
    hierarchy.workStationId,
    'WorkStation',
    hierarchy.processId,
    hierarchy.workStationCode,
    hierarchy.workStationName,
  )]
  const subStations = [item(
    hierarchy.id,
    'SubStation',
    hierarchy.workStationId,
    hierarchy.code,
    hierarchy.name,
  )]

  return {
    workspace,
    selection: {
      lineId: hierarchy.productionLineId,
      processId: hierarchy.processId,
      workStationId: hierarchy.workStationId,
      subStationId: hierarchy.id,
    },
    lines,
    processes,
    workStations,
    subStations,
  }
}
