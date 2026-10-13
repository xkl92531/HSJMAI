<script setup lang="ts">
import type { TiaLadOperandProjection, TiaLadSymbolProjection } from './tiaLadProjection'
import type { LineUpConditionRuntimeValue } from '@/api/modules/LineUp/monitor'
import type { LineUpGraphLogicNetwork } from '@/api/modules/LineUp/types'
import { computed, ref } from 'vue'
import {
  LAD_COLUMN_WIDTH,
  expandLadOrGates,
  isLadComparatorKind,
  ladColumnMaxChars,
  ladPinLayout,
  layoutLadNetwork,
  projectLadGeometry,
  projectLadNetwork,
  tiaLadComparisonExpression,
  tiaLadNetworkExpression,
  tiaLadPartGlyph,
  tiaLadSymbolHeight,
  wrapLadLabelLines,
} from './tiaLadProjection'
import { resolveTiaLadViewport } from './tiaLadViewport'
import { useI18n } from 'vue-i18n'
import messages from './lang'

const props = withDefaults(defineProps<{
  network: LineUpGraphLogicNetwork
  runtimeValues?: LineUpConditionRuntimeValue[]
  /** FB/FC 局部变量 → 全局符号路径 映射：LocalVariable 部件按此显示实际对应的 DB 符号路径 */
  localSymbolMap?: Record<string, string>
  /** 全屏遮罩内的实例：不再响应"点图放大"（避免无限叠加），并自适应初始缩放 */
  fullscreen?: boolean
}>(), {
  runtimeValues: () => [],
  localSymbolMap: () => ({}),
  fullscreen: false,
})

const projection = computed(() => projectLadNetwork(props.network, props.runtimeValues, props.localSymbolMap))

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
    mergeLocaleMessage(locale, messages[locale])
}
// 博图 LAD 视图：Or(>=1) 盒不保留，展开为并联支路（上下并排、汇合后进完结节点线圈）
const expanded = computed(() => expandLadOrGates(projection.value))
const layout = computed(() => layoutLadNetwork(expanded.value))
const geometry = computed(() => projectLadGeometry(expanded.value, layout.value))
// 右母线在内容宽度之外 36px，再留 24px 边距（博图：梯级始于左母线、终于右母线）
const networkWidth = computed(() => Math.max(360, geometry.value.rightRailX + 24))
// 未附着 Access 兜底行放在图形内容下方（旧实现固定 y=174 起排，行多时会压到最后一梯级）
const fallbackTop = computed(() => Math.max(160, layout.value.height) + 6)
const networkHeight = computed(() => Math.max(224, fallbackTop.value + projection.value.accessFallbacks.length * 16))

// 博图式浏览：默认 1:1 原始尺寸（文字不缩小），宽图横向滚动；缩放由按钮控制
const scrollEl = ref<HTMLElement | null>(null)
const zoom = ref(1)
const ZOOM_MIN = 0.4
const ZOOM_MAX = 3
function applyZoom(next: number) {
  zoom.value = Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, Math.round(next * 100) / 100))
}
function zoomIn() {
  applyZoom(zoom.value * 1.25)
}
function zoomOut() {
  applyZoom(zoom.value / 1.25)
}
function zoomReset() {
  applyZoom(1)
}
function zoomFit() {
  const panelWidth = scrollEl.value?.clientWidth ?? 0
  if (panelWidth > 0) {
    applyZoom((panelWidth - 12) / Math.max(1, networkWidth.value))
  }
}
const viewport = computed(() => resolveTiaLadViewport(networkWidth.value, networkHeight.value, zoom.value))

// 图形 ↔ 纯文本表达式切换
const viewMode = ref<'lad' | 'text'>('lad')
const expressionText = computed(() => tiaLadNetworkExpression(props.network, props.runtimeValues, props.localSymbolMap))
function toggleViewMode() {
  viewMode.value = viewMode.value === 'lad' ? 'text' : 'lad'
}

