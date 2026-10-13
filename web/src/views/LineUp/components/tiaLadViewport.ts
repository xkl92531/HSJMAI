export interface TiaLadViewport {
  viewBox: string
  style: {
    width: string
    height: string
  }
}

function dimension(value: number) {
  return Number.isFinite(value) && value > 0 ? value : 0
}

/**
 * 博图式视口：SVG 按"逻辑尺寸 × 缩放"的像素尺寸渲染（默认 1:1 原始大小），
 * 面板负责横向滚动 —— 不再整体缩到面板宽里把字压小。
 */
export function resolveTiaLadViewport(width: number, height: number, scale = 1): TiaLadViewport {
  const logicalWidth = dimension(width)
  const logicalHeight = dimension(height)
  const safeScale = Number.isFinite(scale) && scale > 0 ? scale : 1

  return {
    viewBox: `0 0 ${logicalWidth} ${logicalHeight}`,
    style: {
      width: Math.max(1, Math.round(logicalWidth * safeScale)) + 'px',
      height: Math.max(1, Math.round(logicalHeight * safeScale)) + 'px',
    },
  }
}
