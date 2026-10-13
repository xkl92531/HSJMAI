import type { PlcGraphBlockItem } from '@/api/modules/PLC/graphBlock'
import type { LineUpGraphCatalogLookup } from '@/api/modules/LineUp/catalog'
import { describe, expect, it } from 'vitest'
import { useGraphBlockCatalog, type GraphBlockCatalogGateway } from './useGraphBlockCatalog'

function block(name: string, projectId: string, blockId: string): PlcGraphBlockItem {
  return {
    source: { plcProjectId: projectId, plcBlockId: blockId, parseRunId: 'plc_upload_' + projectId + '_20260827_abcd1234', sourceHash: 'hash-' + blockId },
    block: { blockName: name, blockType: 'FB', blockNumber: 260, programmingLanguage: 'GRAPH' },
    hasPreviewBatch: false,
  }
}

const hierarchy: Pick<LineUpGraphCatalogLookup, 'processes' | 'stations'> = {
  processes: [
    { id: '20', lineId: '10', code: 'P20', name: '上料' },
    { id: '21', lineId: '11', code: 'P21', name: '装配' },
  ],
  stations: [
    { id: '30', lineId: '10', processId: '20', code: 'OP010', name: '缓存上料工站' },
    { id: '31', lineId: '10', processId: '21', code: 'OP020', name: '夹紧工站' },
    { id: '32', lineId: '11', processId: '21', code: 'OP030', name: '装配工站' },
  ],
}

function fixture() {
  const calls: Array<{ name: string, value?: unknown }> = []
  const gateway: GraphBlockCatalogGateway = {
    async getProductionLines() {
      calls.push({ name: 'lines' })
      return [
        { id: '10', code: 'CX1', name: '产线1' },
        { id: '11', code: 'CX2', name: '产线2' },
      ]
    },
    async getProjects() {
      calls.push({ name: 'projects' })
      return [
        { id: '100', name: 'THEONE-PLC1', plcCode: 'PLC1', productionLineId: '10', plcDeviceId: '500', plcDeviceName: 'PLC1' },
        { id: '101', name: 'THEONE-PLC2', plcCode: 'PLC2', productionLineId: '10', plcDeviceId: '501', plcDeviceName: 'PLC2' },
        { id: '102', name: 'plc2-again', plcCode: 'PLC2', productionLineId: '10', plcDeviceId: '501', plcDeviceName: 'PLC2' },
        { id: '103', name: 'line2-project', plcCode: 'PLC9', productionLineId: '11', plcDeviceId: '502', plcDeviceName: 'PLC9' },
      ]
    },
    async getHierarchy(params) {
      calls.push({ name: 'hierarchy', value: params })
      return hierarchy
    },
    async getGraphBlocks(params) {
      calls.push({ name: 'blocks', value: params })
      return [block('FB260_CacheGraph', '100', '900'), block('FB270_LoadGraph', '101', '901')]
    },
  }
  return { calls, gateway }
}

function lastCall(calls: Array<{ name: string, value?: unknown }>, name: string) {
  return calls.filter(call => call.name === name).pop()
}