// 点图放大：全屏遮罩内看大图（遮罩内的实例自身不再叠加）。
// 层级策略：打开瞬间扫描页面里所有定位元素的 z-index，取最大值 +10 ——
// 无论外层套了多少个 el-dialog / 抽屉 / 消息层，放大层永远置顶最上层。
const overlayOpen = ref(false)
const overlayZIndex = ref(2000)
function topmostZIndex(): number {
  let max = 0
  for (const el of Array.from(document.body.querySelectorAll('*'))) {
    const style = window.getComputedStyle(el)
    if (style.position === 'fixed' || style.position === 'absolute') {
      const z = Number.parseInt(style.zIndex, 10)
      if (Number.isFinite(z) && z > max) {
        max = z
      }
    }
  }
  return max
}
function openOverlay() {
  if (props.fullscreen) {
    return
  }
  overlayZIndex.value = Math.max(2000, topmostZIndex() + 10)
  overlayOpen.value = true
}
// 博图 LAD 视图：TrCoil/IlCoil/SvCoil 只是逻辑完结点，不画线圈本体——
// 支路合流后一条线直达右母线（线圈仍参与布局定位与居中）
const drawableSymbols = computed(() => expanded.value.symbols.filter(symbol => !symbol.kind.endsWith('-coil')))

function isContact(symbol: TiaLadSymbolProjection) {
  return symbol.kind === 'contact'
}

function isComparator(symbol: TiaLadSymbolProjection) {
  return isLadComparatorKind(symbol.kind)
}

function isNot(symbol: TiaLadSymbolProjection) {
  return symbol.kind === 'not'
}

// 比较器是内联表达式：操作数不再逐引脚罗列，由表达式整体呈现
function visibleOperands(symbol: TiaLadSymbolProjection) {
  return isComparator(symbol) ? [] : symbol.operands
}

// 长文本折行渲染：按该符号所在列的字符预算决定每行可容纳的字符数（预算由布局引擎
// 按"本列与相邻列宽的最小值 - 净空"统一折算，宽列在窄邻列旁宁可多折一行也不横向越界），
// 内容全部展示（不截断不加省略号），完整内容另在 title 悬浮提示里
function labelMaxChars(symbol: TiaLadSymbolProjection) {
  const column = layout.value.symbolColumns.get(symbol.key) ?? 0
  return layout.value.columnMaxChars[column] ?? ladColumnMaxChars(layout.value.columnWidths[column] ?? LAD_COLUMN_WIDTH)
}
function operandLines(symbol: TiaLadSymbolProjection, operand: TiaLadOperandProjection) {
  return wrapLadLabelLines(operand.label, labelMaxChars(symbol))
}
function expressionLines(symbol: TiaLadSymbolProjection) {
  return wrapLadLabelLines(tiaLadComparisonExpression(symbol), labelMaxChars(symbol))
}

// —— 运行信息统一放符号最上层（变量名下、图形上），替代旧版"内联值行 + 下方三行块"的重复展示 ——
// 结果行 = 部件级真正计算出的判断：触点含常闭取反、比较器为类型化比较结果，且与上游能流 AND
function hasJudgment(symbol: TiaLadSymbolProjection) {
  return symbol.runtime.quality !== 'Unknown'
}

function stateTone(state: 'true' | 'false' | 'unknown') {
  return state === 'true' ? 'is-true' : state === 'false' ? 'is-false' : ''
}

function judgmentClass(symbol: TiaLadSymbolProjection) {
  return stateTone(symbol.runtime.state)
}

function comparatorValues(symbol: TiaLadSymbolProjection) {
  const pinOrder = ['in1', 'in2', 'in', 'operand']
  const ordered = [...symbol.operands].sort((a, b) => {
    const ai = pinOrder.indexOf(a.targetPin)
    const bi = pinOrder.indexOf(b.targetPin)
    return (ai === -1 ? 9 : ai) - (bi === -1 ? 9 : bi)
  })
  return capMetaText(ordered.map(item => formatMetaValue(item.runtime.value)).join(' , '))
}

