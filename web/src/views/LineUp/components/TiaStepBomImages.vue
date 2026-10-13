<script setup lang="ts">
import type { LineUpImageBom, LineUpImageItem, LineUpImageMarker, LineUpImageWorkspace } from '@/api/modules/LineUp/imageMarks'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import imageMarkApi from '@/api/modules/LineUp/imageMarks'
import graphNodeImageApi from '@/api/modules/LineUp/graphNodeImages'
import { useI18n } from 'vue-i18n'
import messages from './lang'

const props = withDefaults(defineProps<{
  /** BOM 图片所在子工站 id，多个用逗号分隔（层级候选工站；空则不加载） */
  subStationId?: string
  /** 选中节点关联的符号名（完整路径 + 末段裸名），与 Lup_BomDirectory.TagCode 匹配 */
  references?: string[]
  /** 查看流程图模式：按节点读已保存的"成品图+红点"快照（AI_LineUp_GraphNodeImage），不做实时匹配/叠加 */
  savedNode?: { lineId: string | number, blockName: string, nodeSourceKey: string } | null
}>(), {
  subStationId: '',
  references: () => [],
  savedNode: null,
})

const workspaces = ref<LineUpImageWorkspace[]>([])

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}
/** 查看流程图：已保存的节点快照（成品图+红点，保存结构时物化） */
const savedEntries = ref<Array<{ id: string, pictureName: string, pixelWidth: number, pixelHeight: number, url: string }>>([])
const loading = ref(false)
const loaded = ref(false)
const issueCode = ref('')
const imageUrls = ref<Record<string, string>>({})
const previewImageId = ref('')
let loadGeneration = 0

/** 匹配键集合：全部符号小写（完整路径 + 末段都进集合，TagCode 是裸名走末段命中） */
const referenceKeys = computed(() => {
  const keys = new Set<string>()
  for (const code of props.references) {
    const value = code.trim().replaceAll('"', '').toLowerCase()
    if (value) {
      keys.add(value)
    }
  }
  return keys
})

/** 从 lup_bomdirectory（工作站区）按 TagCode 查到的 BOM */
const matchedBoms = computed<LineUpImageBom[]>(() => {
  const boms: LineUpImageBom[] = []
  for (const workspace of workspaces.value) {
    for (const bom of workspace.boms) {
      const tagCode = (bom.tagCode ?? '').trim().toLowerCase()
      if (tagCode && referenceKeys.value.has(tagCode)) {
        boms.push(bom)
      }
    }
  }
  return boms
})

const matchedBomIds = computed(() => new Set(matchedBoms.value.map(item => String(item.id))))

const matchedMarkers = computed<LineUpImageMarker[]>(() => {
  const markers: LineUpImageMarker[] = []
  for (const workspace of workspaces.value) {
    for (const marker of workspace.markers) {
      if (marker.bomIds.some(id => matchedBomIds.value.has(String(id)))) {
        markers.push(marker)
      }
    }
  }
  return markers
})

const imageEntries = computed(() => {
  if (props.savedNode) {
    return savedEntries.value.map(item => ({
      image: {
        id: item.id,
        pictureName: item.pictureName,
        pixelWidth: item.pixelWidth,
        pixelHeight: item.pixelHeight,
      } as LineUpImageItem,
      url: item.url,
      markers: [],
    }))
  }
  const markerImageIds = new Set(matchedMarkers.value.map(marker => marker.imageId))
  const entries: Array<{ image: LineUpImageItem, url: string, markers: LineUpImageMarker[] }> = []
  for (const workspace of workspaces.value) {
    for (const image of workspace.images) {
      if (!markerImageIds.has(image.id)) {
        continue
      }
      const url = imageUrls.value[image.id]
      if (!url) {
        continue
      }
      entries.push({
        image,
        url,
        markers: matchedMarkers.value.filter(marker => marker.imageId === image.id),
      })
    }
  }
  return entries
})

