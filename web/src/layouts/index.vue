<script setup lang="ts">
import { useTimeoutFn } from '@vueuse/core'
import hotkeys from 'hotkeys-js'
import { useSlots } from '@/slots'
import eventBus from '@/utils/eventBus'
import AppSetting from './components/AppSetting/index.vue'
import Header from './components/Header/index.vue'
import HotkeysIntro from './components/HotkeysIntro/index.vue'
import MainSidebar from './components/MainSidebar/index.vue'
import Preferences from './components/Preferences/index.vue'
import SubSidebar from './components/SubSidebar/index.vue'
import Topbar from './components/Topbar/index.vue'
import IframeView from './components/views/iframe.vue'
import LinkView from './components/views/link.vue'

defineOptions({
  name: 'Layout',
})

const routeInfo = useRoute()

const settingsStore = useSettingsStore()
const keepAliveStore = useKeepAliveStore()
const menuStore = useMenuStore()
useWatermarkStore()

const mainPage = useMainPage()

// 头部是否隐藏
const isHeaderHide = computed(() => {
  return ['single', 'side', 'only-side', 'side-panel'].includes(settingsStore.settings.menu.mode) || settingsStore.mode === 'mobile' || settingsStore.mainPageMaximizeStatus
})

// 侧边栏主导航是否隐藏
const isMainSidebarHide = computed(() => {
  return settingsStore.settings.menu.mode === 'single'
    || (['head', 'only-head', 'head-panel'].includes(settingsStore.settings.menu.mode) && settingsStore.mode !== 'mobile')
    || settingsStore.mainPageMaximizeStatus
})

// 侧边栏次导航是否隐藏
const isSubSidebarHide = computed(() => {
  return (['only-side', 'only-head', 'side-panel', 'head-panel'].includes(settingsStore.settings.menu.mode) && settingsStore.mode !== 'mobile')
    || menuStore.sidebarMenus.every(item => item.meta?.menu === false)
    || settingsStore.mainPageMaximizeStatus
})

// 标签栏是否隐藏
const isTabbarHide = computed(() => {
  return !settingsStore.settings.tabbar.enable || settingsStore.mainPageMaximizeStatus
})

// 工具栏是否隐藏
const isToolbarHide = computed(() => {
  return !settingsStore.settings.toolbar.enable
    || !settingsStore.settings.toolbar.layout.some((item) => {
      if (item !== '->') {
        if (settingsStore.settings.app.routeBaseOn === 'filesystem' && item === 'breadcrumb') {
          return false
        }
        return settingsStore.settings.toolbar[item]
      }
      return false
    })
    || settingsStore.mainPageMaximizeStatus
})

const isIframe = computed(() => !!routeInfo.meta.iframe)
const isLink = computed(() => !!routeInfo.meta.link)

watch(() => settingsStore.settings.menu.subMenuCollapse, (val) => {
  if (settingsStore.mode === 'mobile') {
    if (!val) {
      document.body.classList.add('overflow-hidden')
    }
    else {
      document.body.classList.remove('overflow-hidden')
    }
  }
})

watch(() => routeInfo.path, () => {
  if (settingsStore.mode === 'mobile') {
    settingsStore.$patch((state) => {
      state.settings.menu.subMenuCollapse = true
    })
  }
})

onMounted(() => {
  hotkeys('alt+up,alt+down', (e, handle) => {
    e.preventDefault()
    switch (handle.key) {
      case 'alt+up':
        mainPage.maximize(true)
        break
      case 'alt+down':
        mainPage.maximize(false)
        break
    }
  })
})
onUnmounted(() => {
  hotkeys.unbind('alt+up,alt+down')
})

// 是否悬停在侧边栏导航上
const isHoverSidebar = ref(false)
let timeout: (() => void) | undefined
function handleMouseenter() {
  timeout?.()
  isHoverSidebar.value = true
}
function handleMouseleave() {
  timeout?.()
  ;({ stop: timeout } = useTimeoutFn(() => {
    isHoverSidebar.value = false
  }, 300))
}

const enableAppSetting = import.meta.env.VITE_APP_SETTING
</script>