function contactInfoCount(symbol: TiaLadSymbolProjection, operand: TiaLadOperandProjection) {
  let count = operandLines(symbol, operand).length
  if (hasJudgment(symbol)) count += 1
  if (operand.runtime.quality !== 'Unknown') count += 1
  return count
}

function comparatorInfoCount(symbol: TiaLadSymbolProjection) {
  let count = expressionLines(symbol).length
  if (hasJudgment(symbol)) count += 1
  if (symbol.operands.length > 0 && symbol.runtime.quality !== 'Unknown') count += 1
  return count
}

function boxInfoCount(symbol: TiaLadSymbolProjection) {
  const labelCount = wrapLadLabelLines(symbol.label, labelMaxChars(symbol)).length
  return labelCount + (hasJudgment(symbol) ? 1 : 0)
}
function stackLineY(count: number, index: number) {
  // 自下而上堆叠：最后一行贴 -9，其余每行向上抬 12px
  return -9 - (count - 1 - index) * 12
}

function symbolPosition(symbol: TiaLadSymbolProjection) {
  return layout.value.positions.get(symbol.key) ?? { x: 78, y: 40 }
}

function symbolTone(symbol: TiaLadSymbolProjection) {
  if (symbol.runtime.quality !== 'Good' || symbol.runtime.state === 'unknown') {
    return 'is-unknown'
  }
  return symbol.runtime.state === 'true' ? 'is-true' : 'is-false'
}

function runtimeLabel(symbol: TiaLadSymbolProjection) {
  if (symbol.runtime.quality !== 'Good') {
    return symbol.runtime.quality
  }
  return symbol.runtime.state === 'true' ? 'True' : symbol.runtime.state === 'false' ? 'False' : 'Unknown'
}

function formatRuntimeValue(value: unknown) {
  if (value === null || value === undefined) {
    return '—'
  }
  if (typeof value === 'string') {
    return value.slice(0, 96)
  }
  if (typeof value === 'boolean' || typeof value === 'number') {
    return String(value)
  }
  return '—'
}

// 值状态行（9px 单行渲染）的值做短截断：长值会把整行撑过列文字预算、压到相邻列文字；
// 完整值仍在悬浮 title（runtimeDetail / operandRuntimeDetail）里可看。
function capMetaText(text: string) {
  return text.length > 10 ? text.slice(0, 9) + '…' : text
}
function formatMetaValue(value: unknown) {
  return capMetaText(formatRuntimeValue(value))
}

function formatRuntimeTimestamp(value: string | null) {
  return value || '—'
}

function runtimeDetail(symbol: TiaLadSymbolProjection) {
  const runtime = symbol.runtime
  return t('lineupShared.ladRuntimeDetail', { value: formatRuntimeValue(runtime.value), quality: runtime.quality, code: runtime.qualityCode || '—', time: formatRuntimeTimestamp(runtime.sourceTimestamp) })
}

function operandRuntimeDetail(operand: TiaLadOperandProjection) {
  const runtime = operand.runtime
  return t('lineupShared.ladRuntimeDetail', { value: formatRuntimeValue(runtime.value), quality: runtime.quality, code: runtime.qualityCode || '—', time: formatRuntimeTimestamp(runtime.sourceTimestamp) })
}

function operandRuntimeSuffix(operand: TiaLadOperandProjection) {
  if (operand.runtime.quality === 'Good') {
    return operand.runtime.state === 'true' ? ' [True]' : operand.runtime.state === 'false' ? ' [False]' : ''
  }
  return operand.runtime.quality === 'Unknown' && !operand.runtime.qualityCode
    ? ''
    : ` [${operand.runtime.quality}]`
}

// 盒式部件引脚几何与标签折行（tiaLadProjection 统一计算，导线接入点与标签同位）：
// 旧实现 operandY = min(pinIndex,3)*14+12 把第 5 根起引脚钳到同一 y，长标签也不折行越列。
// 折行预算与布局引擎共用同一组数字：居中标签用 columnMaxChars、
// 盒左引脚标签用更紧的 columnPinMaxChars（走线走廊更窄）。
function symbolMaxChars(symbol: TiaLadSymbolProjection) {
  const column = layout.value.symbolColumns.get(symbol.key) ?? 0
  const centered = symbol.kind === 'contact' || isComparator(symbol) || symbol.operands.length === 0
  const maxChars = (centered ? layout.value.columnMaxChars : layout.value.columnPinMaxChars)[column]
  return maxChars ?? ladColumnMaxChars(layout.value.columnWidths[column] ?? LAD_COLUMN_WIDTH)
}