const emptyDescription = computed(() => {
  if (!loaded.value) {
    return ''
  }
  if (props.savedNode) {
    return savedEntries.value.length === 0 ? t('lineupShared.noSavedSnapshot') : ''
  }
  if (loading.value) {
    return t('lineupShared.loadingBom')
  }
  if (workspaces.value.every(workspace => workspace.boms.length === 0)) {
    return t('lineupShared.stationNoBoms')
  }
  if (props.references.length === 0) {
    return t('lineupShared.noReferences')
  }
  if (matchedBoms.value.length === 0) {
    return t('lineupShared.noTagCodeHit', { count: props.references.length })
  }
  if (matchedMarkers.value.length === 0) {
    return t('lineupShared.matchedButNoMarks')
  }
  return ''
})

const previewEntry = computed(() =>
  imageEntries.value.find(item => item.image.id === previewImageId.value) ?? null)

/** 实际加载位图的自然尺寸（imageId → w/h）。DB 的 pixelWidth/Height 可能与当前内容版本不一致，定位一律优先用它 */
const naturalSizes = ref<Record<string, { w: number, h: number }>>({})

function onImgLoad(imageId: string, event: Event) {
  const img = event.target as HTMLImageElement
  if (!img.naturalWidth || !img.naturalHeight) {
    return
  }
  naturalSizes.value = {
    ...naturalSizes.value,
    [imageId]: { w: img.naturalWidth, h: img.naturalHeight },
  }
}

function sizeBasis(image: LineUpImageItem): { w: number, h: number } {
  return naturalSizes.value[image.id]
    ?? { w: image.pixelWidth || 0, h: image.pixelHeight || 0 }
}

/** 用成品图做底图的图片：保存点位时由编辑器同源合成（原始图 1:1 + 标签 + 连线，不含红点）。
 * 红点是消费侧动态叠加层：生成预览 = 成品图 + 当前工步匹配 BOM 的红点位置。
 * 成品图里的照片相对图面有边距带偏移，红点坐标（原始图空间）需加照片原点换算。 */
const compositeImageIds = ref<Set<string>>(new Set())

/** 成品图内照片原点：导出时照片居中放置，直接按 (成品图尺寸 - 照片尺寸) / 2 精确计算 */
function compositePhotoOffset(image: LineUpImageItem, basis: { w: number, h: number }) {
  if (!basis.w || !basis.h || !image.pixelWidth || !image.pixelHeight) {
    return null
  }
  return { x: (basis.w - image.pixelWidth) / 2, y: (basis.h - image.pixelHeight) / 2 }
}

function isComposite(imageId: string) {
  return compositeImageIds.value.has(imageId)
}

/** 红点锚点：成品图 = 照片原点(居中计算) + 标记坐标；原始图 = 标记坐标本身。 */
function markerAnchor(image: LineUpImageItem, marker: LineUpImageMarker) {
  const point = marker.point
  if (point?.x == null || point?.y == null) {
    return null
  }
  const basis = sizeBasis(image)
  if (isComposite(image.id)) {
    const offset = compositePhotoOffset(image, basis)
    if (!offset) {
      return null
    }
    return { left: percent(offset.x + point.x, basis.w), top: percent(offset.y + point.y, basis.h) }
  }
  return { left: percent(point.x, basis.w), top: percent(point.y, basis.h) }
}

function markerText(marker: LineUpImageMarker) {
  const boms = matchedBoms.value.filter(item => marker.bomIds.some(id => String(id) === String(item.id)))
  const names = boms.map(item => item.refNo || item.tagCode || item.id)
  return [marker.billboard?.text, ...names].filter(Boolean).join(' · ')
}

function percent(value: number, total: number) {
  return `${value / (total || 1) * 100}%`
}

function clearUrls() {
  for (const url of Object.values(imageUrls.value)) {
    URL.revokeObjectURL(url)
  }
  imageUrls.value = {}
}

