import type { Route } from '#/global'
import type { RouteRecordRaw } from 'vue-router'
import generatedRoutes from 'virtual:generated-pages'
import { setupLayouts } from 'virtual:meta-layouts'
import { $t } from '@/locales'
import pinia from '@/store'
import { lineUpFunctionRoutes } from './modules/lineup'
import { mcpProgressTestSystemChildren } from './mcpProgressTestSystemChildren'
import MultilevelMenuExample from './modules/multilevel.menu.example'

// 固定路由（默认路由）
const constantRoutes: RouteRecordRaw[] = [
  {
    path: '/login',
    name: 'login',
    component: () => import('@/views/login.vue'),
    meta: {
      whiteList: true,
      title: $t('app.route.login'),
    },
  },
  {
    path: '/linediagnosis',
    name: 'LineDiagnosisStandalone',
    component: () => import('@/views/PLC/line-diagnosis.vue'),
    meta: {
      whiteList: true,
      title: 'Line Stop Diagnosis',
      menu: false,
    },
  },
  {
    // 外部系统嵌入专用：不套 Layout（无菜单/顶栏/标签栏），复用运行监控页组件。
    // 支持与 /lineup/monitor 相同的直达 query（subStationId/bindingId/sequenceSourceKey/lineId+deviceId+stationId），
    // 另支持 ?token=<jwt> 注入登录态（guards.ts setupRoutes 处理）。
    path: '/lineup/monitor-embed',
    name: 'LineUpMonitorEmbed',
    component: () => import('@/views/LineUp/monitor/index.vue'),
    meta: {
      title: $t('app.route.monitorEmbed'),
      menu: false,
    },
  },
  {
    path: '/:all(.*)*',
    name: 'notFound',
    component: () => import('@/views/[...all].vue'),
    meta: {
      title: $t('app.route.notFound'),
    },
  },
]

// 系统路由
const systemRoutes: RouteRecordRaw[] = [
  {
    path: '/',
    component: () => import('@/layouts/index.vue'),
    meta: {
      breadcrumb: false,
    },
    children: [
      {
        path: '',
        component: () => import('@/views/index.vue'),
        meta: {
          title: $t(useSettingsStore(pinia).settings.home.title),
          icon: 'i-ant-design:home-twotone',
          breadcrumb: false,
        },
      },
      {
        path: 'reload',
        name: 'reload',
        component: () => import('@/views/reload.vue'),
        meta: {
          title: $t('app.route.reload'),
          breadcrumb: false,
        },
      },
      {
        path: 'agent/workflow/detail',
        name: 'WorkflowDetail',
        component: () => import('@/views/agent/workflow/detail.vue'),
        meta: {
          title: $t('app.route.workflowDetail'),
          sidebar: false,
        },
      },
      {
        path: '/diagnostic_test',
        name: 'DiagnosticTest',
        component: () => import('@/views/diagnostic_test/index.vue'),
        meta: {
          title: $t('app.route.workflowDetail'),
          sidebar: false,
        },
      },
      {
        path: '/energy-flow',
        name: 'EnergyFlow',
        component: () => import('@/views/diagnostic_test/energy-flow.vue'),
        meta: {
          title: $t('app.route.workflowDetail'),
          sidebar: false,
        },
      },
      {
        path: '/energy-flow-chat',
        name: 'EnergyFlowChat',
        component: () => import('@/views/diagnostic/energy-flow-chat/index.vue'),
        meta: {
          title: $t('app.route.energyFlowChat'),
          sidebar: false,
        },
      },
      ...mcpProgressTestSystemChildren,
      {
        path: '/parse',
        name: 'parse',
        component: () => import('@/views/AI_PLC/Project/parse.vue'),
        meta: {
          whiteList: true,
          title: $t('app.route.parse'),
          menu: false,
          cache: true,
        },
      },
      {
        path: '/PLC/detail',
        name: 'PLCdetail',
        component: () => import('@/views/PLC/detail.vue'),
        meta: {
          whiteList: true,
          title: $t('app.route.plcDetail'),
          menu: false,
          cache: true,
        },
      },
      {
        path: '/plc/monitoring',
        name: 'PLCMonitoring',
        component: () => import('@/views/PLC/monitoring.vue'),
        meta: {
          whiteList: true,
          title: $t('app.route.plcMonitoring'),
          menu: false,
          cache: true,
        },
      },
      {
        path: '/plc/line-diagnosis',
        name: 'PLCLineDiagnosis',
        component: () => import('@/views/PLC/line-diagnosis.vue'),
        meta: {
          whiteList: true,
          title: $t('app.route.lineDiagnosis'),
          menu: false,
          cache: true,
        },
      },
      {
        path: '/plc/signal-live',
        name: 'PLCSignalLive',
        component: () => import('@/views/PLC/signal-live.vue'),
        meta: {
          whiteList: true,
          title: $t('app.route.signalLive'),
          menu: false,
          cache: true,
        },
      },
      {
        path: '/plc/signal-history',
        name: 'PLCSignalHistory',
        component: () => import('@/views/PLC/signal-history.vue'),
        meta: {
          whiteList: true,
          title: $t('app.route.signalHistory'),
          menu: false,
          cache: true,
        },
      },
      {
        path: '/plc/alarm-variable',
        name: 'PLCAlarmVariable',
        component: () => import('@/views/PLC/alarm-variable.vue'),
        meta: {
          whiteList: true,
          title: $t('app.route.alarmVariable'),
          menu: false,
          cache: true,
        },
      },
      {
        path: '/plc/job-center',
        name: 'PLCJobCenter',
        component: () => import('@/views/PLC/job-center.vue'),
        meta: {
          whiteList: true,
          title: $t('app.route.jobCenter'),
          menu: false,
          cache: true,
        },
      },
      ...lineUpFunctionRoutes,
      {
        path: '/lineup/workspace',
        name: 'LineUpWorkspaceLegacy',
        redirect: '/lineup/graphs',
        meta: {
          title: $t('app.route.lineupGraphs'),
          menu: false,
        },
      },
      {
        path: '/plc/cylinder-signal-event-test',
        name: 'PLCCylinderSignalEventTest',
        component: () => import('@/views/PLC/cylinder-signal-event-test.vue'),
        meta: {
          title: $t('app.route.cylinderTest'),
          menu: false,
          cache: true,
        },
      },
    ],
  },
]

// 动态路由（异步路由、导航栏路由）
const asyncRoutes: Route.recordMainRaw[] = [
  {
    meta: {
      title: $t('app.route.demo'),
      icon: 'i-uim:box',
    },
    children: [
      MultilevelMenuExample,
    ],
  },
]

const constantRoutesByFilesystem = generatedRoutes.filter((item) => {
  return item.meta?.enabled !== false && item.meta?.constant === true
})

const asyncRoutesByFilesystem = [...setupLayouts(generatedRoutes.filter((item) => {
  return item.meta?.enabled !== false && item.meta?.constant !== true && item.meta?.layout !== false
}))]

export {
  asyncRoutes,
  asyncRoutesByFilesystem,
  constantRoutes,
  constantRoutesByFilesystem,
  systemRoutes,
}