<template>
  <div
    class="layout" :style="{
      '--g-header-actual-height': isHeaderHide ? '0px' : 'var(--g-header-height)',
      '--g-main-sidebar-actual-width': isMainSidebarHide ? '0px' : 'var(--g-main-sidebar-width)',
      '--g-sub-sidebar-actual-width': isSubSidebarHide ? '0px' : (settingsStore.settings.menu.subMenuCollapse && settingsStore.mode !== 'mobile' ? 'var(--g-sub-sidebar-collapse-width)' : 'var(--g-sub-sidebar-width)'),
      '--g-tabbar-actual-height': isTabbarHide ? '0px' : 'var(--g-tabbar-height)',
      '--g-toolbar-actual-height': isToolbarHide ? '0px' : 'var(--g-toolbar-height)',
    }"
  >
    <div id="app-main" :class="{ 'main-page-maximize': settingsStore.mainPageMaximizeStatus }">
      <Header :class="{ dark: settingsStore.settings.menu.enableDark }" />
      <div class="wrapper">
        <div class="sidebar-container" :class="{ show: settingsStore.mode === 'mobile' && !settingsStore.settings.menu.subMenuCollapse }" @mouseenter="handleMouseenter" @mouseleave="handleMouseleave">
          <MainSidebar :class="{ dark: settingsStore.settings.menu.enableDark }" />
          <SubSidebar :is-hover="isHoverSidebar" :class="{ dark: settingsStore.settings.menu.enableDark }" />
        </div>
        <div class="invisible fixed inset-0 z-1009 bg-black/50 op-0 backdrop-blur-sm transition-opacity" :class="{ 'op-100! visible!': settingsStore.mode === 'mobile' && !settingsStore.settings.menu.subMenuCollapse }" @click="settingsStore.toggleSidebarCollapse()" />
        <div class="main-container pb-[var(--g-main-container-padding-bottom)]">
          <Topbar />
          <div id="app-content" class="main">
            <div v-show="settingsStore.mainPageMaximizeStatus" class="exit-main-page-maximize" @click="settingsStore.setMainPageMaximize()">
              <FaIcon name="i-ri:logout-box-line" />
            </div>
            <RouterView v-slot="{ Component, route }">
              <Transition :name="(settingsStore.settings.mainPage.enableTransition && !settingsStore.isReloading) ? settingsStore.settings.mainPage.transitionMode : ''" mode="out-in">
                <KeepAlive :include="keepAliveStore.list">
                  <component :is="Component" v-show="!(isIframe || isLink)" :key="route.fullPath" />
                </KeepAlive>
              </Transition>
            </RouterView>
            <IframeView v-show="isIframe && !isLink" />
            <LinkView v-if="isLink" />
          </div>
          <div class="copyright">
            <FaCopyright />
          </div>
        </div>
      </div>
    </div>
    <Preferences v-if="settingsStore.settings.userPreferences.enable" />
    <HotkeysIntro />
    <template v-if="enableAppSetting">
      <div class="app-setting" @click="eventBus.emit('global-app-setting-toggle')">
        <FaIcon name="i-uiw:setting-o" class="icon" />
      </div>
      <AppSetting />
    </template>
    <component :is="useSlots('free-position')" />
  </div>
</template>

<style scoped>
[data-app-width-mode-scope="outer"][data-app-width-mode="adaption"]:not([data-mode="mobile"]) {
  #app-main {
    width: 100%;
  }
}

[data-app-width-mode-scope="outer"][data-app-width-mode="center"]:not([data-mode="mobile"]) {
  #app-main {
    width: min(var(--g-app-width), 100%);
  }
}

[data-mode="mobile"] {
  #app-main {
    width: 100%;
  }

  .sidebar-container {
    transform: translateX(calc((var(--g-main-sidebar-width) + var(--g-sub-sidebar-width)) * -1));

    [dir="rtl"] & {
      transform: translateX(calc(var(--g-main-sidebar-width) + var(--g-sub-sidebar-width)));
    }

    &.show {
      transform: translateX(0);
    }
  }

  .main-container {
    margin-inline-start: 0 !important;
  }

  &[data-menu-mode="single"] {
    .sidebar-container {
      transform: translateX(calc(var(--g-sub-sidebar-width) * -1));

      [dir="rtl"] & {
        transform: translateX(var(--g-sub-sidebar-width));
      }

      &.show {
        transform: translateX(0);
      }
    }
  }
}

.layout {
  height: 100%;
}

#app-main {
  height: 100%;
  margin: 0 auto;

  /* 页面最大化 */
  &.main-page-maximize {
    :deep(header),
    :deep(.sidebar-container),
    :deep(.topbar-container) {
      display: none;
    }
  }
}

