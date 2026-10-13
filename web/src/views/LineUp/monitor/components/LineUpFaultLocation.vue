<script setup lang="ts">
import type { TiaGraphSelection } from '../../components/tiaGraphSelection'
import type { LineUpImageItem, LineUpImageMarker, LineUpImageWorkspace } from '@/api/modules/LineUp/imageMarks'
import type { LineUpConditionRuntimeValue } from '@/api/modules/LineUp/monitor'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import imageMarkApi from '@/api/modules/LineUp/imageMarks'
import { describeMonitorBomLocation, plcReferencesForMonitorSelection, projectBomForMonitorSelection } from '../monitorProjection'
import { useI18n } from 'vue-i18n'
import messages from './lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}

const props = withDefaults(defineProps<{
  selection?: TiaGraphSelection | null
  conditions?: LineUpConditionRuntimeValue[]
  subStationId?: string
}>(), {
  selection: null,
  conditions: () => [],
  subStationId: '',
})

const workspace = ref<LineUpImageWorkspace | null>(null)
const selectedImageId = ref('')
const imageUrl = ref('')
const loading = ref(false)
const loaded = ref(false)
const issueCode = ref('')
let workspaceGeneration = 0
let imageGeneration = 0

const references = computed(() => plcReferencesForMonitorSelection(props.selection, props.conditions))
const location = computed(() => workspace.value
  ? projectBomForMonitorSelection(workspace.value, references.value)
  : { boms: [], images: [], markers: [], abnormalBomIds: [] })
const abnormalBomIds = computed(() => new Set(location.value.abnormalBomIds))

const selectedImage = computed<LineUpImageItem | null>(() =>
  location.value.images.find(item => item.id === selectedImageId.value) ?? location.value.images[0] ?? null,
)
const markers = computed<LineUpImageMarker[]>(() =>
  location.value.markers.filter(item => item.imageId === selectedImage.value?.id),
)
const abnormalReferenceCount = computed(() => references.value.filter(item => item.abnormal).length)
const emptyDescription = computed(() => describeMonitorBomLocation({
  loaded: loaded.value,
  stationBomCount: workspace.value?.boms.length ?? 0,
  referenceCount: references.value.length,
  matchedBomCount: location.value.boms.length,
  markerCount: location.value.markers.length,
}) || t('lineupMonitor_form.loadingBomLocation'))

function clearUrl() {
  if (imageUrl.value) {
    URL.revokeObjectURL(imageUrl.value)
  }
  imageUrl.value = ''
}

async function loadImage() {
  const generation = ++imageGeneration
  clearUrl()
  const image = selectedImage.value
  const contentUrl = image?.currentContentUrl || image?.contentUrl
  if (!contentUrl) {
    return
  }
  try {
    const nextUrl = URL.createObjectURL(await imageMarkApi.getContent(contentUrl))
    if (generation === imageGeneration) {
      imageUrl.value = nextUrl
    }
    else {
      URL.revokeObjectURL(nextUrl)
    }
  }
  catch {
    issueCode.value = t('lineupMonitor_form.errImageContentUnavailable')
  }
}

async function loadWorkspace() {
  const generation = ++workspaceGeneration
  imageGeneration++
  clearUrl()
  workspace.value = null
  selectedImageId.value = ''
  issueCode.value = ''
  loaded.value = false
  const subStationId = props.subStationId.trim()
  if (!subStationId) {
    loaded.value = true
    return
  }
  loading.value = true
  try {
    const result = await imageMarkApi.getStation(subStationId)
    if (generation === workspaceGeneration) {
      workspace.value = result
      loaded.value = true
    }
  }
  catch {
    if (generation === workspaceGeneration) {
      issueCode.value = t('lineupMonitor_form.errBomDataLoadFailed')
      loaded.value = true
    }
  }
  finally {
    if (generation === workspaceGeneration) {
      loading.value = false
    }
  }
}

function markerIsAbnormal(marker: LineUpImageMarker) {
  return marker.bomIds.some(id => abnormalBomIds.value.has(id))
}

watch(() => props.subStationId, () => void loadWorkspace(), { immediate: true })
watch(() => location.value.images.map(item => item.id).join(','), () => {
  const nextImageId = location.value.images[0]?.id ?? ''
  if (selectedImageId.value === nextImageId) {
    void loadImage()
  }
  else {
    selectedImageId.value = nextImageId
  }
})
watch(selectedImageId, () => void loadImage())
onBeforeUnmount(() => {
  workspaceGeneration++
  imageGeneration++
  clearUrl()
})
</script>

