import type { RouteRecordRaw } from 'vue-router'
import { $t } from '@/locales'

export const lineUpFunctionRoutes: RouteRecordRaw[] = [
  {
    path: '/lineup/monitor',
    name: 'LineUpMonitor',
    component: () => import('@/views/LineUp/monitor/index.vue'),
    meta: {
      title: $t('app.route.monitorEmbed'),
      menu: false,
      cache: true,
    },
  },
  {
    path: '/lineup/bindings',
    name: 'LineUpBindingSettings',
    component: () => import('@/views/LineUp/bindings/index.vue'),
    meta: {
      title: $t('app.route.lineupBinding'),
      menu: false,
      cache: true,
    },
  },
  {
    path: '/lineup/graphs',
    name: 'LineUpGraphCatalog',
    component: () => import('@/views/LineUp/catalog/index.vue'),
    meta: {
      title: $t('app.route.lineupGraphs'),
      menu: false,
      cache: true,
    },
  },
  {
    path: '/lineup/import',
    name: 'LineUpImport',
    component: () => import('@/views/LineUp/workspace/index.vue'),
    meta: {
      title: $t('app.route.lineupImport'),
      // 数据库菜单（LineUpImportMenu）也指向本页面；menu:false 避免导航里出现重复入口
      menu: false,
    },
  },
  {
    path: '/lineup/history',
    name: 'LineUpHistory',
    component: () => import('@/views/LineUp/history/index.vue'),
    meta: {
      title: $t('app.route.lineupHistory'),
      menu: false,
      cache: true,
    },
  },
  {
    path: '/lineup/image-marks',
    name: 'LineUpImageMarks',
    component: () => import('@/views/LineUp/image-marks/index.vue'),
    meta: {
      title: $t('app.route.lineupBom'),
      menu: false,
      cache: true,
    },
  },
  {
    path: '/lineup/master-data',
    name: 'LineUpMasterData',
    component: () => import('@/views/LineUp/master-data/index.vue'),
    meta: {
      title: $t('app.route.lineupBasic'),
      // 数据库菜单（BasicMasterDataMenu）指向本页面；menu:false 避免导航里出现重复入口
      menu: false,
      cache: true,
    },
  },
  {
    path: '/lineup/business-config',
    name: 'LineUpBusinessConfig',
    component: () => import('@/views/LineUp/business-config/index.vue'),
    meta: {
      title: $t('app.route.lineupConfig'),
      menu: false,
      cache: true,
    },
  },
]
