<script setup lang="ts">
import type { EditableImageMarker } from './LineUpImageMarkCanvas.vue'
import type { LineUpImageBom, LineUpImageItem, LineUpImageMarker, LineUpImageWorkspace } from '@/api/modules/LineUp/imageMarks'
import type { LineUpMasterItem, LineUpMasterLevel } from '@/api/modules/LineUp/masterData'
import type { EntityId } from '@/api/modules/LineUp/types'
import { ElMessage, ElMessageBox } from 'element-plus'
import { computed, onActivated, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { onBeforeRouteLeave, useRoute } from 'vue-router'
import imageMarkApi from '@/api/modules/LineUp/imageMarks'
import masterDataApi from '@/api/modules/LineUp/masterData'
import { loadImageMarkRouteContext } from './imageMarkNavigation'
import LineUpImageMarkCanvas from './LineUpImageMarkCanvas.vue'
import BomImportDialog from './components/BomImportDialog.vue'
import { serializeMarkers } from './markerGeometry'
import { useI18n } from 'vue-i18n'
import messages from './components/lang'

const MAX_IMAGE_BYTES = 25 * 1024 * 1024
const IMAGE_EXTENSIONS = new Set(['png', 'jpg', 'jpeg', 'webp'])
const route = useRoute()

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}

const lines = ref<LineUpMasterItem[]>([])
const processes = ref<LineUpMasterItem[]>([])
const workStations = ref<LineUpMasterItem[]>([])
const subStations = ref<LineUpMasterItem[]>([])
const lineId = ref<EntityId>('')
const processId = ref<EntityId>('')
const workStationId = ref<EntityId>('')
const subStationId = ref<EntityId>('')
const workspace = ref<LineUpImageWorkspace | null>(null)
const selectedImageId = ref<EntityId>('')
const markers = ref<EditableImageMarker[]>([])
const selectedMarkerKey = ref<string | null>(null)
const selectedBomIds = ref<EntityId[]>([])
const bomKeyword = ref('')
const markerMode = ref<'single' | 'combined'>('single')
const loading = ref(false)
const saving = ref(false)
const uploading = ref(false)
const dirty = ref(false)
const editImageObjectUrl = ref('')
const viewImageObjectUrl = ref('')
const viewMode = ref<'view' | 'edit'>('view')
const rightTab = ref<'bom' | 'markers'>('bom')
const canvasRef = ref<InstanceType<typeof LineUpImageMarkCanvas> | null>(null)
const fileInput = ref<HTMLInputElement | null>(null)

const currentImage = computed(() =>
  workspace.value?.images.find(item => item.id === selectedImageId.value) ?? null)
const selectedMarker = computed(() =>
  markers.value.find(item => item.clientKey === selectedMarkerKey.value) ?? null)
/** 点位归属：绑定当前子工站（本站） or 产线直挂未绑工站（产线池） */
function isCurrentStationBom(bom: LineUpImageBom) {
  return !!bom.stationId && String(bom.stationId) === String(subStationId.value)
}

const filteredBoms = computed(() => {
  const keyword = bomKeyword.value.trim().toLocaleLowerCase()
  const all = workspace.value?.boms ?? []
  const base = keyword
    ? all.filter(item =>
        [item.refNo, item.name, item.tagCode, item.dbAddress]
          .some(value => value?.toLocaleLowerCase().includes(keyword)))
    : [...all]
  // 本子工站已绑定点位优先展示，产线直挂（未绑工站）点位靠后
  return base.sort((a, b) =>
    Number(!isCurrentStationBom(a)) - Number(!isCurrentStationBom(b)))
})
const activeImageUrl = computed(() =>
  viewMode.value === 'view' ? viewImageObjectUrl.value : editImageObjectUrl.value)
/** 图上已有标记的 BOM（勾选状态与其同步）；newBomIds=新勾选待上图。 */
const markedBomIds = computed(() => [...new Set(markers.value.flatMap(marker => marker.bomIds))])
const newBomIds = computed(() => selectedBomIds.value.filter(id => !markedBomIds.value.includes(id)))
// 关联型 BOM（IsAssociated=TRUE）集合：图上标记挂到其中任一时标签边框显示粉红
const associatedBomIds = computed(() =>
  (workspace.value?.boms ?? []).filter(bom => bom.isAssociated).map(bom => bom.id),
)

/** 本图视角的关联 BOM 汇总：当前图上标记挂到的、且 tagid 绑定（IsAssociated）的 BOM */
const imageAssociatedBoms = computed(() => {
  const onImage = new Set(markers.value.flatMap(marker => marker.bomIds ?? []).map(String))
  return (workspace.value?.boms ?? []).filter(bom => bom.isAssociated && onImage.has(String(bom.id)))
})

/** 本图已上图的 BOM（关联多选框的选项池） */
const onImageBoms = computed(() => {
  const onImage = new Set(markers.value.flatMap(marker => marker.bomIds ?? []).map(String))
  return (workspace.value?.boms ?? []).filter(bom => onImage.has(String(bom.id)))
})

/** 关联状态本地补丁：不整页刷新，保留未保存的点位编辑 */
function patchLocalBomAssociation(bomId: EntityId, associated: boolean) {
  const bom = (workspace.value?.boms ?? []).find(item => String(item.id) === String(bomId))
  if (bom) bom.isAssociated = associated
}

/** 关联 BOM 多选框：get=本图关联集合，set=diff 后逐个设为/取消关联 */
const imageAssociatedIds = computed<EntityId[]>({
  get: () => imageAssociatedBoms.value.map(bom => bom.id),
  set: (next) => {
    void applyAssociationChange(next)
  },
})

