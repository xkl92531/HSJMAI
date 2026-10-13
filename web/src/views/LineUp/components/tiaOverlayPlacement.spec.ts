import type { TiaOverlayPlacement, TiaOverlayRect } from './tiaOverlayPlacement'
import { describe, expect, it } from 'vitest'
import { dragTiaOverlay, placeTiaOverlay } from './tiaOverlayPlacement'

function rect(x: number, y: number, width: number, height: number): TiaOverlayRect {
  return { x, y, width, height }
}

function expectInsideAvailableCanvas(
  placement: TiaOverlayPlacement,
  canvas: TiaOverlayRect,
  reservedRight: number,
) {
  const padding = 12
  const right = canvas.x + canvas.width - reservedRight - padding
  const bottom = canvas.y + canvas.height - padding

  expect(placement.left).toBeGreaterThanOrEqual(canvas.x + padding)
  expect(placement.top).toBeGreaterThanOrEqual(canvas.y + padding)
  expect(placement.left + placement.maxWidth).toBeLessThanOrEqual(right)
  expect(placement.top + placement.maxHeight).toBeLessThanOrEqual(bottom)
}

describe('tia overlay placement', () => {
  it('moves a dragged panel by the pointer delta without changing its size', () => {
    expect(dragTiaOverlay({
      placement: { direction: 'right', left: 500, top: 40, maxWidth: 720, maxHeight: 440 },
      deltaX: -180,
      deltaY: 120,
      canvas: rect(0, 0, 1400, 700),
      reservedRight: 0,
    })).toEqual({
      direction: 'right',
      left: 320,
      top: 160,
      maxWidth: 720,
      maxHeight: 440,
    })
  })

  it('keeps a dragged panel inside the usable canvas area', () => {
    const placement = { direction: 'right', left: 500, top: 40, maxWidth: 720, maxHeight: 440 } as const
    const canvas = rect(0, 0, 1400, 700)

    expect(dragTiaOverlay({
      placement,
      deltaX: 5000,
      deltaY: 5000,
      canvas,
      reservedRight: 120,
    })).toMatchObject({ left: 548, top: 248 })
    expect(dragTiaOverlay({
      placement,
      deltaX: -5000,
      deltaY: -5000,
      canvas,
      reservedRight: 120,
    })).toMatchObject({ left: 12, top: 12 })
  })

  it('prefers the left side when the right side is too narrow but the left can host a panel', () => {
    const canvas = rect(0, 0, 900, 600)
    const placement = placeTiaOverlay({
      canvas,
      node: rect(320, 180, 120, 48),
      reservedRight: 360,
    })

    expect(placement.direction).toBe('left')
    expectInsideAvailableCanvas(placement, canvas, 360)
  })

  it('prefers the right side when it can host the panel', () => {
    const canvas = rect(0, 0, 1200, 700)
    const placement = placeTiaOverlay({
      canvas,
      node: rect(240, 160, 120, 48),
      reservedRight: 320,
    })

    expect(placement.direction).toBe('right')
    expectInsideAvailableCanvas(placement, canvas, 320)
  })

  it('places the panel below when neither side can host its minimum width', () => {
    const canvas = rect(0, 0, 640, 480)
    const sourceNode = rect(260, 160, 120, 48)
    const placement = placeTiaOverlay({
      canvas,
      node: sourceNode,
      reservedRight: 0,
    })

    expect(placement.direction).toBe('below')
    expect(placement.top).toBeGreaterThanOrEqual(sourceNode.y + sourceNode.height)
    expect(placement.maxHeight).toBeLessThanOrEqual(480 - 12 - (sourceNode.y + sourceNode.height))
    expectInsideAvailableCanvas(placement, canvas, 0)
  })

  it('returns zero below height instead of overlapping the node when no lower space remains', () => {
    const sourceNode = rect(260, 170, 120, 18)
    const placement = placeTiaOverlay({
      canvas: rect(0, 0, 640, 200),
      node: sourceNode,
      reservedRight: 0,
    })

    expect(placement.direction).toBe('below')
    expect(placement.top).toBe(sourceNode.y + sourceNode.height)
    expect(placement.maxHeight).toBe(0)
  })

  it('normalizes non-finite and negative geometry without returning an out-of-bounds rectangle', () => {
    const placement = placeTiaOverlay({
      canvas: rect(Number.NaN, Number.POSITIVE_INFINITY, -200, Number.NaN),
      node: rect(Number.NEGATIVE_INFINITY, Number.NaN, -40, Number.POSITIVE_INFINITY),
      reservedRight: Number.POSITIVE_INFINITY,
    })

    expect(placement).toEqual({ direction: 'below', left: 0, top: 0, maxWidth: 0, maxHeight: 0 })
  })

  it('uses the geometrically available size on an extremely narrow viewport', () => {
    const canvas = rect(0, 0, 20, 16)
    const placement = placeTiaOverlay({
      canvas,
      node: rect(5, 4, 10, 8),
      reservedRight: 0,
    })

    expect(placement.direction).toBe('below')
    expect(placement.maxWidth).toBeLessThanOrEqual(canvas.width)
    expect(placement.maxHeight).toBeLessThanOrEqual(canvas.height)
    expect(placement.left).toBeGreaterThanOrEqual(canvas.x)
    expect(placement.top).toBeGreaterThanOrEqual(canvas.y)
    expect(placement.left + placement.maxWidth).toBeLessThanOrEqual(canvas.x + canvas.width)
    expect(placement.top + placement.maxHeight).toBeLessThanOrEqual(canvas.y + canvas.height)
  })

  it('uses the larger TIA detail size when the canvas has enough room', () => {
    const canvas = rect(0, 0, 1600, 800)
    const placement = placeTiaOverlay({
      canvas,
      node: rect(240, 160, 120, 48),
      reservedRight: 320,
    })

    expect(placement).toMatchObject({
      direction: 'right',
      // 预览弹框（非 alignRight）走紧凑面板宽 760（39fc742 起）；alignRight 运行详情仍为 720
      maxWidth: 760,
      maxHeight: 440,
    })
    expectInsideAvailableCanvas(placement, canvas, 320)
  })

  it('places a runtime detail panel at the right edge while keeping it floating', () => {
    const canvas = rect(0, 0, 1400, 700)
    const placement = placeTiaOverlay({
      canvas,
      node: rect(180, 60, 120, 48),
      reservedRight: 0,
      alignRight: true,
    })

    expect(placement).toMatchObject({
      direction: 'right',
      left: 668,
      top: 60,
      maxWidth: 720,
      maxHeight: 440,
    })
    expectInsideAvailableCanvas(placement, canvas, 0)
  })

  it('keeps the preferred panel width when one side is usable but narrower than the full canvas', () => {
    const canvas = rect(0, 0, 1320, 480)
    const placement = placeTiaOverlay({
      canvas,
      node: rect(660, 72, 120, 48),
      reservedRight: 0,
    })

    expect(placement.direction).toBe('right')
    // 预览弹框紧凑宽 760（39fc742 起），不再回退 720
    expect(placement.maxWidth).toBe(760)
    expect(placement.maxHeight).toBe(440)
    expectInsideAvailableCanvas(placement, canvas, 0)
  })
})
