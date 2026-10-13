export const tiaGraphMinimumReadableScale = 0.8
export const tiaGraphTopPadding = 24
export const tiaGraphLeftPadding = 32

interface TiaGraphCanvasBounds {
  width: number
  height: number
}

export function resolveTiaGraphCanvasSize(
  ownBounds?: TiaGraphCanvasBounds | null,
  parentBounds?: TiaGraphCanvasBounds | null,
) {
  const parentWidth = parentBounds?.width ?? 0
  const parentHeight = parentBounds?.height ?? 0
  const preferredWidth = parentWidth > 1 ? parentWidth : (ownBounds?.width ?? 0)
  const preferredHeight = parentHeight > 1 ? parentHeight : (ownBounds?.height ?? 0)
  return {
    width: Math.max(1, Math.round(preferredWidth)),
    height: Math.max(1, Math.round(preferredHeight)),
  }
}

export interface TiaGraphViewportDriver {
  fit: (options: { padding: number, minScale: number, maxScale: number }) => void
  zoom: () => number
  anchorTop: () => void
  shiftY: (delta: number) => void
  anchorLeft?: () => void
  shiftX?: (delta: number) => void
}

export function fitTiaGraphViewport(
  driver: TiaGraphViewportDriver,
  options: { alignLeft?: boolean } = {},
) {
  driver.fit({
    padding: 32,
    minScale: tiaGraphMinimumReadableScale,
    maxScale: 1,
  })

  if (driver.zoom() <= tiaGraphMinimumReadableScale) {
    driver.anchorTop()
    driver.shiftY(tiaGraphTopPadding)
  }
  if (options.alignLeft) {
    driver.anchorLeft?.()
    driver.shiftX?.(tiaGraphLeftPadding)
  }
}