<template>
  <div v-loading="loading" class="fault-location">
    <div v-if="selection" class="bom-summary">
      <strong>{{ $t('lineupMonitor_form.currentLocationTitle') }}</strong>
      <span>{{ $t('lineupMonitor_form.identifiedVariables', { count: references.length }) }}</span>
      <el-tag v-if="abnormalReferenceCount" size="small" type="danger" effect="dark">
        {{ $t('lineupMonitor_form.abnormalReferencesFirst', { count: abnormalReferenceCount }) }}
      </el-tag>
    </div>
    <el-alert v-if="issueCode" :title="issueCode" type="warning" :closable="false" show-icon />

    <template v-if="location.images.length">
      <el-select v-if="location.images.length > 1" v-model="selectedImageId" :placeholder="$t('lineupMonitor_form.phSelectImage')">
        <el-option v-for="image in location.images" :key="image.id" :value="image.id" :label="image.pictureName || image.id" />
      </el-select>
      <div
        v-if="selectedImage && imageUrl"
        class="fault-image"
        :style="{ aspectRatio: `${selectedImage.pixelWidth || 4} / ${selectedImage.pixelHeight || 3}` }"
      >
        <img :src="imageUrl" :alt="selectedImage.pictureName || $t('lineupMonitor_form.defaultImageAlt')">
        <template v-for="marker in markers" :key="marker.id">
          <span
            v-if="marker.point?.x != null && marker.point?.y != null"
            class="fault-marker fault-marker--point"
            :class="{ 'is-abnormal': markerIsAbnormal(marker) }"
            :style="{
              left: `${marker.point.x / (selectedImage.pixelWidth || 1) * 100}%`,
              top: `${marker.point.y / (selectedImage.pixelHeight || 1) * 100}%`,
            }"
            :title="marker.point.description || marker.id"
          />
          <span
            v-else-if="marker.billboard?.x != null && marker.billboard?.y != null"
            class="fault-marker fault-marker--box"
            :class="{ 'is-abnormal': markerIsAbnormal(marker) }"
            :style="{
              left: `${marker.billboard.x / (selectedImage.pixelWidth || 1) * 100}%`,
              top: `${marker.billboard.y / (selectedImage.pixelHeight || 1) * 100}%`,
              width: `${(marker.billboard.width || 32) / (selectedImage.pixelWidth || 1) * 100}%`,
              height: `${(marker.billboard.height || 24) / (selectedImage.pixelHeight || 1) * 100}%`,
            }"
          >{{ marker.billboard.text }}</span>
        </template>
      </div>
      <div class="fault-evidence">
        <strong>{{ $t('lineupMonitor_form.locationResultTitle') }}</strong>
        <span>{{ $t('lineupMonitor_form.bomCount', { count: location.boms.length }) }}</span>
        <span>{{ $t('lineupMonitor_form.markerCount', { count: markers.length }) }}</span>
      </div>
    </template>
    <el-empty v-else :image-size="58" :description="emptyDescription" />
  </div>
</template>

<style scoped>
.fault-location {
  display: grid;
  gap: 10px;
  min-width: 0;
}

.bom-summary {
  display: flex;
  flex-wrap: wrap;
  gap: 8px 12px;
  align-items: center;
  padding: 9px 10px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
  background: var(--el-fill-color-light);
  border-radius: 6px;
}

.bom-summary strong {
  color: var(--el-text-color-primary);
}

.fault-image {
  position: relative;
  width: 100%;
  overflow: hidden;
  background: #111827;
  border: 1px solid var(--el-border-color);
  border-radius: 6px;
}

.fault-image img {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  object-fit: contain;
}

.fault-marker {
  position: absolute;
  z-index: 2;
  transform: translate(-50%, -50%);
}

.fault-marker--point {
  width: 16px;
  height: 16px;
  background: #ff4d4f;
  border: 3px solid #fff;
  border-radius: 50%;
  box-shadow: 0 0 0 4px rgb(255 77 79 / 35%);
}

.fault-marker--point.is-abnormal {
  width: 20px;
  height: 20px;
  background: #cf1322;
  box-shadow: 0 0 0 6px rgb(207 19 34 / 38%);
}

.fault-marker--box {
  display: grid;
  place-items: center;
  min-width: 24px;
  min-height: 18px;
  padding: 2px;
  overflow: hidden;
  font-size: 10px;
  color: #fff;
  background: rgb(207 19 34 / 72%);
  border: 2px solid #ff4d4f;
  transform: none;
}

.fault-marker--box.is-abnormal {
  background: rgb(207 19 34 / 88%);
  border-width: 3px;
  box-shadow: 0 0 0 4px rgb(207 19 34 / 24%);
}

.fault-evidence {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.fault-evidence strong {
  width: 100%;
  color: var(--el-text-color-primary);
}
</style>