function pinLayoutOf(symbol: TiaLadSymbolProjection) {
  return ladPinLayout(symbol, symbolMaxChars(symbol))
}

function symbolBoxHeight(symbol: TiaLadSymbolProjection) {
  return tiaLadSymbolHeight(symbol, symbolMaxChars(symbol))
}

function operandBlockLines(symbol: TiaLadSymbolProjection, operand: TiaLadOperandProjection) {
  return pinLayoutOf(symbol).lines.get(operand.targetPin) ?? [operand.label]
}

function operandBlockY(symbol: TiaLadSymbolProjection, operand: TiaLadOperandProjection, index: number) {
  const geometry = pinLayoutOf(symbol)
  const baseY = geometry.pinY.get(operand.targetPin)
  const lines = geometry.lines.get(operand.targetPin) ?? [operand.label]
  if (baseY === undefined) {
    return Math.max(0, symbol.pins.indexOf(operand.targetPin)) * 14 + 12
  }
  // 多行标签以引脚位为末行，其余行向上堆叠（与引脚纵排推让的间距一致）
  return baseY - (lines.length - 1 - index) * 12
}
</script>

<template>
  <article class="tia-lad-network">
    <header>
      <div class="network-title">
        <strong>{{ projection.title || 'Network' }}</strong>
        <span>{{ projection.programmingLanguage || 'LAD' }}</span>
      </div>
      <div class="zoom-controls">
        <button type="button" :title="viewMode === 'lad' ? $t('lineupShared.switchToText') : $t('lineupShared.switchToLad')" @click="toggleViewMode">
          {{ viewMode === 'lad' ? $t('lineupShared.textMode') : $t('lineupShared.graphMode') }}
        </button>
        <template v-if="viewMode === 'lad'">
          <button type="button" :title="$t('lineupShared.zoomOut')" @click="zoomOut">−</button>
          <span class="zoom-level">{{ Math.round(zoom * 100) }}%</span>
          <button type="button" :title="$t('lineupShared.zoomIn')" @click="zoomIn">＋</button>
          <button type="button" :title="$t('lineupShared.fitPanelTitle')" @click="zoomFit">{{ $t('lineupShared.fitCompactBtn') }}</button>
          <button type="button" :title="$t('lineupShared.resetSizeTitle')" @click="zoomReset">1:1</button>
          <button v-if="!fullscreen" type="button" :title="$t('lineupShared.fullscreenTitle')" @click="openOverlay">⤢ {{ $t('lineupShared.enlargeBtn') }}</button>
        </template>
      </div>
    </header>
    <div v-if="viewMode === 'text'" class="lad-expression-wrap">
      <pre class="lad-expression">{{ expressionText ?? $t('lineupShared.noConditionExpression') }}</pre>
    </div>
    <div v-show="viewMode === 'lad'" ref="scrollEl" class="lad-scroll" :class="{ 'is-zoomable': !fullscreen }" @click="openOverlay">
      <svg
        class="lad-canvas"
        :viewBox="viewport.viewBox"
        :style="viewport.style"
        role="img"
        :aria-label="projection.title || projection.key"
      >
        <line v-if="geometry.hasPowerrail" class="powerrail" x1="28" y1="20" x2="28" :y2="networkHeight - 20" />
        <line v-if="geometry.rightRailX > 0" class="powerrail powerrail-right" :x1="geometry.rightRailX" y1="20" :x2="geometry.rightRailX" :y2="networkHeight - 20" />
        <polyline
          v-for="wire in geometry.wires"
          :key="wire.key"
          class="connection-wire"
          :class="wire.state ? 'is-' + wire.state : undefined"
          :points="wire.points"
        />
        <g
          v-for="marker in geometry.markers"
          :key="marker.key"
          class="endpoint-marker"
          :class="`is-${marker.kind}`"
        >
          <title>{{ marker.label }}</title>
          <circle v-if="marker.kind === 'open'" :cx="marker.x" :cy="marker.y" r="5" />
          <template v-else>
            <line :x1="marker.x - 5" :y1="marker.y - 5" :x2="marker.x + 5" :y2="marker.y + 5" />
            <line :x1="marker.x - 5" :y1="marker.y + 5" :x2="marker.x + 5" :y2="marker.y - 5" />
          </template>
          <text :x="marker.x + 8" :y="marker.y - 7">{{ marker.label }}</text>
        </g>
        <g
          v-for="symbol in drawableSymbols"
          :key="symbol.key"
          class="lad-symbol"
          :class="[symbolTone(symbol), { 'is-unknown-part': symbol.kind === 'unknown' }]"
          :transform="`translate(${symbolPosition(symbol).x} ${symbolPosition(symbol).y})`"
        >
          <title>{{ symbol.partName }} · {{ symbol.label }} · {{ runtimeLabel(symbol) }} · {{ runtimeDetail(symbol) }}</title>
          <template v-if="isContact(symbol)">
            <!-- 博图触点：母线导线贯穿触点（左右短线接到触点上，不悬空） -->
            <line x1="0" y1="26" x2="38" y2="26" />
            <line x1="38" y1="8" x2="38" y2="44" />
            <line x1="72" y1="8" x2="72" y2="44" />
            <line v-if="symbol.negated" x1="34" y1="44" x2="76" y2="8" />
            <line x1="72" y1="26" x2="112" y2="26" />
          </template>

          <template v-else-if="isComparator(symbol)">
            <!-- 比较器是表达式不是盒：默认按常开触点 || 画，表达式写在触点上方 -->
            <line x1="0" y1="26" x2="38" y2="26" />
            <line x1="38" y1="8" x2="38" y2="44" />
            <line x1="72" y1="8" x2="72" y2="44" />
            <line v-if="symbol.negated" x1="34" y1="44" x2="76" y2="8" />
            <line x1="72" y1="26" x2="112" y2="26" />
            <text class="operand-label" text-anchor="middle">
              <title>{{ tiaLadComparisonExpression(symbol) }} · {{ runtimeLabel(symbol) }} · {{ runtimeDetail(symbol) }}</title>
              <tspan
                v-for="(line, index) in expressionLines(symbol)"
                :key="index"
                x="55"
                :y="stackLineY(comparatorInfoCount(symbol), index)"
              >{{ line }}</tspan>
              <tspan
                v-if="hasJudgment(symbol)"
                x="55"
                :y="stackLineY(comparatorInfoCount(symbol), expressionLines(symbol).length)"
                class="runtime-judgment"
                :class="judgmentClass(symbol)"
              >>{{ $t('lineupShared.ladJudgment', { value: runtimeLabel(symbol) }) }}</tspan>
              <tspan
                v-if="symbol.operands.length > 0 && symbol.runtime.quality !== 'Unknown'"
                x="55"
                :y="stackLineY(comparatorInfoCount(symbol), expressionLines(symbol).length + 1)"
                class="runtime-meta"
              >{{ $t('lineupShared.ladValueStatusMeta', { value: comparatorValues(symbol), status: symbol.runtime.quality }) }}</tspan>
            </text>
          </template>

          <template v-else-if="isNot(symbol)">
            <!-- NOT 不画盒子：触点式竖线 |NOT| -->
            <line x1="0" y1="26" x2="38" y2="26" />
            <line x1="38" y1="8" x2="38" y2="44" />
            <line x1="72" y1="8" x2="72" y2="44" />
            <line x1="72" y1="26" x2="112" y2="26" />
            <text class="not-label" x="55" y="26" text-anchor="middle" dominant-baseline="central">NOT</text>
            <text v-if="hasJudgment(symbol)" class="runtime-judgment" :class="judgmentClass(symbol)" x="55" y="-9" text-anchor="middle">>{{ $t('lineupShared.ladJudgment', { value: runtimeLabel(symbol) }) }}</text>
          </template>

          <rect v-else x="0" y="0" width="110" :height="symbolBoxHeight(symbol)" rx="2" />
          <text
            v-if="symbol.operands.length === 0 && !isNot(symbol)"
            class="symbol-label"
            text-anchor="middle"
          >
            <title>{{ symbol.label }}</title>
            <tspan
              v-for="(line, index) in wrapLadLabelLines(symbol.label, labelMaxChars(symbol))"
              :key="index"
              x="55"
              :y="stackLineY(boxInfoCount(symbol), index)"
            >{{ line }}</tspan>
            <tspan
              v-if="hasJudgment(symbol)"
              x="55"
              :y="stackLineY(boxInfoCount(symbol), wrapLadLabelLines(symbol.label, labelMaxChars(symbol)).length)"
              class="runtime-judgment"
              :class="judgmentClass(symbol)"
            >>{{ $t('lineupShared.ladJudgment', { value: runtimeLabel(symbol) }) }}</tspan>
          </text>
          <template v-for="operand in visibleOperands(symbol)" :key="operand.key">
            <text
              v-if="isContact(symbol) && operand.targetPin === 'operand'"
              class="operand-label"
              text-anchor="middle"
            >
              <title>{{ operand.label }} · {{ operandRuntimeDetail(operand) }}</title>
              <tspan
                v-for="(line, index) in operandLines(symbol, operand)"
                :key="index"
                x="55"
                :y="stackLineY(contactInfoCount(symbol, operand), index)"
              >{{ line }}</tspan>
              <tspan
                v-if="hasJudgment(symbol)"
                x="55"
                :y="stackLineY(contactInfoCount(symbol, operand), operandLines(symbol, operand).length)"
                class="runtime-judgment"
                :class="judgmentClass(symbol)"
              >>{{ $t('lineupShared.ladJudgment', { value: runtimeLabel(symbol) }) }}</tspan>
              <tspan
                v-if="operand.runtime.quality !== 'Unknown'"
                x="55"
                :y="stackLineY(contactInfoCount(symbol, operand), operandLines(symbol, operand).length + (hasJudgment(symbol) ? 1 : 0))"
                class="runtime-meta"
              >{{ $t('lineupShared.ladValueStatusMeta', { value: formatMetaValue(operand.runtime.value), status: operand.runtime.quality }) }}</tspan>
            </text>
            <text
              v-else
              class="operand-label"
              text-anchor="end"
            >
              <title>{{ operand.label }} · {{ operandRuntimeDetail(operand) }}</title>
              <tspan
                v-for="(line, index) in operandBlockLines(symbol, operand)"
                :key="index"
                x="-8"
                :y="operandBlockY(symbol, operand, index)"
              >{{ line }}<tspan
                v-if="index === operandBlockLines(symbol, operand).length - 1 && operandRuntimeSuffix(operand)"
                class="runtime-suffix"
                :class="stateTone(operand.runtime.state)"
              >{{ operandRuntimeSuffix(operand) }}</tspan></tspan>
            </text>
          </template>
          <text v-if="!isComparator(symbol) && !isNot(symbol) && tiaLadPartGlyph(symbol)" class="part-label" x="55" y="30" text-anchor="middle">{{ tiaLadPartGlyph(symbol) }}</text>
          <text v-if="symbol.constantValue" class="constant-label" x="55" :y="symbolBoxHeight(symbol) + 14" text-anchor="middle">
            {{ symbol.constantValue }}
          </text>
        </g>
        <g
          v-for="(access, index) in projection.accessFallbacks"
          :key="access.key"
          class="access-fallback"
          :transform="`translate(42 ${fallbackTop + index * 16})`"
        >
          <title>{{ access.key }} · {{ operandRuntimeDetail(access) }}</title>
          <text>{{ $t('lineupShared.ladAccessFallback', { label: access.label, uid: access.uId, value: formatRuntimeValue(access.runtime.value), quality: access.runtime.quality, code: access.runtime.qualityCode || '—', time: formatRuntimeTimestamp(access.runtime.sourceTimestamp) }) }}</text>
        </g>
      </svg>
    </div>
    <Teleport to="body">
      <div v-if="overlayOpen" class="lad-overlay" :style="{ zIndex: overlayZIndex }" @click.self="overlayOpen = false">
        <div class="lad-overlay-panel">
          <div class="lad-overlay-header">
            <strong>{{ projection.title || 'Network' }} · {{ projection.programmingLanguage || 'LAD' }}</strong>
            <button type="button" @click="overlayOpen = false">{{ $t('lineupShared.closeMark') }}</button>
          </div>
          <div class="lad-overlay-body">
            <TiaLadNetwork :network="network" :runtime-values="runtimeValues" :local-symbol-map="localSymbolMap" fullscreen />
          </div>
        </div>
      </div>
    </Teleport>
  </article>