async function applyAssociationChange(next: EntityId[]) {
  const prevIds = new Set(imageAssociatedBoms.value.map(bom => String(bom.id)))
  const nextIds = new Set(next.map(String))
  try {
    for (const id of nextIds) {
      if (!prevIds.has(id)) {
        await imageMarkApi.setBomAssociation(id, true)
        patchLocalBomAssociation(id, true)
      }
    }
    for (const id of prevIds) {
      if (!nextIds.has(id)) {
        await imageMarkApi.setBomAssociation(id, false)
        patchLocalBomAssociation(id, false)
      }
    }
    ElMessage.success(t('lineupImageMarks_form.associatedUpdated'))
  }
  catch (error) {
    ElMessage.error(error instanceof Error ? error.message : t('lineupImageMarks_form.associatedUpdateFailed'))
  }
}
const bomLabels = computed(() => {
  const map: Record<string, string> = {}
  for (const bom of workspace.value?.boms ?? []) {
    if (bom.id && bom.refNo) {
      map[bom.id] = bom.refNo
    }
  }
  return map
})

async function queryMaster(level: LineUpMasterLevel, parentId?: EntityId) {
  return (await masterDataApi.query(level, parentId, { page: 1, pageSize: 200 })).items
}

async function loadLines() {
  lines.value = await queryMaster('ProductionLine')
}

async function changeLine(value: EntityId) {
  if (!await allowDiscard()) {
    return
  }
  lineId.value = value
  processId.value = ''
  workStationId.value = ''
  subStationId.value = ''
  processes.value = value ? await queryMaster('Process', value) : []
  workStations.value = []
  subStations.value = []
  clearWorkspace()
}

async function changeProcess(value: EntityId) {
  if (!await allowDiscard()) {
    return
  }
  processId.value = value
  workStationId.value = ''
  subStationId.value = ''
  workStations.value = value ? await queryMaster('WorkStation', value) : []
  subStations.value = []
  clearWorkspace()
}

async function changeWorkStation(value: EntityId) {
  if (!await allowDiscard()) {
    return
  }
  workStationId.value = value
  subStationId.value = ''
  subStations.value = value ? await queryMaster('SubStation', value) : []
  clearWorkspace()
}

async function changeSubStation(value: EntityId) {
  if (!await allowDiscard()) {
    return
  }
  subStationId.value = value
  if (value) {
    await loadWorkspace(value)
  }
  else {
    clearWorkspace()
  }
}

async function loadWorkspace(stationId: EntityId, preferredImageId?: EntityId) {
  loading.value = true
  try {
    await applyWorkspace(await imageMarkApi.getStation(stationId), preferredImageId)
  }
  catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'LineUpImageWorkspaceLoadFailed')
    clearWorkspace()
  }
  finally {
    loading.value = false
  }
}

async function applyWorkspace(data: LineUpImageWorkspace, preferredImageId?: EntityId) {
  workspace.value = data
  const preferred = data.images.find(item => item.id === preferredImageId)
  const next = preferred ?? data.images[0] ?? null
  selectedImageId.value = next?.id ?? ''
  loadMarkersForCurrentImage()
  await loadCurrentImageContent()
  dirty.value = false
}

function requestedSubStationId() {
  const value = route.query.subStationId
  return String(Array.isArray(value) ? value[0] ?? '' : value ?? '').trim()
}

async function initializePage() {
  const requestedId = requestedSubStationId()
  if (requestedId && requestedId === subStationId.value && workspace.value) {
    return
  }
  if (!requestedId && lines.value.length > 0) {
    return
  }
  if (!requestedId) {
    await loadLines()
    return
  }

  loading.value = true
  try {
    const context = await loadImageMarkRouteContext(requestedId, {
      getStation: imageMarkApi.getStation,
    })
    lines.value = context.lines
    processes.value = context.processes
    workStations.value = context.workStations
    subStations.value = context.subStations
    lineId.value = context.selection.lineId
    processId.value = context.selection.processId
    workStationId.value = context.selection.workStationId
    subStationId.value = context.selection.subStationId
    await applyWorkspace(context.workspace)
  }
  catch (error) {
    ElMessage.warning(error instanceof Error
      ? t('lineupImageMarks_form.stationLoadFailed', { message: error.message })
      : t('lineupImageMarks_form.stationLoadFailedFallback'))
    await loadLines()
  }
  finally {
    loading.value = false
  }
}

async function selectImage(image: LineUpImageItem) {
  if (image.id === selectedImageId.value) {
    return
  }
  if (!await allowDiscard()) {
    return
  }
  selectedImageId.value = image.id
  loadMarkersForCurrentImage()
  await loadCurrentImageContent()
  dirty.value = false
}

function loadMarkersForCurrentImage() {
  markers.value = (workspace.value?.markers ?? [])
    .filter(marker => marker.imageId === selectedImageId.value)
    .map(toEditable)
  selectedMarkerKey.value = markers.value[0]?.clientKey ?? null
  // BOM 勾选状态反映图上现状：已上图的 BOM 默认勾选
  selectedBomIds.value = [...new Set(markers.value.flatMap(marker => marker.bomIds))]
}

function toEditable(marker: LineUpImageMarker): EditableImageMarker {
  return {
    clientKey: `server-${marker.id}`,
    id: marker.id,
    markType: marker.markType,
    isSingleMark: marker.isSingleMark,
    point: marker.point ? { ...marker.point } : null,
    billboard: marker.billboard ? { ...marker.billboard } : null,
    line: marker.line ? { ...marker.line } : null,
    bomIds: [...marker.bomIds],
  }
}

