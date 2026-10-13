import { describe, expect, it } from 'vitest'
import {
  centeredView,
  clampImagePoint,
  fitView,
  separateBillboards,
  imageToScreen,
  moveBillboard,
  moveLineEnd,
  panView,
  screenToImage,
  serializeMarkers,
  zoomViewAt,
} from './markerGeometry'

describe('lineUp image marker geometry', () => {
  it('round trips original-image coordinates through the viewport', () => {
    const view = { scale: 1.75, translateX: 42, translateY: -18 }
    const image = { x: 320.5, y: 120.25 }
    const screen = imageToScreen(image, view)

    expect(screenToImage(screen, view)).toEqual(image)
  })

  it('zooms around the cursor without moving the image point under it', () => {
    const before = { scale: 1, translateX: 20, translateY: 30 }
    const cursor = { x: 420, y: 330 }
    const imagePoint = screenToImage(cursor, before)
    const after = zoomViewAt(before, cursor, 2)

    expect(after.scale).toBe(2)
    expect(imageToScreen(imagePoint, after)).toEqual(cursor)
  })

  it('pans only the viewport and never changes stored marker coordinates', () => {
    expect(panView({ scale: 2, translateX: 10, translateY: 20 }, 15, -8)).toEqual({
      scale: 2,
      translateX: 25,
      translateY: 12,
    })
  })

  it('clamps dragged points inside the image but lets billboards sit outside', () => {
    expect(clampImagePoint({ x: -5, y: 120 }, 100, 80)).toEqual({ x: 0, y: 80 })
    expect(moveBillboard({ x: 95, y: -76, width: 200, height: 12 }, 100, 80)).toEqual({
      x: 95,
      y: -76,
      width: 100,
      height: 12,
    })
  })

  it('moves only the selected line endpoint and clamps it to the image', () => {
    const line = { startX: 10, startY: 20, endX: 30, endY: 40 }

    expect(moveLineEnd(line, 'end', { x: 120, y: -3 }, 100, 80)).toEqual({
      startX: 10,
      startY: 20,
      endX: 100,
      endY: 0,
    })
  })

  it('serializes markers and exact BOM identities deterministically', () => {
    expect(serializeMarkers([
      { id: '31', markType: 'Point', isSingleMark: false, bomIds: ['202', '201', '202'] },
      { id: null, markType: 'Point', isSingleMark: true, bomIds: ['12'] },
    ])).toBe('[{"id":null,"markType":"Point","isSingleMark":true,"bomIds":["12"]},{"id":"31","markType":"Point","isSingleMark":false,"bomIds":["201","202"]}]')
  })

  it('scales a small image up to fill the canvas and centers it', () => {
    const view = fitView({ width: 1200, height: 800 }, 800, 600, 16)

    expect(view?.scale).toBeCloseTo(1.28, 6)
    expect(view?.translateX).toBeCloseTo(88, 6)
    expect(view?.translateY).toBeCloseTo(16, 6)
  })

  it('scales a large image down instead of overflowing the canvas', () => {
    const view = fitView({ width: 1000, height: 700 }, 2000, 1000, 16)

    expect(view?.scale).toBeCloseTo(0.484, 6)
    expect(view?.translateX).toBeCloseTo(16, 6)
    expect(view?.translateY).toBeCloseTo(108, 6)
  })

  it('applies a zoom factor on top of the fitted scale and stays centered', () => {
    const view = fitView({ width: 1200, height: 800 }, 800, 600, 16, 0.9)

    expect(view?.scale).toBeCloseTo(1.152, 6)
    expect(view?.translateX).toBeCloseTo((1200 - 800 * 1.152) / 2, 6)
    expect(view?.translateY).toBeCloseTo((800 - 600 * 1.152) / 2, 6)
  })

  it('centers at an absolute scale regardless of canvas fit', () => {
    const view = centeredView({ width: 1200, height: 800 }, 800, 600, 0.9)

    expect(view?.scale).toBe(0.9)
    expect(view?.translateX).toBeCloseTo((1200 - 720) / 2, 6)
    expect(view?.translateY).toBeCloseTo((800 - 540) / 2, 6)
    expect(centeredView({ width: 1200, height: 800 }, 0, 600, 0.9)).toBeNull()
  })

  it('separates overlapping billboards vertically with a gap', () => {
    const result = separateBillboards([
      { key: 'a', x: 0, y: 5, width: 120, height: 20 },
      { key: 'b', x: 10, y: 0, width: 120, height: 20 },
      { key: 'c', x: 500, y: 8, width: 120, height: 20 },
    ])
    const byKey = Object.fromEntries(result.map(r => [r.key, r]))

    expect(byKey.b.y).toBe(0)
    expect(byKey.a.y).toBe(30)
    expect(byKey.c.y).toBe(8)
  })

  it('clamps extreme ratios and rejects invalid image geometry', () => {
    expect(fitView({ width: 10000, height: 10000 }, 10, 10)?.scale).toBe(5)
    expect(fitView({ width: 100, height: 100 }, 1000, 1000)?.scale).toBe(0.2)
    expect(fitView({ width: 800, height: 600 }, 0, 600)).toBeNull()
    expect(fitView({ width: 30, height: 600 }, 100, 100)).toBeNull()
  })
})