async function load() {
  const generation = ++loadGeneration
  clearUrls()
  workspaces.value = []
  savedEntries.value = []
  loaded.value = false
  issueCode.value = ''
  previewImageId.value = ''
  compositeImageIds.value = new Set()
  if (props.savedNode) {
    loading.value = true
    try {
      const rows = await graphNodeImageApi.listByNode(String(props.savedNode.lineId), props.savedNode.blockName, props.savedNode.nodeSourceKey)
      if (generation !== loadGeneration) {
        return
      }
      const next: typeof savedEntries.value = []
      for (const row of rows) {
        try {
          const url = URL.createObjectURL(await graphNodeImageApi.getContent(row.contentUrl))
          compositeImageIds.value.add(row.sourceImageId)
          next.push({ id: `${row.sourceImageId}`, pictureName: row.blockName, pixelWidth: row.pixelWidth, pixelHeight: row.pixelHeight, url })
        }
        catch {
          // 单张快照读取失败不阻塞其余
        }
      }
      if (generation === loadGeneration) {
        savedEntries.value = next
        loaded.value = true
      }
      else {
        for (const item of next) {
          URL.revokeObjectURL(item.url)
        }
      }
    }
    catch {
      if (generation === loadGeneration) {
        issueCode.value = t('lineupShared.snapshotLoadFailed')
        loaded.value = true
      }
    }
    finally {
      if (generation === loadGeneration) {
        loading.value = false
      }
    }
    return
  }
  const stationIds = props.subStationId.split(',').map(item => item.trim()).filter(Boolean)
  if (!stationIds.length) {
    loaded.value = true
    return
  }
  loading.value = true
  try {
    const results: LineUpImageWorkspace[] = []
    for (const stationId of stationIds) {
      try {
        results.push(await imageMarkApi.getStation(stationId))
      }
      catch {
        // 单工站读取失败不阻塞其余
      }
    }
    if (generation !== loadGeneration) {
      return
    }
    workspaces.value = results
    loaded.value = true
    const next: Record<string, string> = {}
    for (const workspace of results) {
      for (const image of workspace.images) {
        // 成品图优先（查看点位的正图）：新成品图在保存点位时由编辑器同源合成、老成品图为老系统快照，
        // 两者都已把点位/标签烤进图内，直接整图展示即可；
        // 仅当没有成品图时回退原始图 + 实时叠加红点（标记坐标就是原始图空间）。
        const compositeUrl = image.currentContentUrl
        const contentUrl = compositeUrl || image.contentUrl
        if (!contentUrl || next[image.id]) {
          continue
        }
        if (compositeUrl) {
          compositeImageIds.value.add(image.id)
        }
        try {
          next[image.id] = URL.createObjectURL(await imageMarkApi.getContent(contentUrl))
        }
        catch {
          // 单张图读取失败不阻塞其余缩略图
        }
      }
    }
    if (generation === loadGeneration) {
      imageUrls.value = next
    }
    else {
      for (const url of Object.values(next)) {
        URL.revokeObjectURL(url)
      }
    }
  }
  catch {
    if (generation === loadGeneration) {
      issueCode.value = t('lineupShared.bomLoadFailed')
      loaded.value = true
    }
  }
  finally {
    if (generation === loadGeneration) {
      loading.value = false
    }
  }
}

watch(() => [props.subStationId, props.savedNode?.lineId, props.savedNode?.blockName, props.savedNode?.nodeSourceKey] as const, () => void load(), { immediate: true })
onBeforeUnmount(clearUrls)
</script>