async function loadCurrentImageContent() {
  releaseImageUrls()
  // 成品图（照片+标签合成，查看态铺满显示）；原始图（编辑态打标底图）
  const renderedUrl = currentImage.value?.currentContentUrl
  const originalUrl = currentImage.value?.contentUrl || currentImage.value?.currentContentUrl
  if (renderedUrl) {
    try {
      const blob = await imageMarkApi.getContent(renderedUrl)
      viewImageObjectUrl.value = URL.createObjectURL(blob)
    }
    catch {
      // 成品图缺失时回退编辑态
    }
  }
  if (originalUrl) {
    try {
      const blob = await imageMarkApi.getContent(originalUrl)
      editImageObjectUrl.value = URL.createObjectURL(blob)
    }
    catch {
      ElMessage.warning(t('lineupImageMarks_form.imageFileMissing'))
    }
  }
  viewMode.value = viewImageObjectUrl.value ? 'view' : 'edit'
}

function addMarkers() {
  const image = currentImage.value
  if (!image || !image.pixelWidth || !image.pixelHeight) {
    ElMessage.warning(t('lineupImageMarks_form.selectValidImageFirst'))
    return
  }
  if (selectedBomIds.value.length === 0) {
    ElMessage.warning(t('lineupImageMarks_form.selectBomFirst'))
    return
  }
  const pending = newBomIds.value
  if (!pending.length) {
    ElMessage.warning(t('lineupImageMarks_form.allBomsAlreadyMarked'))
    return
  }

  const groups = markerMode.value === 'single'
    ? pending.map(id => [id])
    : [pending.slice()]
  createMarkersFor(groups)
}

function createMarkersFor(groups: EntityId[][]) {
  const image = currentImage.value
  if (!image || !image.pixelWidth || !image.pixelHeight) {
    return
  }
  groups.forEach((bomIds, index) => {
    const key = `local-${Date.now()}-${index}`
    const x = Math.max(20, image.pixelWidth! / 2 - 100 + index * 18)
    const y = Math.max(20, image.pixelHeight! / 2 - 30 + index * 18)
    const label = bomIds.map(id => workspace.value?.boms.find(item => item.id === id)?.refNo || id).join(' + ')
    // 标签自动排在图片上边缘外（不遮挡照片内容），避让布局会自动错开重叠
    const billboardX = Math.min(Math.max(0, x - 60), Math.max(0, image.pixelWidth! - 120))
    markers.value.push({
      clientKey: key,
      id: null,
      markType: 'PointBillboard',
      isSingleMark: markerMode.value === 'single',
      point: { id: `${key}-point`, x, y, description: label },
      billboard: {
        id: `${key}-billboard`,
        x: billboardX,
        y: -30,
        width: 120,
        height: 20,
        text: label,
        description: '',
        combinedBomId: markerMode.value === 'combined' ? bomIds.join(',') : null,
        backgroundColor: '#0c2539',
        borderColor: '#24d4c3',
      },
      line: {
        id: `${key}-line`,
        startX: billboardX,
        startY: -20,
        endX: x,
        endY: y,
        markId: `${key}-point`,
        billboardId: `${key}-billboard`,
        combinedBomId: markerMode.value === 'combined' ? bomIds.join(',') : null,
      },
      bomIds,
    })
    selectedMarkerKey.value = key
  })
  rightTab.value = 'markers'
  dirty.value = true
}

/** 点位清单管理：新增/编辑/删除子工站下的点位（BOM）并绑定标签编码。 */
const bomDialog = ref(false)
const bomImportVisible = ref(false)

async function onBomImported() {
  if (subStationId.value) {
    await loadWorkspace(subStationId.value)
  }
}
const bomSaving = ref(false)
const editingBomId = ref<EntityId | null>(null)
const bomForm = reactive({ refNo: '', tagCode: '', name: '', dbAddress: '', modelNo: '', brand: '', ioAddress: '', partNo: '', plcId: '' as EntityId | '' })
const plcDevices = ref<Array<{ id: EntityId, name: string | null, ip: string | null }>>([])
const plcLoading = ref(false)

async function loadPlcDevices() {
  const lineId = workspace.value?.subStation.productionLineId
  if (!lineId) return
  plcLoading.value = true
  try {
    plcDevices.value = await imageMarkApi.listPlcs(lineId)
  }
  catch {
    plcDevices.value = []
  }
  finally {
    plcLoading.value = false
  }
}

function openBomCreate() {
  editingBomId.value = null
  bomForm.refNo = ''
  bomForm.tagCode = ''
  bomForm.name = ''
  bomForm.dbAddress = ''
  bomForm.modelNo = ''
  bomForm.brand = ''
  bomForm.ioAddress = ''
  bomForm.partNo = ''
  bomForm.plcId = ''
  void loadPlcDevices()
  bomDialog.value = true
}

function openBomEdit(bom: LineUpImageBom) {
  editingBomId.value = bom.id
  bomForm.refNo = bom.refNo || ''
  bomForm.tagCode = bom.tagCode || ''
  bomForm.name = bom.name || ''
  bomForm.dbAddress = bom.dbAddress || ''
  bomForm.modelNo = bom.modelNo || ''
  bomForm.brand = bom.brand || ''
  bomForm.ioAddress = bom.ioAddress || ''
  bomForm.partNo = bom.partNo || ''
  bomForm.plcId = bom.plcId || ''
  void loadPlcDevices()
  bomDialog.value = true
}

