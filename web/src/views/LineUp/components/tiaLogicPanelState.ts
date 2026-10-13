import type { Ref } from 'vue'
import { computed, ref, watch } from 'vue'

export type TiaLogicPanelTab
  = | 'actions'
    | 'interlock'
    | 'supervision'
    | 'step-tags'
    | 'runtime-step'
    | 'runtime-conditions'
    | 'runtime-faults'
    | 'runtime-events'
    | 'runtime-bom'
    | 'bom-images'

export function useTiaLogicPanelTab(
  controlledTab: Readonly<Ref<TiaLogicPanelTab | undefined>>,
  selectionSourceKey: Readonly<Ref<string>>,
  onUpdate: (value: TiaLogicPanelTab) => void,
  defaultTab: TiaLogicPanelTab = 'actions',
) {
  const internalTab = ref<TiaLogicPanelTab>(defaultTab)
  const effectiveTab = computed(() => controlledTab.value ?? internalTab.value)

  watch(selectionSourceKey, () => {
    internalTab.value = defaultTab
    if (controlledTab.value !== undefined && controlledTab.value !== defaultTab) {
      onUpdate(defaultTab)
    }
  })

  function selectTab(tab: TiaLogicPanelTab) {
    if (controlledTab.value === undefined) {
      internalTab.value = tab
    }
    onUpdate(tab)
  }

  return { effectiveTab, selectTab }
}

export function formatTiaActiveDuration(value: number | null | undefined) {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0
    ? `${(value / 1000).toFixed(1)} s`
    : '—'
}
