import type { LineUpImageWorkspace } from '@/api/modules/LineUp/imageMarks'
import type { LineUpMasterItem } from '@/api/modules/LineUp/masterData'
import { describe, expect, it } from 'vitest'
import { buildImageMarkRoute, loadImageMarkRouteContext } from './imageMarkNavigation'

describe('bom image-mark navigation', () => {
  it('opens the editor with the exact current work-position identity', async () => {
    expect(buildImageMarkRoute('50')).toEqual({
      path: '/lineup/image-marks',
      query: { subStationId: '50' },
    })
  })

  it('loads all hierarchy selections even when the hierarchy catalog is unavailable', async () => {
    const workspace: LineUpImageWorkspace = {
      subStation: {
        id: '50',
        code: 'OP020-01',
        name: 'OP020 主工位',
        workStationId: '40',
        workStationCode: 'OP020',
        workStationName: '缓存上料工站',
        processId: '30',
        processCode: 'PROC-FEED',
        processName: '缓存上料工序',
        productionLineId: '20',
        productionLineCode: 'LINE-LOCAL',
        productionLineName: '本机演示产线',
      },
      images: [],
      boms: [],
      markers: [],
    }
    const items: Record<string, LineUpMasterItem[]> = {
      'ProductionLine': [{ id: '20', level: 'ProductionLine', parentId: null, code: 'LINE-LOCAL', name: '本机演示产线', isEnabled: true }],
      'Process:20': [{ id: '30', level: 'Process', parentId: '20', code: 'PROC-FEED', name: '缓存上料工序', isEnabled: true }],
      'WorkStation:30': [{ id: '40', level: 'WorkStation', parentId: '30', code: 'OP020', name: '缓存上料工站', isEnabled: true }],
      'SubStation:40': [{ id: '50', level: 'SubStation', parentId: '40', code: 'OP020-01', name: 'OP020 主工位', isEnabled: true }],
    }

    const gatewayWithUnavailableCatalog = {
      getStation: async () => workspace,
      queryMaster: async () => {
        throw new Error('hierarchy catalog unavailable')
      },
    }
    const context = await loadImageMarkRouteContext('50', gatewayWithUnavailableCatalog)
      .catch(error => ({ error: error instanceof Error ? error.message : String(error) }))

    expect(context).toEqual({
      workspace,
      selection: {
        lineId: '20',
        processId: '30',
        workStationId: '40',
        subStationId: '50',
      },
      lines: items.ProductionLine,
      processes: items['Process:20'],
      workStations: items['WorkStation:30'],
      subStations: items['SubStation:40'],
    })
  })
})