async function submitBom() {
  if (!subStationId.value) {
    ElMessage.warning(t('lineupImageMarks_form.selectSubStationFirst'))
    return
  }
  if (!bomForm.refNo.trim() && !bomForm.tagCode.trim()) {
    ElMessage.warning(t('lineupImageMarks_form.refNoOrTagCodeRequired'))
    return
  }
  bomSaving.value = true
  try {
    const payload = {
      refNo: bomForm.refNo.trim() || null,
      tagCode: bomForm.tagCode.trim() || null,
      name: bomForm.name.trim() || null,
      dbAddress: bomForm.dbAddress.trim() || null,
      modelNo: bomForm.modelNo.trim() || null,
      brand: bomForm.brand.trim() || null,
      ioAddress: bomForm.ioAddress.trim() || null,
      partNo: bomForm.partNo.trim() || null,
      plcId: bomForm.plcId || null,
    }
    if (editingBomId.value) {
      const updated = await imageMarkApi.updateBom(editingBomId.value, payload)
      const index = workspace.value?.boms.findIndex(item => item.id === editingBomId.value) ?? -1
      if (index >= 0 && workspace.value) {
        workspace.value.boms.splice(index, 1, updated)
      }
      ElMessage.success(t('lineupImageMarks_form.bomUpdated'))
    }
    else {
      const created = await imageMarkApi.createBom(subStationId.value, payload)
      workspace.value?.boms.push(created)
      ElMessage.success(t('lineupImageMarks_form.bomCreated'))
    }
    bomDialog.value = false
  }
  catch (error) {
    ElMessage.error(error instanceof Error ? error.message : t('lineupImageMarks_form.bomSaveFailed'))
  }
  finally {
    bomSaving.value = false
  }
}

async function removeBomRow(bom: LineUpImageBom) {
  try {
    await ElMessageBox.confirm(t('lineupImageMarks_form.confirmDeleteBom', { name: bom.refNo || bom.tagCode || bom.id }), t('lineupImageMarks_form.deleteBomTitle'), { type: 'warning' })
  }
  catch {
    return
  }
  try {
    await imageMarkApi.removeBom(bom.id)
    const index = workspace.value?.boms.findIndex(item => item.id === bom.id) ?? -1
    if (index >= 0 && workspace.value) {
      workspace.value.boms.splice(index, 1)
    }
    selectedBomIds.value = selectedBomIds.value.filter(id => id !== bom.id)
    ElMessage.success(t('lineupImageMarks_form.bomDeleted'))
  }
  catch (error) {
    ElMessage.error(error instanceof Error ? error.message : t('lineupImageMarks_form.bomDeleteFailed'))
  }
}

/** 点位编码：优先标签标题，否则解析关联 BOM 的 RefNo（不展示原始 id）。 */
function markerCodeText(marker: EditableImageMarker, index: number) {
  const code = marker.billboard?.text
    || marker.bomIds
      .map(id => workspace.value?.boms.find(bom => bom.id === id)?.refNo)
      .filter(Boolean)
      .join(' + ')
  return code || t('lineupImageMarks_form.markerFallback', { index: index + 1 })
}

function handleBomSelectionChange(ids: string[]) {
  const selected = new Set(ids)
  const removed = markedBomIds.value.filter(id => !selected.has(id))
  if (!removed.length) {
    return
  }
  const removedSet = new Set(removed)
  markers.value = markers.value
    .map(marker => ({ ...marker, bomIds: marker.bomIds.filter(id => !removedSet.has(id)) }))
    .filter(marker => marker.bomIds.length > 0)
  if (selectedMarkerKey.value && !markers.value.some(marker => marker.clientKey === selectedMarkerKey.value)) {
    selectedMarkerKey.value = markers.value[0]?.clientKey ?? null
  }
  dirty.value = true
  ElMessage.success(t('lineupImageMarks_form.removedFromImage', { count: removed.length }))
  addBackCheckedBoms(ids)
}

// 重新勾选时（单独点位模式）立即加回图上，与取消勾选对称；组合点位仍用「添加到图片」批量提交
function addBackCheckedBoms(ids: string[]) {
  if (markerMode.value !== 'single') {
    return
  }
  const added = ids.filter(id => !markedBomIds.value.includes(id))
  if (added.length) {
    createMarkersFor(added.map(id => [id]))
  }
}

function handleCanvasSelect(clientKey: string) {
  selectedMarkerKey.value = clientKey
  rightTab.value = 'markers'
}

function updateMarker(marker: EditableImageMarker) {
  const index = markers.value.findIndex(item => item.clientKey === marker.clientKey)
  if (index < 0) {
    return
  }
  markers.value[index] = marker
  dirty.value = true
}

function updateSelectedMarker() {
  dirty.value = true
}

function removeSelectedMarker() {
  if (!selectedMarkerKey.value) {
    return
  }
  markers.value = markers.value.filter(item => item.clientKey !== selectedMarkerKey.value)
  selectedMarkerKey.value = markers.value[0]?.clientKey ?? null
  dirty.value = true
}

async function save() {
  const image = currentImage.value
  if (!image) {
    return
  }
  saving.value = true
  try {
    const payload = markers.value.map(({ clientKey: _, ...marker }) => marker)
    // 编辑态保存时合成成品图（照片+标签+连线）一并上传，查看态直接展示成品
    const rendered = canvasRef.value ? await canvasRef.value.exportRenderedImage() : null
    const result = await imageMarkApi.save(image.id, image.rowVersion, serializeMarkers(payload), rendered ?? undefined)
    image.rowVersion = result.rowVersion
    image.currentContentUrl = result.currentContentUrl
    dirty.value = false
    ElMessage.success(t('lineupImageMarks_form.marksSaved'))
    await loadWorkspace(subStationId.value, image.id)
    await loadCurrentImageContent()
  }
  catch (error) {
    const message = error instanceof Error ? error.message : String(error ?? '')
    // 版本冲突（其他会话保存过，如自动化重合成成品图）：静默同步最新版本号，保留未保存的拖拽改动，提示重存
    if (/VersionConflict|changed by another user/i.test(message)) {
      try {
        const fresh = await imageMarkApi.getStation(subStationId.value)
        workspace.value = fresh
        const current = fresh.images.find(item => item.id === image.id)
        if (current) {
          image.rowVersion = current.rowVersion
          image.currentContentUrl = current.currentContentUrl
        }
        ElMessage.warning(t('lineupImageMarks_form.versionConflict'))
        return
      }
      catch {
        // 同步失败退回普通报错
      }
    }
    ElMessage.error(error instanceof Error ? error.message : 'LineUpImageMarkSaveFailed')
  }
  finally {
    saving.value = false
  }
}

