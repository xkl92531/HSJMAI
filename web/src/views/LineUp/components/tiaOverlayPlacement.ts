export type TiaOverlayDirection = 'above' | 'right' | 'left' | 'below'

export interface TiaOverlayRect {
  x: number
  y: number
  width: number
  height: number
}

export interface TiaOverlayPlacementInput {
  canvas: TiaOverlayRect
  node: TiaOverlayRect
  reservedRight: number
  alignRight?: boolean
}

export interface TiaOverlayPlacement {
  direction: TiaOverlayDirection
  left: number
  top: number
  maxWidth: number
  maxHeight: number
}

export interface TiaOverlayDragInput {
  placement: TiaOverlayPlacement
  deltaX: number
  deltaY: number
  canvas: TiaOverlayRect
  reservedRight: number
}

const padding = 12
const minimumPanelWidth = 280
const preferredPanelWidth = 720
const preferredPanelHeight = 440
/** 预览弹框（非 alignRight）场景的紧凑面板尺寸 */
const compactPanelWidth = 760
const compactPanelHeight = 440
/** 节点上方至少要有这么多高度才走 above（再小面板会缩成一条缝） */
const minimumAboveHeight = 200

function nonNegative(value: number) {
  return Number.isFinite(value) && value > 0 ? value : 0
}

function finite(value: number) {
  return Number.isFinite(value) ? value : 0
}

function clamp(value: number, minimum: number, maximum: number) {
  if (maximum <= minimum) {
    return minimum
  }
  return Math.min(Math.max(value, minimum), maximum)
}

function panelWidth(desiredWidth: number, availableWidth: number) {
  if (availableWidth <= 0) {
    return 0
  }
  return clamp(nonNegative(desiredWidth), Math.min(minimumPanelWidth, availableWidth), availableWidth)
}

export function dragTiaOverlay(input: TiaOverlayDragInput): TiaOverlayPlacement {
  const canvasX = finite(input.canvas.x)
  const canvasY = finite(input.canvas.y)
  const canvasWidth = nonNegative(input.canvas.width)
  const canvasHeight = nonNegative(input.canvas.height)
  const usableCanvasWidth = Math.max(0, canvasWidth - nonNegative(input.reservedRight))
  const safePadding = Math.min(padding, usableCanvasWidth / 2, canvasHeight / 2)
  const maxWidth = Math.min(nonNegative(input.placement.maxWidth), Math.max(0, usableCanvasWidth - safePadding * 2))
  const maxHeight = Math.min(nonNegative(input.placement.maxHeight), Math.max(0, canvasHeight - safePadding * 2))
  const minLeft = canvasX + safePadding
  const minTop = canvasY + safePadding
  const maxLeft = canvasX + usableCanvasWidth - safePadding - maxWidth
  const maxTop = canvasY + canvasHeight - safePadding - maxHeight

  return {
    direction: input.placement.direction,
    left: clamp(finite(input.placement.left) + finite(input.deltaX), minLeft, maxLeft),
    top: clamp(finite(input.placement.top) + finite(input.deltaY), minTop, maxTop),
    maxWidth,
    maxHeight,
  }
}

export function placeTiaOverlay(input: TiaOverlayPlacementInput): TiaOverlayPlacement {
  const canvasX = finite(input.canvas.x)
  const canvasY = finite(input.canvas.y)
  const canvasWidth = nonNegative(input.canvas.width)
  const canvasHeight = nonNegative(input.canvas.height)
  const usableCanvasWidth = Math.max(0, canvasWidth - nonNegative(input.reservedRight))
  const safePadding = Math.min(padding, usableCanvasWidth / 2, canvasHeight / 2)
  const minLeft = canvasX + safePadding
  const maxRight = canvasX + usableCanvasWidth - safePadding
  const minTop = canvasY + safePadding
  const maxBottom = canvasY + canvasHeight - safePadding
  const availableWidth = Math.max(0, maxRight - minLeft)
  const availableHeight = Math.max(0, maxBottom - minTop)
  const nodeLeft = clamp(finite(input.node.x), minLeft, maxRight)
  const nodeRight = clamp(finite(input.node.x) + nonNegative(input.node.width), minLeft, maxRight)
  const nodeBottom = clamp(finite(input.node.y) + nonNegative(input.node.height), minTop, maxBottom)
  const rightWidth = Math.max(0, maxRight - nodeRight)
  const leftWidth = Math.max(0, nodeLeft - minLeft)
  const canUseSide = (width: number) => width > 0 && width >= Math.min(minimumPanelWidth, availableWidth)
  // 预览弹框（非 alignRight）：浮层悬浮在节点正上方，空间不足再退回右侧/左侧/下方
  const aboveHeight = Math.max(0, finite(input.node.y) - minTop)
  // aboveHeight 必须真大于 0：退化场景（NaN/极窄画布把阈值压成 0）下 0>=0 会误选 above，
  // 与"空间不足再退回"的语义矛盾（2026-09-11 修复：极窄画布仍返回 above 的越界退化）
  const canUseAbove = !input.alignRight && aboveHeight > 0 && aboveHeight >= Math.min(minimumAboveHeight, availableHeight)
  const direction: TiaOverlayDirection = canUseAbove
    ? 'above'
    : canUseSide(rightWidth)
      ? 'right'
      : canUseSide(leftWidth)
        ? 'left'
        : 'below'
  const desiredWidth = input.alignRight ? preferredPanelWidth : compactPanelWidth
  const desiredHeight = input.alignRight ? preferredPanelHeight : compactPanelHeight
  const width = panelWidth(desiredWidth, availableWidth)
  const height = Math.min(
    desiredHeight,
    direction === 'below'
      ? Math.max(0, maxBottom - nodeBottom)
      : direction === 'above'
        ? aboveHeight
        : availableHeight,
  )
  const left = clamp(
    direction === 'above'
      ? finite(input.node.x) + nonNegative(input.node.width) / 2 - width / 2
      : input.alignRight && direction !== 'below'
        ? maxRight - width
        : direction === 'right'
          ? nodeRight
          : direction === 'left'
            ? nodeLeft - width
            : nodeLeft,
    minLeft,
    maxRight - width,
  )
  const top = direction === 'above'
    ? finite(input.node.y) - height
    : direction === 'below'
      ? nodeBottom
      : clamp(finite(input.node.y), minTop, maxBottom - height)

  return { direction, left, top, maxWidth: width, maxHeight: height }
}
