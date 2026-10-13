import { describe, expect, it } from 'vitest'
import { lineUpFunctionRoutes } from './lineup'

describe('lineUpFunctionRoutes', () => {
  it('exposes the project GRAPH catalog and the master-data maintenance page', () => {
    expect(lineUpFunctionRoutes.map(route => ({
      name: route.name,
      path: route.path,
      title: route.meta?.title,
    }))).toEqual([
      { name: 'LineUpMonitor', path: '/lineup/monitor', title: '设备运行监控' },
      { name: 'LineUpBindingSettings', path: '/lineup/bindings', title: '绑定设置' },
      { name: 'LineUpGraphCatalog', path: '/lineup/graphs', title: 'GRAPH 结构管理' },
      { name: 'LineUpImport', path: '/lineup/import', title: 'GRAPH 结构导入' },
      { name: 'LineUpHistory', path: '/lineup/history', title: '运行历史' },
      { name: 'LineUpImageMarks', path: '/lineup/image-marks', title: 'BOM 图片点位' },
      { name: 'LineUpMasterData', path: '/lineup/master-data', title: '基础信息' },
      { name: 'LineUpBusinessConfig', path: '/lineup/business-config', title: '业务配置' },
    ])
    // /lineup/import 静态直达 GRAPH 智能导入工作台（数据库菜单动态路由未挂出时依然可达）
    expect(lineUpFunctionRoutes.find(route => route.path === '/lineup/import')).toMatchObject({
      name: 'LineUpImport',
      meta: { title: 'GRAPH 结构导入', menu: false },
    })
    // /lineup/master-data 静态直达基础信息维护页（数据库菜单 BasicMasterDataMenu 的组件载体）
    expect(lineUpFunctionRoutes.find(route => route.path === '/lineup/master-data')).toMatchObject({
      name: 'LineUpMasterData',
      meta: { title: '基础信息', menu: false, cache: true },
    })
    expect(lineUpFunctionRoutes.some(route => route.name === 'LineUpMasterDataLegacy')).toBe(false)
    expect(lineUpFunctionRoutes.find(route => route.name === 'LineUpBindingSettings')).toMatchObject({
      path: '/lineup/bindings',
      meta: { title: '绑定设置', menu: false, cache: true },
    })
  })

  it('does not replace the website home route with a LineUp function page', () => {
    expect(lineUpFunctionRoutes.every(route => route.path !== '/')).toBe(true)
  })
})