function chooseUpload() {
  fileInput.value?.click()
}

async function uploadImage(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file || !subStationId.value) {
    return
  }
  const extension = file.name.split('.').pop()?.toLocaleLowerCase() ?? ''
  if (!IMAGE_EXTENSIONS.has(extension) || file.size > MAX_IMAGE_BYTES) {
    ElMessage.warning(t('lineupImageMarks_form.invalidImageFile'))
    return
  }
  uploading.value = true
  try {
    const created = await imageMarkApi.upload(subStationId.value, file)
    ElMessage.success(t('lineupImageMarks_form.imageUploaded'))
    await loadWorkspace(subStationId.value, created.id)
  }
  catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'LineUpImageUploadFailed')
  }
  finally {
    uploading.value = false
  }
}

async function deleteImage() {
  const image = currentImage.value
  if (!image) {
    return
  }
  try {
    await ElMessageBox.confirm(t('lineupImageMarks_form.confirmDeleteImage', { name: image.pictureName || image.id }), t('lineupImageMarks_form.deleteImageTitle'), {
      type: 'warning',
    })
    await imageMarkApi.remove(image.id)
    ElMessage.success(t('lineupImageMarks_form.imageDeleted'))
    await loadWorkspace(subStationId.value)
  }
  catch (error) {
    if (error !== 'cancel' && error !== 'close') {
      ElMessage.error(error instanceof Error ? error.message : 'LineUpImageDeleteFailed')
    }
  }
}

async function allowDiscard(): Promise<boolean> {
  if (!dirty.value) {
    return true
  }
  try {
    await ElMessageBox.confirm(t('lineupImageMarks_form.discardTip'), t('lineupImageMarks_form.discardTitle'), {
      confirmButtonText: t('lineupImageMarks_form.discardAndContinue'),
      cancelButtonText: t('lineupImageMarks_form.stayHere'),
      type: 'warning',
    })
    dirty.value = false
    return true
  }
  catch {
    return false
  }
}

function clearWorkspace() {
  workspace.value = null
  selectedImageId.value = ''
  markers.value = []
  selectedMarkerKey.value = null
  dirty.value = false
  releaseImageUrls()
}

function releaseImageUrls() {
  for (const url of [viewImageObjectUrl.value, editImageObjectUrl.value]) {
    if (url) {
      URL.revokeObjectURL(url)
    }
  }
  viewImageObjectUrl.value = ''
  editImageObjectUrl.value = ''
}

function handleBeforeUnload(event: BeforeUnloadEvent) {
  if (!dirty.value) {
    return
  }
  event.preventDefault()
  event.returnValue = ''
}

onBeforeRouteLeave(async () => await allowDiscard())
onMounted(() => {
  window.addEventListener('beforeunload', handleBeforeUnload)
  void initializePage().catch(() => {
    ElMessage.error(t('lineupImageMarks_form.initLoadFailed'))
  })
})
onActivated(() => {
  void initializePage().catch(() => {
    ElMessage.error(t('lineupImageMarks_form.initLoadFailed'))
  })
})
onBeforeUnmount(() => {
  window.removeEventListener('beforeunload', handleBeforeUnload)
  releaseImageUrls()
})
</script>

