import type {
  LineUpMonitorAdminGateway,
  LineUpMonitorBindingSummary,
  LineUpMonitorNavigationNode,
  LineUpRuntimeBindingPreparation,
  LineUpRuntimeBindingValidation,
} from '@/api/modules/LineUp/monitor'
import type { EntityId } from '@/api/modules/LineUp/types'
import { computed, ref } from 'vue'
import lineUpMonitorApi from '@/api/modules/LineUp/monitor'

export function createLineUpBindingWorkspace(gateway: LineUpMonitorAdminGateway = lineUpMonitorApi) {
  const navigationNodes = ref<LineUpMonitorNavigationNode[]>([])
  const selectedSubStationId = ref<EntityId | null>(null)
  const bindings = ref<LineUpMonitorBindingSummary[]>([])
  const preparations = ref<Record<string, LineUpRuntimeBindingPreparation>>({})
  const validations = ref<Record<string, LineUpRuntimeBindingValidation>>({})
  const navigationLoading = ref(false)
  const bindingLoading = ref(false)
  const actionBusy = ref<Record<string, boolean>>({})
  const errorCode = ref('')
  let navigationGeneration = 0
  let bindingGeneration = 0
  let lastKeyword = ''

  const selectedNode = computed(() => navigationNodes.value.find(node => (
    node.nodeType === 'SubStation' && node.entityId === selectedSubStationId.value
  )) ?? null)
  const selectedPath = computed(() => {
    const path: LineUpMonitorNavigationNode[] = []
    const byId = new Map(navigationNodes.value.map(node => [node.nodeId, node]))
    let current = selectedNode.value
    while (current) {
      path.unshift(current)
      current = current.parentNodeId ? byId.get(current.parentNodeId) ?? null : null
    }
    return path
  })

  async function refreshNavigation(keyword = lastKeyword) {
    const generation = ++navigationGeneration
    lastKeyword = keyword.trim()
    navigationLoading.value = true
    try {
      const result = await gateway.getNavigation(true, lastKeyword)
      if (generation !== navigationGeneration) {
        return
      }
      navigationNodes.value = result
      if (selectedSubStationId.value && !result.some(node => (
        node.nodeType === 'SubStation' && node.entityId === selectedSubStationId.value
      ))) {
        selectedSubStationId.value = null
        bindings.value = []
        bindingGeneration++
      }
    }
    catch (error) {
      if (generation === navigationGeneration) {
        errorCode.value = safeErrorCode(error)
      }
    }
    finally {
      if (generation === navigationGeneration) {
        navigationLoading.value = false
      }
    }
  }

  async function selectSubStation(subStationId: EntityId) {
    selectedSubStationId.value = subStationId
    bindings.value = []
    preparations.value = {}
    validations.value = {}
    errorCode.value = ''
    await loadBindings(subStationId, true)
  }

  async function loadBindings(subStationId: EntityId, clear: boolean) {
    const generation = ++bindingGeneration
    if (clear) {
      bindings.value = []
    }
    bindingLoading.value = true
    try {
      const result = await gateway.getBindingsBySubStation(subStationId)
      if (generation !== bindingGeneration || selectedSubStationId.value !== subStationId) {
        return
      }
      bindings.value = result.filter(binding => binding.subStationId === subStationId)
    }
    catch (error) {
      if (generation === bindingGeneration) {
        errorCode.value = safeErrorCode(error)
      }
    }
    finally {
      if (generation === bindingGeneration) {
        bindingLoading.value = false
      }
    }
  }

  async function prepare(bindingId: EntityId) {
    await runAction(bindingId, async () => {
      const result = await gateway.prepareBinding(bindingId)
      preparations.value = { ...preparations.value, [bindingId]: result }
      validations.value = without(validations.value, bindingId)
      await refreshAfterAction()
    })
  }

  async function validate(bindingId: EntityId) {
    if (!preparations.value[bindingId]?.canValidate) {
      throw new Error('LineUpBindingPrepareRequired')
    }
    await runAction(bindingId, async () => {
      const result = await gateway.validateBinding(bindingId)
      validations.value = { ...validations.value, [bindingId]: result }
      await refreshAfterAction()
    })
  }

  async function setMonitoring(bindingId: EntityId, enabled: boolean) {
    if (enabled && !validations.value[bindingId]?.canEnable) {
      throw new Error('LineUpBindingValidationRequired')
    }
    await runAction(bindingId, async () => {
      await gateway.setMonitoring(bindingId, enabled)
      await refreshAfterAction()
    })
  }

  async function refresh() {
    const subStationId = selectedSubStationId.value
    await Promise.all([
      refreshNavigation(),
      subStationId ? loadBindings(subStationId, false) : Promise.resolve(),
    ])
  }

  async function refreshAfterAction() {
    const subStationId = selectedSubStationId.value
    await Promise.all([
      refreshNavigation(),
      subStationId ? loadBindings(subStationId, false) : Promise.resolve(),
    ])
  }

  async function runAction(bindingId: EntityId, action: () => Promise<void>) {
    actionBusy.value = { ...actionBusy.value, [bindingId]: true }
    errorCode.value = ''
    try {
      await action()
    }
    catch (error) {
      errorCode.value = safeErrorCode(error)
      throw error
    }
    finally {
      actionBusy.value = { ...actionBusy.value, [bindingId]: false }
    }
  }

  const canValidate = (bindingId: EntityId) => preparations.value[bindingId]?.canValidate === true
  const canEnable = (bindingId: EntityId) => validations.value[bindingId]?.canEnable === true

  return {
    navigationNodes,
    selectedSubStationId,
    bindings,
    preparations,
    validations,
    navigationLoading,
    bindingLoading,
    actionBusy,
    errorCode,
    selectedNode,
    selectedPath,
    canValidate,
    canEnable,
    refreshNavigation,
    selectSubStation,
    prepare,
    validate,
    setMonitoring,
    refresh,
  }
}

function without<T>(values: Record<string, T>, key: string) {
  return Object.fromEntries(Object.entries(values).filter(([itemKey]) => itemKey !== key))
}

function safeErrorCode(error: unknown) {
  if (!(error instanceof Error)) {
    return 'LineUpBindingActionFailed'
  }
  return /^[A-Z][A-Z0-9]{2,80}$/i.test(error.message) ? error.message : 'LineUpBindingActionFailed'
}