describe('useGraphBlockCatalog', () => {
  it('loads base options and queries without filters by default', async () => {
    const value = fixture()
    const catalog = useGraphBlockCatalog(value.gateway)
    await catalog.initialize()

    expect(catalog.lines.value).toHaveLength(2)
    expect(catalog.projects.value).toHaveLength(4)
    expect(lastCall(value.calls, 'hierarchy')?.value).toEqual({ lineId: undefined })
    expect(lastCall(value.calls, 'blocks')?.value).toEqual({ projectId: undefined, plcCode: undefined, stationCode: undefined, blockName: undefined, limit: 500 })
    expect(catalog.items.value).toHaveLength(2)
    expect(catalog.pagedItems.value).toHaveLength(2)
  })

  it('derives available PLC devices from active projects of the selected line', async () => {
    const value = fixture()
    const catalog = useGraphBlockCatalog(value.gateway)
    await catalog.initialize()

    // 未选产线：全部在用项目设备（按设备去重）
    expect(catalog.availableDevices.value.map(device => device.id)).toEqual(['500', '501', '502'])

    await catalog.selectLine('10')
    // 产线1 在用项目：THEONE-PLC1/PLC2/plc2-again → 设备 500/501 去重
    expect(catalog.availableDevices.value).toEqual([
      { id: '500', plcCode: 'PLC1', name: 'PLC1' },
      { id: '501', plcCode: 'PLC2', name: 'PLC2' },
    ])
    expect(catalog.availableProjects.value.map(project => project.id)).toEqual(['100', '101', '102'])
  })

  it('resets downstream selections when the line changes', async () => {
    const value = fixture()
    const catalog = useGraphBlockCatalog(value.gateway)
    await catalog.initialize('101')
    expect(catalog.filters.projectId).toBe('101')
    expect(catalog.filters.lineId).toBe('10')
    expect(catalog.filters.deviceId).toBe('501')

    await catalog.selectLine('11')
    expect(catalog.filters.processId).toBe('')
    expect(catalog.filters.stationId).toBe('')
    expect(catalog.filters.deviceId).toBe('')
    expect(catalog.filters.projectId).toBe('')
    expect(lastCall(value.calls, 'hierarchy')?.value).toEqual({ lineId: '11' })
  })

  it('maps device/station/keyword selections to plcCode/stationCode/blockName query params', async () => {
    const value = fixture()
    const catalog = useGraphBlockCatalog(value.gateway)
    await catalog.initialize()

    await catalog.selectLine('10')
    catalog.selectProcess('20')
    catalog.selectStation('30')
    catalog.selectDevice('500')
    catalog.filters.keyword = '  Cache  '
    await catalog.search()

    expect(lastCall(value.calls, 'blocks')?.value).toEqual({
      projectId: undefined,
      plcCode: 'PLC1',
      stationCode: 'OP010',
      blockName: 'Cache',
      limit: 500,
    })
  })

  it('keeps device and project selections consistent', async () => {
    const value = fixture()
    const catalog = useGraphBlockCatalog(value.gateway)
    await catalog.initialize()

    // 选项目 100（设备 500）后设备自动对齐
    catalog.selectProject('100')
    expect(catalog.filters.deviceId).toBe('500')

    // 再选设备 501：项目 100 不属于该设备，清空项目
    catalog.selectDevice('501')
    expect(catalog.filters.projectId).toBe('')

    // 查询带上所选设备推导的 plcCode
    await catalog.search()
    expect(lastCall(value.calls, 'blocks')?.value).toMatchObject({ plcCode: 'PLC2', projectId: undefined })
  })

  it('paginates the single-shot result locally', async () => {
    const value = fixture()
    const gateway: GraphBlockCatalogGateway = {
      ...value.gateway,
      async getGraphBlocks() {
        return Array.from({ length: 25 }, (_, index) => block('FB' + index, '100', String(900 + index)))
      },
    }
    const catalog = useGraphBlockCatalog(gateway)
    await catalog.initialize()

    expect(catalog.items.value).toHaveLength(25)
    expect(catalog.pagedItems.value).toHaveLength(20)
    catalog.filters.page = 2
    expect(catalog.pagedItems.value).toHaveLength(5)
  })

  it('builds the graph import navigation with line/device/blockName query params', async () => {
    const value = fixture()
    const catalog = useGraphBlockCatalog(value.gateway)
    await catalog.initialize()

    // 未选择时跳转不带参数
    expect(catalog.buildGraphImportRoute()).toEqual({ path: '/lineup/import', query: {} })

    await catalog.selectLine('10')
    catalog.selectDevice('500')
    catalog.selectProcess('20')
    catalog.selectStation('30')
    expect(catalog.buildGraphImportRoute('FB261_RobotGraph')).toEqual({
      path: '/lineup/import',
      query: { lineId: '10', deviceId: '500', blockName: 'FB261_RobotGraph' },
    })

    // 清空设备后设备参数随之移除，产线保留；不传块名则无 blockName
    catalog.selectDevice('')
    expect(catalog.buildGraphImportRoute()).toEqual({
      path: '/lineup/import',
      query: { lineId: '10' },
    })
  })
})