<template>
  <main v-loading="loading" class="image-mark-page">
    <header class="page-heading">
      <div>
        <span>LINEUP / BOM IMAGE MARKER</span>
        <h2>{{ $t('lineupImageMarks_form.pageTitle') }}</h2>
        <p>{{ $t('lineupImageMarks_form.pageIntro') }}</p>
      </div>
      <div class="heading-actions">
        <el-button type="primary" plain @click="bomImportVisible = true">
          {{ $t('lineupImageMarks_import.openButton') }}
        </el-button>
        <el-radio-group v-model="viewMode" size="small">
          <el-radio-button value="view" :disabled="!viewImageObjectUrl">
            {{ $t('lineupImageMarks_form.renderedView') }}
          </el-radio-button>
          <el-radio-button value="edit">
            {{ $t('lineupImageMarks_form.editMarks') }}
          </el-radio-button>
        </el-radio-group>
        <el-tag :type="dirty ? 'warning' : 'success'">
          {{ dirty ? $t('lineupImageMarks_form.unsavedChanges') : $t('lineupImageMarks_form.synced') }}
        </el-tag>
        <el-button type="primary" :loading="saving" :disabled="!currentImage || !dirty" @click="save">
          {{ $t('lineupImageMarks_form.saveMarks') }}
        </el-button>
      </div>
    </header>

    <section class="hierarchy-bar">
      <el-select :model-value="lineId" :placeholder="$t('lineupImageMarks_form.selectLine')" filterable @change="changeLine">
        <el-option v-for="item in lines" :key="item.id" :label="`${item.code || ''} ${item.name || ''}`" :value="item.id" />
      </el-select>
      <span>›</span>
      <el-select :model-value="processId" :placeholder="$t('lineupImageMarks_form.selectProcess')" filterable :disabled="!lineId" @change="changeProcess">
        <el-option v-for="item in processes" :key="item.id" :label="`${item.code || ''} ${item.name || ''}`" :value="item.id" />
      </el-select>
      <span>›</span>
      <el-select :model-value="workStationId" :placeholder="$t('lineupImageMarks_form.selectWorkStation')" filterable :disabled="!processId" @change="changeWorkStation">
        <el-option v-for="item in workStations" :key="item.id" :label="`${item.code || ''} ${item.name || ''}`" :value="item.id" />
      </el-select>
      <span>›</span>
      <el-select :model-value="subStationId" :placeholder="$t('lineupImageMarks_form.selectSubStation')" filterable :disabled="!workStationId" @change="changeSubStation">
        <el-option v-for="item in subStations" :key="item.id" :label="`${item.code || ''} ${item.name || ''}`" :value="item.id" />
      </el-select>
    </section>

    <section v-if="workspace" class="editor-grid">
      <aside class="image-panel panel">
        <div class="panel-title">
          <div>
            <strong>{{ $t('lineupImageMarks_form.stationImages') }}</strong>
            <span>{{ $t('lineupImageMarks_form.imageCount', { count: workspace.images.length }) }}</span>
          </div>
          <el-button type="primary" link :loading="uploading" @click="chooseUpload">
            {{ $t('lineupImageMarks_form.uploadImageBtn') }}
          </el-button>
          <input ref="fileInput" class="file-input" type="file" accept=".png,.jpg,.jpeg,.webp" @change="uploadImage">
        </div>
        <div v-if="workspace.images.length" class="image-list">
          <button
            v-for="image in workspace.images"
            :key="image.id"
            type="button"
            class="image-item"
            :class="{ active: image.id === selectedImageId }"
            @click="selectImage(image)"
          >
            <span>{{ image.pictureName || image.id }}</span>
            <small>{{ image.pixelWidth || '?' }} × {{ image.pixelHeight || '?' }} · v{{ image.rowVersion }}</small>
          </button>
        </div>
        <el-empty v-else :description="$t('lineupImageMarks_form.noImagesYet')" :image-size="72" />
        <el-button v-if="currentImage" class="delete-image" type="danger" plain @click="deleteImage">
          {{ $t('lineupImageMarks_form.deleteCurrentImage') }}
        </el-button>
      </aside>

      <section class="canvas-panel">
        <LineUpImageMarkCanvas
          v-if="currentImage && activeImageUrl && currentImage.pixelWidth && currentImage.pixelHeight"
          ref="canvasRef"
          :image-url="activeImageUrl"
          :image-width="currentImage.pixelWidth"
          :image-height="currentImage.pixelHeight"
          :markers="viewMode === 'view' ? [] : markers"
          :bom-labels="bomLabels"
      :associated-bom-ids="associatedBomIds"
          :fit-mode="viewMode === 'view' ? 'fill' : 'default'"
          :selected-marker-key="viewMode === 'view' ? null : selectedMarkerKey"
          @select="handleCanvasSelect"
          @update="updateMarker"
        />
        <div v-else class="canvas-empty">
          <strong>{{ currentImage ? $t('lineupImageMarks_form.imageUnavailable') : $t('lineupImageMarks_form.selectOrUploadImage') }}</strong>
          <span>{{ $t('lineupImageMarks_form.canvasEmptyTip') }}</span>
        </div>
      </section>

      <aside class="property-panel panel">
        <el-tabs v-model="rightTab" class="property-tabs">
          <el-tab-pane name="bom">
            <template #label>
              {{ $t('lineupImageMarks_form.bomListTab', { count: workspace.boms.length }) }}
            </template>
            <div class="bom-toolbar">
              <el-button type="primary" size="small" :disabled="!subStationId" @click="openBomCreate">
                {{ $t('lineupImageMarks_form.addBom') }}
              </el-button>
              <span class="bom-toolbar-tip">{{ $t('lineupImageMarks_form.bomToolbarTip') }}</span>
            </div>
            <el-input v-model="bomKeyword" clearable :placeholder="$t('lineupImageMarks_form.searchBomPlaceholder')" />
            <el-radio-group v-model="markerMode" class="marker-mode">
              <el-radio-button value="single">
                {{ $t('lineupImageMarks_form.singleMark') }}
              </el-radio-button>
              <el-radio-button value="combined">
                {{ $t('lineupImageMarks_form.combinedMark') }}
              </el-radio-button>
            </el-radio-group>
            <el-checkbox-group v-model="selectedBomIds" class="bom-list" @change="handleBomSelectionChange">
              <el-checkbox v-for="bom in filteredBoms" :key="bom.id" :value="bom.id">
                <div class="bom-row">
                  <div class="bom-text">
                    <span class="bom-code">
                      {{ bom.refNo || bom.id }}
                      <el-tag
                        :type="isCurrentStationBom(bom) ? 'success' : 'info'"
                        size="small"
                        effect="plain"
                        class="bom-scope-tag"
                      >
                        {{ isCurrentStationBom(bom) ? $t('lineupImageMarks_form.currentStationTag') : $t('lineupImageMarks_form.linePoolTag') }}
                      </el-tag>
                    </span>
                    <small>{{ bom.tagCode || bom.dbAddress || bom.name || $t('lineupImageMarks_form.noTagAddress') }}</small>
                  </div>
                  <span class="bom-actions" @click.stop>
                    <el-button link size="small" @click="openBomEdit(bom)">
                      {{ $t('common.edit') }}
                    </el-button>
                    <el-button link size="small" type="danger" @click="removeBomRow(bom)">
                      {{ $t('common.delete') }}
                    </el-button>
                  </span>
                </div>
              </el-checkbox>
            </el-checkbox-group>
            <el-button type="primary" plain :disabled="!currentImage || newBomIds.length === 0" @click="addMarkers">
              {{ $t('lineupImageMarks_form.addToImage', { count: newBomIds.length }) }}
            </el-button>
          </el-tab-pane>

          <el-tab-pane name="markers">
            <template #label>
              {{ $t('lineupImageMarks_form.markersTab', { count: markers.length }) }}
            </template>
            <div class="marker-list-title">
              <span>{{ $t('lineupImageMarks_form.currentImageMarks') }}</span>
              <strong>{{ markers.length }}</strong>
            </div>
            <div class="marker-list">
              <button
                v-for="(marker, index) in markers"
                :key="marker.clientKey"
                type="button"
                :class="{ active: marker.clientKey === selectedMarkerKey }"
                @click="selectedMarkerKey = marker.clientKey"
              >
                <span class="marker-code">{{ markerCodeText(marker, index) }}</span>
                <small>#{{ index + 1 }} · {{ marker.isSingleMark ? $t('lineupImageMarks_form.singleMark') : $t('lineupImageMarks_form.combinedMark') }}</small>
              </button>
              <el-empty v-if="!markers.length" :description="$t('lineupImageMarks_form.noMarkersYet')" :image-size="60" />
            </div>

            <div v-if="selectedMarker && selectedMarker.billboard" class="marker-form">
              <el-divider content-position="left">
                {{ $t('lineupImageMarks_form.markerProps') }}
              </el-divider>
              <label>{{ $t('lineupImageMarks_form.associatedBomLabel') }}</label>
              <el-select
                v-model="imageAssociatedIds"
                multiple
                filterable
                collapse-tags
                collapse-tags-tooltip
                :placeholder="$t('lineupImageMarks_form.associatedPlaceholder')"
              >
                <el-option
                  v-for="bom in onImageBoms"
                  :key="bom.id"
                  :label="bom.tagCode ? `${bom.refNo || bom.id} · ${bom.tagCode}` : (bom.refNo || bom.id)"
                  :value="bom.id"
                />
              </el-select>
              <label>{{ $t('lineupImageMarks_form.billboardTitle') }}</label>
              <el-input v-model="selectedMarker.billboard.text" maxlength="200" @input="updateSelectedMarker" />
              <label>{{ $t('lineupImageMarks_form.siteDescription') }}</label>
              <el-input v-model="selectedMarker.billboard.description" type="textarea" maxlength="500" show-word-limit @input="updateSelectedMarker" />
              <el-button type="danger" plain @click="removeSelectedMarker">
                {{ $t('lineupImageMarks_form.deleteThisMarker') }}
              </el-button>
            </div>
            <el-empty
              v-else-if="markers.length"
              :description="$t('lineupImageMarks_form.selectMarkerFirst')"
              :image-size="60"
            />
          </el-tab-pane>
        </el-tabs>
      </aside>
    </section>

    <section v-else class="selection-empty">
      <strong>{{ $t('lineupImageMarks_form.selectHierarchyFirst') }}</strong>
      <span>{{ $t('lineupImageMarks_form.selectHierarchyTip') }}</span>
    </section>
    <el-dialog
      v-model="bomDialog"
      :title="editingBomId ? $t('lineupImageMarks_form.editBomTitle') : $t('lineupImageMarks_form.addBom')"
      width="440px"
      append-to-body
    >
      <div class="bom-form">
        <div v-if="workspace" class="bom-context">
          <span>{{ $t('lineupImageMarks_form.ctxLine', { name: workspace.subStation.productionLineName || '-' }) }}</span>
          <span>{{ $t('lineupImageMarks_form.ctxProcess', { name: workspace.subStation.processName || '-' }) }}</span>
          <span>{{ $t('lineupImageMarks_form.ctxStationCode', { name: workspace.subStation.code || '-' }) }}</span>
        </div>
        <label>{{ $t('lineupImageMarks_form.refNoLabel') }}</label>
        <el-input v-model="bomForm.refNo" :placeholder="$t('lineupImageMarks_form.refNoPlaceholder')" maxlength="100" />
        <label>{{ $t('lineupImageMarks_form.nameLabel') }}</label>
        <el-input v-model="bomForm.name" maxlength="200" />
        <label>{{ $t('lineupImageMarks_form.plcDeviceLabel') }}</label>
        <el-select v-model="bomForm.plcId" :loading="plcLoading" clearable filterable :placeholder="$t('lineupImageMarks_form.selectPlcDevice')">
          <el-option
            v-for="device in plcDevices"
            :key="device.id"
            :label="device.ip ? $t('lineupImageMarks_form.deviceWithIp', { name: device.name, ip: device.ip }) : (device.name || String(device.id))"
            :value="device.id"
          />
        </el-select>
        <label>{{ $t('lineupImageMarks_form.modelLabel') }}</label>
        <el-input v-model="bomForm.modelNo" maxlength="100" />
        <label>{{ $t('lineupImageMarks_form.brandLabel') }}</label>
        <el-input v-model="bomForm.brand" maxlength="100" />
        <label>{{ $t('lineupImageMarks_form.ioAddressLabel') }}</label>
        <el-input v-model="bomForm.ioAddress" maxlength="200" />
        <label>{{ $t('lineupImageMarks_form.partNoLabel') }}</label>
        <el-input v-model="bomForm.partNo" maxlength="100" />
        <label>{{ $t('lineupImageMarks_form.tagCodeLabel') }}</label>
        <el-input v-model="bomForm.tagCode" :placeholder="$t('lineupImageMarks_form.tagCodePlaceholder')" maxlength="100" />
      </div>
      <template #footer>
        <el-button @click="bomDialog = false">
          {{ $t('common.cancel') }}
        </el-button>
        <el-button type="primary" :loading="bomSaving" @click="submitBom">
          {{ $t('common.save') }}
        </el-button>
      </template>
    </el-dialog>

    <BomImportDialog
      v-model:visible="bomImportVisible"
      :lines="lines"
      :default-line-id="lineId"
      @imported="onBomImported"
    />
  </main>