</template>

<style scoped>
.tia-lad-network {
  min-width: 0;
  background: #f8f9fb;
  border: 1px solid #aeb4bd;
}

.tia-lad-network header {
  display: flex;
  gap: 12px;
  align-items: center;
  justify-content: space-between;
  padding: 6px 9px;
  font-size: 12px;
  color: #303133;
  overflow-wrap: anywhere;
  background: #e7e9ed;
  border-bottom: 1px solid #b9bec6;
}

.network-title {
  display: flex;
  gap: 12px;
  align-items: baseline;
  min-width: 0;
}

.tia-lad-network header span {
  color: #6b7280;
}

/* 缩放控制：默认 1:1 原始尺寸渲染，宽图横向滚动（不整体缩小） */
.zoom-controls {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
}

.zoom-controls button {
  border: 1px solid #b9bec6;
  background: #fff;
  border-radius: 4px;
  font-size: 11px;
  line-height: 1;
  padding: 3px 7px;
  cursor: pointer;
  color: #30343b;
}

.zoom-controls button:hover {
  background: #eef1f5;
}

.zoom-level {
  font-size: 11px;
  color: #6b7280;
  min-width: 38px;
  text-align: center;
}

.lad-scroll {
  max-width: 100%;
  overflow-x: auto;
  overflow-y: hidden;
}