.wrapper {
  position: relative;
  width: 100%;
  height: 100%;
  padding-top: var(--g-header-actual-height);
  transition: padding-top 0.3s;

  .sidebar-container {
    position: fixed;
    top: var(--g-header-actual-height);
    bottom: 0;
    z-index: 1010;
    display: flex;
    width: calc(var(--g-main-sidebar-actual-width) + var(--g-sub-sidebar-actual-width));
    box-shadow: -1px 0 0 0 hsl(var(--border)), 1px 0 0 0 hsl(var(--border));
    transition: width 0.3s, transform 0.3s, box-shadow 0.3s, top 0.3s;

    &:has(> .main-sidebar-container.main-sidebar-enter-active),
    &:has(> .main-sidebar-container.main-sidebar-leave-active),
    &:has(> .sub-sidebar-container.sub-sidebar-enter-active),
    &:has(> .sub-sidebar-container.sub-sidebar-leave-active) {
      overflow: hidden;
    }
  }

  .main-sidebar-container:not(.main-sidebar-leave-active) + .sub-sidebar-container {
    inset-inline-start: var(--g-main-sidebar-width);
  }

  .main-container {
    display: flex;
    flex-direction: column;
    min-height: 100%;
    margin-inline-start: calc(var(--g-main-sidebar-actual-width) + var(--g-sub-sidebar-actual-width));
    overflow: auto;
    background-color: var(--g-main-area-bg);
    box-shadow: -1px 0 0 0 hsl(var(--border)), 1px 0 0 0 hsl(var(--border));
    transition: margin-inline-start 0.3s, background-color 0.3s, box-shadow 0.3s;

    .main {
      position: relative;
      flex: auto;
      width: 100%;
      height: 100%;
      margin: calc(var(--g-tabbar-actual-height) + var(--g-toolbar-actual-height)) auto 0;
      overflow: hidden;
      background-color: var(--g-main-area-bg);
      box-shadow: -1px 0 0 0 hsl(var(--border)), 1px 0 0 0 hsl(var(--border));

      [data-app-width-mode-scope="inner"][data-app-width-mode="adaption"]:not([data-mode="mobile"]) & {
        width: 100%;
      }

      [data-app-width-mode-scope="inner"][data-app-width-mode="center"]:not([data-mode="mobile"]) & {
        width: min(var(--g-app-width), 100%);
      }

      .exit-main-page-maximize {
        --uno: bg-primary text-primary-foreground op-50 hover-op-100 transition-opacity;

        position: fixed;
        top: -40px;
        right: -40px;
        z-index: 1000;
        width: 80px;
        height: 80px;
        cursor: pointer;
        border-radius: 50%;

        i {
          position: absolute;
          bottom: 16px;
          left: 16px;
        }
      }
    }

    .copyright {
      width: 100%;
      margin-inline: auto;
      box-shadow: -1px 0 0 0 hsl(var(--border)), 1px 0 0 0 hsl(var(--border));

      [data-app-width-mode-scope="inner"][data-app-width-mode="adaption"]:not([data-mode="mobile"]) & {
        width: 100%;
      }

      [data-app-width-mode-scope="inner"][data-app-width-mode="center"]:not([data-mode="mobile"]) & {
        width: min(var(--g-app-width), 100%);
      }
    }
  }
}

.app-setting {
  --uno: bg-primary text-primary-foreground ltr:rounded-l-md rtl:rounded-r-md;

  position: fixed;
  inset-inline-end: 0;
  top: calc(50% + 250px);
  z-index: 10;
  display: flex;
  align-items: center;
  justify-content: center;
  width: 50px;
  height: 50px;
  font-size: 24px;
  cursor: pointer;

  .icon {
    animation: rotate 5s linear infinite;
  }

  @keyframes rotate {
    from {
      transform: rotate(0deg);
    }

    to {
      transform: rotate(360deg);
    }
  }
}

/* 主内容区动画 */
.fade-enter-active,
.slide-left-enter-active,
.slide-right-enter-active,
.slide-top-enter-active,
.slide-bottom-enter-active {
  transition: 0.2s;
}

.fade-leave-active,
.slide-left-leave-active,
.slide-right-leave-active,
.slide-top-leave-active,
.slide-bottom-leave-active {
  transition: 0.15s;
}

.fade-enter-from {
  opacity: 0;
}

.fade-leave-to {
  opacity: 0;
}

.slide-left-enter-from {
  margin-left: 20px;
  opacity: 0;
}

.slide-left-leave-to {
  margin-left: -20px;
  opacity: 0;
}

.slide-right-enter-from {
  margin-left: -20px;
  opacity: 0;
}

.slide-right-leave-to {
  margin-left: 20px;
  opacity: 0;
}

.slide-top-enter-from {
  margin-top: 20px;
  opacity: 0;
}

.slide-top-leave-to {
  margin-top: -20px;
  opacity: 0;
}

.slide-bottom-enter-from {
  margin-top: -20px;
  opacity: 0;
}

.slide-bottom-leave-to {
  margin-top: 20px;
  opacity: 0;
}
</style>