</template>

<style scoped lang="scss">
.image-mark-page {
  display: flex;
  flex-direction: column;
  height: calc(100vh - 112px);
  padding: 18px;
  color: var(--el-text-color-primary);
  background: var(--el-bg-color-page);
}

.page-heading,
.hierarchy-bar,
.panel,
.canvas-panel,
.selection-empty {
  background: var(--el-bg-color);
  border: 1px solid var(--el-border-color-lighter);
}

.page-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 18px 22px;
  border-radius: 12px 12px 0 0;

  span,
  p {
    font-size: 12px;
    color: var(--el-text-color-secondary);
  }

  h2 {
    margin: 4px 0;
    font-size: 22px;
    color: var(--el-text-color-primary);
  }

  p {
    margin: 0;
  }
}

.heading-actions,
.hierarchy-bar {
  display: flex;
  gap: 12px;
  align-items: center;
}

.hierarchy-bar {
  padding: 12px 18px;
  border-top: 0;
  border-radius: 0 0 12px 12px;

  .el-select {
    width: min(230px, 22%);
  }

  > span {
    color: var(--el-text-color-secondary);
  }
}

.editor-grid {
  display: grid;
  grid-template-columns: 220px minmax(480px, 1fr) 320px;
  gap: 12px;
  flex: 1;
  min-height: 480px;
  margin-top: 14px;
}