.lad-scroll.is-zoomable {
  cursor: zoom-in;
}

/* 纯文本表达式视图（与 LAD 图等价的布尔表达式） */
.lad-expression-wrap {
  padding: 10px 12px;
  background: #fff;
}

.lad-expression {
  margin: 0;
  font-family: Consolas, "Cascadia Mono", "Microsoft YaHei", monospace;
  font-size: 13px;
  line-height: 1.7;
  color: #1f2937;
  white-space: pre-wrap;
  word-break: break-all;
}

/* 全屏遮罩：点击卡片/放大按钮后的大图弹层（z-index 由 openOverlay 动态置顶） */
.lad-overlay {
  position: fixed;
  inset: 0;
  z-index: 2000;
  display: flex;
  align-items: center;
  justify-content: center;
  background: rgba(15, 18, 24, 0.55);
}

.lad-overlay-panel {
  display: flex;
  flex-direction: column;
  width: min(1240px, 94vw);
  max-height: 92vh;
  background: #f8f9fb;
  border-radius: 8px;
  overflow: hidden;
  box-shadow: 0 18px 50px rgba(0, 0, 0, 0.35);
}

.lad-overlay-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 10px 14px;
  font-size: 13px;
  color: #303133;
  background: #e7e9ed;
  border-bottom: 1px solid #b9bec6;
}

