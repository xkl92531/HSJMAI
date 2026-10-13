import { describe, expect, it } from 'vitest'
import { resolveTiaLadViewport } from './tiaLadViewport'

describe('tIA LAD viewport', () => {
  it('renders at natural pixel size by default so text stays readable', () => {
    expect(resolveTiaLadViewport(872, 256)).toEqual({
      viewBox: '0 0 872 256',
      style: {
        width: '872px',
        height: '256px',
      },
    })
  })

  it('scales the pixel size by the requested zoom factor', () => {
    expect(resolveTiaLadViewport(400, 200, 1.5)).toEqual({
      viewBox: '0 0 400 200',
      style: {
        width: '600px',
        height: '300px',
      },
    })
  })

  it('normalizes invalid dimensions without overflowing pixel sizes', () => {
    expect(resolveTiaLadViewport(Number.NaN, -40)).toEqual({
      viewBox: '0 0 0 0',
      style: {
        width: '1px',
        height: '1px',
      },
    })
  })
})