.panel,
.canvas-panel {
  min-width: 0;
  overflow: hidden;
  border-radius: 10px;
}

.canvas-panel {
  display: flex;
  flex-direction: column;
}

.canvas-panel > .marker-canvas-shell {
  flex: 1;
}

.panel {
  display: flex;
  flex-direction: column;
  padding: 14px;
}

.property-tabs {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;

  > :deep(.el-tabs__header) {
    margin-bottom: 10px;
  }

  :deep(.el-tabs__item) {
    color: var(--el-text-color-primary);

    &:hover {
      color: var(--el-text-color-primary);
    }

    &.is-active {
      color: var(--el-color-primary);
    }
  }

  > :deep(.el-tabs__content) {
    display: flex;
    flex: 1;
    min-height: 0;
    overflow: hidden;
  }

  .el-tab-pane {
    display: flex;
    flex-direction: column;
    gap: 10px;
    width: 100%;
    min-height: 0;
    overflow: auto;
  }
}

.panel-title {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 12px;

  div {
    display: grid;
    gap: 2px;
  }

  span {
    font-size: 11px;
    color: var(--el-text-color-secondary);
  }
}

.file-input {
  display: none;
}

.image-list,
.marker-list {
  display: grid;
  gap: 8px;
  overflow: auto;
}

.image-list {
  flex: 1;
}

.image-item,
.marker-list button {
  display: grid;
  gap: 5px;
  width: 100%;
  padding: 12px;
  color: var(--el-text-color-regular);
  text-align: left;
  cursor: pointer;
  background: var(--el-fill-color-light);
  border: 1px solid var(--el-border-color-light);
  border-radius: 7px;

  small {
    overflow: hidden;
    text-overflow: ellipsis;
    color: var(--el-text-color-secondary);
  }

  &.active {
    color: var(--el-color-primary);
    background: var(--el-color-primary-light-9);
    border-color: var(--el-color-primary);
  }
}

.delete-image {
  margin-top: 12px;
}

.canvas-panel {
  display: grid;
  padding: 8px;
  background: var(--el-bg-color);
}

.canvas-empty,
.selection-empty {
  display: grid;
  gap: 8px;
  place-content: center;
  color: var(--el-text-color-secondary);
  text-align: center;
}

.canvas-empty {
  min-height: 520px;
}

.selection-empty {
  min-height: 380px;
  margin-top: 14px;
  border-radius: 12px;
}

.marker-mode {
  margin: 0;
}

.bom-list {
  display: grid;
  gap: 4px;
  flex: 1;
  align-content: start;
  min-height: 120px;
  margin: 0;
  overflow: auto;

  :deep(.el-checkbox) {
    display: flex;
    align-items: center;
    height: auto;
    padding: 9px 6px;
    margin: 0;
  }

  :deep(.el-checkbox__label) {
    display: flex;
    width: 100%;
    min-width: 0;
  }

  small {
    overflow: hidden;
    text-overflow: ellipsis;
    font-size: 11px;
    color: var(--el-text-color-secondary);
  }
}

.bom-row {
  display: flex;
  gap: 8px;
  align-items: center;
  justify-content: space-between;
  width: 100%;
  min-width: 0;
}

.bom-text {
  display: grid;
  gap: 3px;
  min-width: 0;
}

.bom-code {
  overflow: hidden;
  font-size: 13px;
  font-weight: 600;
  color: var(--el-text-color-primary);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.marker-list-title {
  display: flex;
  justify-content: space-between;
  margin: 0;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.marker-list {
  flex: 1;
  min-height: 100px;
  margin: 0;
}

.marker-code {
  overflow: hidden;
  font-size: 14px;
  font-weight: 600;
  color: var(--el-text-color-primary);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.property-tabs .el-tab-pane > .el-button {
  flex-shrink: 0;
}

.marker-form {
  display: grid;
  gap: 8px;

  label {
    font-size: 12px;
    color: var(--el-text-color-secondary);
  }
}

.bom-toolbar {
  display: flex;
  flex-shrink: 0;
  gap: 8px;
  align-items: center;
}

.bom-toolbar-tip {
  font-size: 11px;
  color: var(--el-text-color-secondary);
}

.bom-actions {
  display: flex;
  flex-shrink: 0;
  gap: 2px;
}

.bom-form {
  display: grid;
  gap: 8px;

  label {
    font-size: 12px;
    color: var(--el-text-color-secondary);
  }
}

.bom-context {
  display: flex;
  flex-wrap: wrap;
  gap: 6px 14px;
  padding: 8px 10px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
  background: var(--el-fill-color-light);
  border: 1px solid var(--el-border-color-light);
  border-radius: 7px;
}

@media (width <= 1280px) {
  .editor-grid {
    grid-template-columns: 190px minmax(420px, 1fr) 280px;
  }
}
</style>
