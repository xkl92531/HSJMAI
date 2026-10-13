<i18n lang="yaml">
zh-cn:
  searchPlaceholder: 搜索...
  searchTabs: 搜索标签页
  closeOtherSide: 关闭其它标签页
  closeLeftSide: 关闭左侧标签页
  closeRightSide: 关闭右侧标签页
zh-tw:
  searchPlaceholder: 搜索...
  searchTabs: 搜索標籤頁
  closeOtherSide: 關閉其它標籤頁
  closeLeftSide: 關閉左側標籤頁
  closeRightSide: 關閉右側標籤頁
en:
  searchPlaceholder: Search...
  searchTabs: Search Tabs
  closeOtherSide: Close Otherside Tabs
  closeLeftSide: Close Leftside Tabs
  closeRightSide: Close Rightside Tabs
ja:
  searchPlaceholder: 検索...
  searchTabs: タブを検索
  closeOtherSide: 他のタブを閉じる
  closeLeftSide: 左側のタブを閉じる
  closeRightSide: 右側のタブを閉じる
</i18n>

<script setup lang="ts">
import type { Tabbar } from '#/global'
import { match } from 'pinyin-pro'
import Sortable from 'sortablejs'

defineOptions({
  name: 'TabbarMoreAction',
})

const router = useRouter()

const settingsStore = useSettingsStore()
const tabbarStore = useTabbarStore()

const tabbar = useTabbar()

const { t } = useI18n()
const { generateI18nTitle } = useMenu()

const open = ref(false)

const searchInput = ref('')
const searchInputRef = useTemplateRef('searchInputRef')
watch(open, (val) => {
  if (val) {
    nextTick(() => {
      searchInputRef.value?.focus()
    })
  }
  else {
    searchInput.value = ''
  }
})

const resultList = computed(() => {
  let result = []
  result = tabbarStore.list.filter((item) => {
    let flag = false
    if (
      generateI18nTitle(item.title).toString().includes(searchInput.value)
      || (match(generateI18nTitle(item.title).toString(), searchInput.value, { continuous: true })?.length ?? 0) > 0
    ) {
      flag = true
    }
    return flag
  })
  return result
})

const activedTabId = computed(() => tabbar.getId())

const dropdownTabContainerRef = useTemplateRef('dropdownTabContainerRef')

const isDragging = ref(false)
let tabSortable: Sortable
watch(() => dropdownTabContainerRef.value, (val) => {
  if (val) {
    tabSortable = new Sortable(val, {
      animation: 200,
      ghostClass: 'opacity-0',
      draggable: '.tab',
      onStart: () => {
        isDragging.value = true
      },
      onEnd: () => {
        isDragging.value = false
      },
      onMove: (e) => {
        return e.dragged.classList.contains('pinned') ? e.related.classList.contains('pinned') : !e.related.classList.contains('pinned')
      },
      onUpdate: (e) => {
        if (e.newIndex !== undefined && e.oldIndex !== undefined) {
          tabbarStore.sort(e.newIndex, e.oldIndex)
        }
      },
    })
    if (settingsStore.mode === 'mobile') {
      tabSortable.option('disabled', true)
    }
  }
  else {
    tabSortable.destroy()
  }
})
watch(searchInput, (val) => {
  nextTick(() => {
    tabSortable.option('disabled', !!val)
  })
})

function iconName(isActive: boolean, icon: Tabbar.recordRaw['icon'], activeIcon: Tabbar.recordRaw['activeIcon']) {
  let name
  if ((!isActive && icon) || (isActive && !activeIcon)) {
    name = icon
  }
  else if (isActive && activeIcon) {
    name = activeIcon
  }
  return name
}

function handleClick(fullPath: string) {
  router.push(fullPath)
  open.value = false
}
</script>