.lad-overlay-header button {
  border: 1px solid #b9bec6;
  background: #fff;
  border-radius: 4px;
  font-size: 12px;
  line-height: 1;
  padding: 4px 10px;
  cursor: pointer;
  color: #30343b;
}

.lad-overlay-header button:hover {
  background: #eef1f5;
}

.lad-overlay-body {
  overflow: auto;
  padding: 12px;
}

.lad-canvas {
  display: block;
  min-height: 224px;
  background-color: #fff;
  background-image: linear-gradient(#eef0f3 1px, transparent 1px), linear-gradient(90deg, #eef0f3 1px, transparent 1px);
  background-size: 16px 16px;
}

.powerrail,
.connection-wire {
  fill: none;
  stroke: #30343b;
  stroke-width: 2;
  stroke-linecap: square;
  stroke-linejoin: round;
}

.powerrail {
  stroke-width: 4;
}

/* 博图 LAD：左母线（火线）粗，右母线（零线）细 */
.powerrail-right {
  stroke-width: 2.5;
}

/* 能流/判断结果配色（鲜明区分）：绿实线=满足(true)，红虚线=未满足(false)，灰实线=未知/未执行；
   离线（无监控数据）不落任何状态类，保持黑色 */
.connection-wire.is-true {
  stroke: #008a00;
}

.connection-wire.is-false {
  stroke: #d92020;
  stroke-dasharray: 7 4;
}

.connection-wire.is-unknown {
  stroke: #8a8f98;
}

.endpoint-marker circle,
.endpoint-marker line {
  fill: #fff;
  stroke: #8b5a00;
  stroke-width: 2;
}

.endpoint-marker.is-unknown line {
  stroke: #a12f2f;
}

.endpoint-marker text {
  font-family: "Segoe UI", Arial, sans-serif;
  font-size: 10px;
  fill: #6b4a10;
}

.lad-symbol line,
.lad-symbol ellipse,
.lad-symbol path,
.lad-symbol rect {
  fill: none;
  stroke: #30343b;
  stroke-width: 2;
}

.lad-symbol rect {
  fill: #fff;
}

.lad-symbol text {
  font-family: "Segoe UI", Arial, sans-serif;
  pointer-events: none;
}

/* 字号整体提升：1:1 渲染下保证可读性 */
.symbol-label {
  font-size: 11px;
  fill: #1f2937;
}

.operand-label {
  font-size: 11px;
  font-weight: 600;
  fill: #1f2937;
}

.access-fallback text {
  font-family: Consolas, "Microsoft YaHei", sans-serif;
  font-size: 10px;
  fill: #a15c00;
}

.part-label {
  font-size: 12px;
  font-weight: 600;
  fill: #30343b;
}

.not-label {
  font-size: 10px;
  font-weight: 600;
  fill: #30343b;
}

.constant-label {
  font-size: 10px;
  fill: #4b5563;
}

.runtime-meta {
  font-size: 9px;
  fill: #4b5563;
}

/* 判断结果（真正计算值）：True=绿 / False=红 */
.runtime-judgment {
  font-size: 10px;
  font-weight: 700;
  fill: #4b5563;
}

.runtime-judgment.is-true,
.runtime-suffix.is-true {
  fill: #0a7a0a;
}

.runtime-judgment.is-false,
.runtime-suffix.is-false {
  fill: #d92020;
}

/* 元件与判断结果配色：true=绿 / false=红（鲜明区分） */
.lad-symbol.is-true line,
.lad-symbol.is-true ellipse,
.lad-symbol.is-true path,
.lad-symbol.is-true rect {
  stroke: #008a00;
}

.lad-symbol.is-true rect {
  fill: #e8f7e9;
}

.lad-symbol.is-false line,
.lad-symbol.is-false ellipse,
.lad-symbol.is-false path,
.lad-symbol.is-false rect {
  stroke: #d92020;
}

.lad-symbol.is-false rect {
  fill: #fdeaea;
}

.lad-symbol.is-unknown line,
.lad-symbol.is-unknown ellipse,
.lad-symbol.is-unknown path,
.lad-symbol.is-unknown rect {
  stroke: #7b828c;
  stroke-dasharray: 4 3;
}

.lad-symbol.is-unknown rect {
  fill: #eceff2;
}

.lad-symbol.is-unknown-part rect {
  fill: #dfe3e8;
}
</style>
