import { describe, expect, it, vi } from 'vitest'
import { settleRouteGenerationFailure } from './routeGenerationFallback'

describe('settleRouteGenerationFailure', () => {
  it('settles a failed backend route load so retry navigation cannot loop forever', () => {
    const generateRoutesAtFront = vi.fn()

    settleRouteGenerationFailure({ isGenerate: false, generateRoutesAtFront })

    expect(generateRoutesAtFront).toHaveBeenCalledOnce()
    expect(generateRoutesAtFront).toHaveBeenCalledWith([])
  })

  it('does not replace an already generated route table', () => {
    const generateRoutesAtFront = vi.fn()

    settleRouteGenerationFailure({ isGenerate: true, generateRoutesAtFront })

    expect(generateRoutesAtFront).not.toHaveBeenCalled()
  })
})
