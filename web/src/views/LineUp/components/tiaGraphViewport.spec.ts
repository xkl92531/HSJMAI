import { describe, expect, it, vi } from 'vitest'
import {
  fitTiaGraphViewport,
  resolveTiaGraphCanvasSize,
  tiaGraphLeftPadding,
  tiaGraphMinimumReadableScale,
  tiaGraphTopPadding,
} from './tiaGraphViewport'

describe('resolveTiaGraphCanvasSize', () => {
  it('uses the visible parent size when X6 has initialized its surface at one pixel', () => {
    expect(resolveTiaGraphCanvasSize(
      { width: 1, height: 1 },
      { width: 746.8, height: 420 },
    )).toEqual({ width: 747, height: 420 })
  })
})

describe('fitTiaGraphViewport', () => {
  it('anchors the runtime graph to the left after fitting it into its pane', () => {
    const anchorLeft = vi.fn()
    const shiftX = vi.fn()
    fitTiaGraphViewport({
      fit: vi.fn(),
      zoom: () => 1,
      anchorTop: vi.fn(),
      shiftY: vi.fn(),
      anchorLeft,
      shiftX,
    }, { alignLeft: true })

    expect(anchorLeft).toHaveBeenCalledOnce()
    expect(shiftX).toHaveBeenCalledWith(tiaGraphLeftPadding)
  })

  it('keeps long GRAPH sequences readable and starts from the top', () => {
    const fit = vi.fn()
    const anchorTop = vi.fn()
    const shiftY = vi.fn()

    fitTiaGraphViewport({ fit, zoom: () => tiaGraphMinimumReadableScale, anchorTop, shiftY })

    expect(fit).toHaveBeenCalledWith({
      padding: 32,
      minScale: tiaGraphMinimumReadableScale,
      maxScale: 1,
    })
    expect(anchorTop).toHaveBeenCalledOnce()
    expect(shiftY).toHaveBeenCalledWith(tiaGraphTopPadding)
  })

  it('retains normal fit behavior for compact sequences', () => {
    const anchorTop = vi.fn()
    const shiftY = vi.fn()

    fitTiaGraphViewport({ fit: vi.fn(), zoom: () => 1, anchorTop, shiftY })

    expect(anchorTop).not.toHaveBeenCalled()
    expect(shiftY).not.toHaveBeenCalled()
  })
})