<template>
  <div v-loading="loading" class="step-bom-images">
    <p v-if="issueCode" class="issue">{{ issueCode }}</p>
    <div v-if="imageEntries.length" class="thumb-grid">
      <figure v-for="entry in imageEntries" :key="entry.image.id" class="thumb-item">
        <button
          type="button"
          class="thumb-stage"
          :style="{ aspectRatio: (sizeBasis(entry.image).w || 4) + ' / ' + (sizeBasis(entry.image).h || 3) }"
          @click="previewImageId = entry.image.id"
        >
          <img :src="entry.url" :alt="entry.image.pictureName || $t('lineupShared.bomImageAlt')" @load="onImgLoad(entry.image.id, $event)">
          <!-- 成品图已含标签/连线；红点=当前工步匹配 BOM 的动态叠加层（按照片原点换算）。
            只有回退原始图（无成品图）时才实时叠加标签。 -->
          <template v-for="marker in entry.markers" :key="marker.id">
            <span
              v-if="markerAnchor(entry.image, marker)"
              class="bom-marker bom-marker--point"
              :style="markerAnchor(entry.image, marker) ?? undefined"
              :title="markerText(marker)"
            />
            <span
              v-if="!isComposite(entry.image.id) && marker.billboard?.x != null && marker.billboard?.y != null && marker.billboard.text"
              class="bom-marker bom-marker--box"
              :style="{
                left: percent(marker.billboard.x, sizeBasis(entry.image).w),
                top: percent(marker.billboard.y, sizeBasis(entry.image).h),
              }"
              :title="markerText(marker)"
            >{{ marker.billboard.text }}</span>
          </template>
        </button>
        <figcaption>
          <span class="thumb-name" :title="entry.image.pictureName ?? undefined">{{ entry.image.pictureName || entry.image.id }}</span>
          <small>{{ props.savedNode ? $t('lineupShared.snapshotLabel') : $t('lineupShared.marksCount', { count: entry.markers.length }) }}</small>
        </figcaption>
      </figure>
    </div>
    <p v-else-if="emptyDescription" class="empty">
      {{ emptyDescription }}
    </p>

    <el-dialog
      :model-value="Boolean(previewEntry)"
      :title="previewEntry?.image.pictureName || $t('lineupShared.bomImageAlt')"
      width="min(92vw, 1080px)"
      append-to-body
      destroy-on-close
      @update:model-value="previewImageId = ''"
    >
      <div
        v-if="previewEntry"
        class="preview-stage"
        :style="{ aspectRatio: (sizeBasis(previewEntry.image).w || 4) + ' / ' + (sizeBasis(previewEntry.image).h || 3) }"
      >
        <img :src="previewEntry.url" :alt="previewEntry?.image.pictureName || $t('lineupShared.bomImageAlt')" @load="onImgLoad(previewEntry.image.id, $event)">
        <template v-for="marker in previewEntry.markers" :key="marker.id">
          <span
            v-if="markerAnchor(previewEntry.image, marker)"
            class="bom-marker bom-marker--point is-preview"
            :style="markerAnchor(previewEntry.image, marker) ?? undefined"
            :title="markerText(marker)"
          />
          <span
            v-if="!isComposite(previewEntry.image.id) && marker.billboard?.x != null && marker.billboard?.y != null && marker.billboard.text"
            class="bom-marker bom-marker--box is-preview"
            :style="{
              left: percent(marker.billboard.x, sizeBasis(previewEntry.image).w),
              top: percent(marker.billboard.y, sizeBasis(previewEntry.image).h),
            }"
            :title="markerText(marker)"
          >{{ marker.billboard.text }}</span>
        </template>
      </div>
    </el-dialog>
  </div>
</template>

<style scoped>
.step-bom-images {
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-width: 0;
}

.issue {
  margin: 0;
  font-size: 12px;
  color: var(--el-color-danger);
}

.thumb-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(168px, 1fr));
  gap: 8px;
}

.thumb-item {
  display: flex;
  flex-direction: column;
  gap: 4px;
  margin: 0;
  min-width: 0;
}

.thumb-stage {
  position: relative;
  width: 100%;
  max-height: 150px;
  padding: 0;
  overflow: hidden;
  cursor: zoom-in;
  background: var(--el-fill-color-light);
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 6px;
}

.thumb-stage img {
  width: 100%;
  height: 100%;
  object-fit: contain;
}

.thumb-item figcaption {
  display: flex;
  gap: 6px;
  align-items: baseline;
  justify-content: space-between;
  min-width: 0;
}

.thumb-name {
  overflow: hidden;
  font-size: 12px;
  color: var(--el-text-color-regular);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.thumb-item small {
  flex-shrink: 0;
  font-size: 11px;
  color: var(--el-text-color-secondary);
}

.preview-stage {
  position: relative;
  width: 100%;
  overflow: hidden;
  border-radius: 6px;
}

.preview-stage img {
  display: block;
  width: 100%;
  height: 100%;
  object-fit: contain;
}

.bom-marker--point {
  position: absolute;
  width: 12px;
  height: 12px;
  cursor: help;
  background: #ff5722;
  border: 2px solid #fff;
  border-radius: 50%;
  box-shadow: 0 0 0 2px #ff5722;
  transform: translate(-50%, -50%);
}

.bom-marker--point.is-preview {
  width: 18px;
  height: 18px;
}

.bom-marker--box {
  position: absolute;
  max-width: 40%;
  padding: 2px 6px;
  overflow: hidden;
  font-size: 11px;
  color: #fff;
  cursor: help;
  background: #ff5722;
  border-radius: 4px;
  transform: translate(-50%, -50%);
}

.bom-marker--box.is-preview {
  font-size: 13px;
}

.empty {
  margin: 0;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}
</style>
