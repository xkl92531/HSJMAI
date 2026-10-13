import type { RecursiveRequired, Settings } from '#/global'
import { cloneDeep } from 'es-toolkit'
import settingsDefault from '@/settings.default'
import { merge } from '@/utils/object'

const globalSettings: Settings.all = {

  app: {
    themeSync: true,
    lightTheme: 'orange',
    darkTheme: 'orange',
    colorScheme: 'light',
    radius: 0.5,
    enableMournMode: false,
    enableColorAmblyopiaMode: false,
    defaultLang: '',
    enablePermission: true,
    enableProgress: true,
    enableDynamicTitle: false,
    enableWatermark: false,
    enableFeedback: false,
    enableErrorLog: false,
    loginExpiredMode: 'redirect',
    enableCheckUpdates: false,
    routeBaseOn: 'backend',
    direction: 'ltr',
  },
  userPreferences: {
    enable: false,
    storageTo: 'local',
  },
  home: {
    enable: true,
    title: 'app.route.home',
    fullPath: '/',
  },
  layout: {
    widthMode: 'adaption',
    widthModeScope: 'outer',
    enableMobileAdaptation: false,
  },
  menu: {
    mode: 'head',
    subMenuCollapse: false,
    enableSubMenuCollapseButton: true,
    baseOn: 'backend',
    style: '',
    enableDark: false,
    mainMenuClickMode: 'switch',
    subMenuUniqueOpened: true,
    subMenuAutoCollapse: false,
    enableHotkeys: false,
  },
  topbar: {
    mode: 'static',
    switchTabbarAndToolbar: false,
  },
  tabbar: {
    enable: true,
    style: '',
    enableIcon: false,
    dblclickAction: 'close',
    enableMemory: false,
    enableHotkeys: false,
    storageTo: 'local',
  },
  toolbar: {
    enable: false,
    favorites: false,
    breadcrumb: true,
    navSearch: true,
    notification: false,
    i18n: true,
    fullscreen: false,
    pageReload: false,
    colorScheme: false,
    layout: ['favorites', 'breadcrumb', '->', 'navSearch', 'notification', 'i18n', 'fullscreen', 'pageReload', 'colorScheme'],
  },
  favorites: {
    storageTo: 'local',
  },
  breadcrumb: {
    style: '',
    enableMainMenu: false,
  },
  mainPage: {
    enableHotkeys: true,
    iframeCacheMax: 3,
    enableTransition: true,
    transitionMode: 'fade',
  },
  navSearch: {
    enableHotkeys: true,
  },
  copyright: {
    enable: false,
    dates: '2026',
    company: 'HSJM',
    website: '',
    beian: '',
  },

}

export default merge(globalSettings, cloneDeep(settingsDefault)) as RecursiveRequired<Settings.all>