<template>
  <FaDropdown
    v-model:open="open" align="end" :items="[
      [
        { label: t('closeOtherSide'), icon: 'i-mdi:close', handle: () => tabbar.closeOtherSide(), disabled: !tabbar.checkCloseOtherSide() },
        { label: t('closeLeftSide'), icon: 'i-mdi:arrow-expand-left', handle: () => tabbar.closeLeftSide(), disabled: !tabbar.checkCloseLeftSide() },
        { label: t('closeRightSide'), icon: 'i-mdi:arrow-expand-right', handle: () => tabbar.closeRightSide(), disabled: !tabbar.checkCloseRightSide() },
      ],
    ]" class="min-w-auto p-0"
  >
    <FaButton variant="outline" size="icon" class="h-7 w-7">
      <FaIcon name="i-ep:caret-bottom" />
    </FaButton>
    <template #footer>
      <div class="max-h-80 w-40 flex flex-col gap-2">
        <input ref="searchInputRef" v-model="searchInput" :placeholder="t('searchPlaceholder')" class="h-8 border border-input rounded-md bg-background px-3 py-2 text-sm placeholder:text-muted-foreground focus-visible:outline-none">
        <FaScrollArea :scrollbar="false" mask gradient-color="hsl(var(--popover))">
          <div ref="dropdownTabContainerRef" class="tabs space-y-1" :class="{ dragging: isDragging }">
            <div
              v-for="element in resultList" :key="element.tabMerge === 'routeName' && element.routeName ? element.routeName : element.tabId" class="relative h-8 rounded-md px-2 tab" :class="{
                'bg-accent': element.tabId === activedTabId,
                'pinned': element.isPermanent || element.isPin,
              }"
            >
              <div :key="element.tabId" class="title" :title="element.customTitleList.find(item => item.fullPath === element.fullPath)?.title || generateI18nTitle(element.title)" @click="handleClick(element.fullPath)">
                <FaIcon v-if="settingsStore.settings.tabbar.enableIcon && iconName(element.tabId === activedTabId, element.icon, element.activeIcon)" :name="iconName(element.tabId === activedTabId, element.icon, element.activeIcon)!" />
                {{ element.customTitleList.find(item => item.fullPath === element.fullPath)?.title || generateI18nTitle(element.title) }}
              </div>
              <div v-if="!element.isPermanent && element.isPin" class="action-icon" @click.stop="tabbarStore.unPin(element.tabId)" @dblclick.stop>
                <FaIcon name="i-ri:pushpin-2-fill" />
              </div>
              <div v-else-if="!element.isPermanent && resultList.length > 1" class="action-icon" @click.stop="tabbar.closeById(element.tabId)" @dblclick.stop>
                <FaIcon name="i-ri:close-fill" />
              </div>
            </div>
          </div>
        </FaScrollArea>
      </div>
    </template>
  </FaDropdown>
</template>

<style scoped>
.tabs {
  &:not(.dragging) {
    .tab:hover {
      &:not(.actived) {
        --uno: bg-accent;
      }

      .action-icon {
        opacity: 1;
      }
    }
  }

  .tab {
    transition: background-color 0.3s;

    &:hover {
      .title {
        --uno: opacity-100;

        margin-inline-end: 20px;
        mask-image: linear-gradient(to right, #000 calc(100% - 44px), transparent);

        [dir="rtl"] & {
          mask-image: linear-gradient(to left, #000 calc(100% - 44px), transparent);
        }
      }
    }

    * {
      user-select: none;
    }

    .title {
      --uno: opacity-70 transition;

      position: relative;
      display: flex;
      gap: 5px;
      align-items: center;
      height: 100%;
      overflow: hidden;
      font-size: 14px;
      white-space: nowrap;
      cursor: pointer;
      mask-image: linear-gradient(to right, #000 calc(100% - 24px), transparent);

      [dir="rtl"] & {
        mask-image: linear-gradient(to left, #000 calc(100% - 24px), transparent);
      }

      i {
        flex-shrink: 0;
        font-size: 16px;
      }
    }

    .action-icon {
      --uno: transition absolute inset-e-2 top-1/2 -translate-y-1/2 rounded-full z-10 w-5 h-5 flex-center text-xs "text-[var(--g-tabbar-tab-color)]" op-0 hover:(border bg-secondary);
    }
  }
}
</style>
