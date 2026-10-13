import type { LineUpBusinessGateway, LineUpBusinessItem, LineUpBusinessKind, LineUpBusinessWrite } from '@/api/modules/LineUp/businessConfig'
import type { EntityId } from '@/api/modules/LineUp/types'
import { ref } from 'vue'
import defaultGateway from '@/api/modules/LineUp/businessConfig'

export function createLineUpBusinessConfigState(gateway: LineUpBusinessGateway = defaultGateway) {
  const selectedSubStationId = ref<EntityId>('')
  const selectedActionId = ref<EntityId>('')
  const records = ref<Record<LineUpBusinessKind, LineUpBusinessItem[]>>({ Action: [], Condition: [], Solution: [] })
  const loading = ref(false)

  async function load(kind: LineUpBusinessKind) {
    const parentId = kind === 'Action' ? selectedSubStationId.value : selectedActionId.value
    if (!parentId) {
      records.value[kind] = []
      return
    }
    loading.value = true
    try {
      records.value[kind] = (await gateway.query(kind, parentId, { page: 1, pageSize: 100 })).items
    }
    finally {
      loading.value = false
    }
  }

  function selectSubStation(id: EntityId) {
    selectedSubStationId.value = id
    selectedActionId.value = ''
    records.value = { Action: [], Condition: [], Solution: [] }
  }

  function selectAction(id: EntityId) {
    selectedActionId.value = id
    records.value.Condition = []
    records.value.Solution = []
  }

  async function save(kind: LineUpBusinessKind, request: LineUpBusinessWrite) {
    if (request.id) {
      await gateway.update(kind, request.id, request)
    }
    else {
      await gateway.create(kind, request)
    }
    await load(kind)
  }

  async function remove(kind: LineUpBusinessKind, id: EntityId) {
    await gateway.remove(kind, id)
    await load(kind)
  }

  return { selectedSubStationId, selectedActionId, records, loading, load, selectSubStation, selectAction, save, remove }
}
