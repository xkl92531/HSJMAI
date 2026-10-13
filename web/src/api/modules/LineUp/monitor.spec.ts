import type { Mock } from 'vitest'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import api from '@/api/index'
import lineUpMonitorApi from './monitor'

vi.mock('@/api/index', () => ({
  default: {
    get: vi.fn(),
    post: vi.fn(),
    put: vi.fn(),
  },
}))

const mockedPost = api.post as Mock
const mockedPut = api.put as Mock

describe('lineUp monitor API', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('prepares a runtime binding through the authenticated LineUp endpoint', async () => {
    mockedPost.mockResolvedValue({
      isSuccess: true,
      data: {
        bindingId: '9001',
        bindingStatus: 'NeedsReview',
        definitionRevision: 'revision-1',
        tagCount: 20,
        canValidate: true,
        issues: [],
      },
    })

    const result = await lineUpMonitorApi.prepareBinding('9001')

    expect(mockedPost).toHaveBeenCalledWith('LineUp/RuntimeBindings/9001/Prepare')
    expect(result.canValidate).toBe(true)
  })

  it('validates a prepared binding online and enables monitoring explicitly', async () => {
    mockedPost.mockResolvedValue({
      isSuccess: true,
      data: {
        bindingId: '9001',
        bindingStatus: 'Resolved',
        goodCount: 20,
        totalCount: 20,
        canEnable: true,
        issues: [],
      },
    })
    mockedPut.mockResolvedValue({
      isSuccess: true,
      data: {
        bindingId: '9001',
        bindingStatus: 'Resolved',
        monitoringEnabled: true,
        globalMonitorEnabled: true,
      },
    })

    const validation = await lineUpMonitorApi.validateBinding('9001')
    const enabled = await lineUpMonitorApi.setMonitoring('9001', true)

    expect(mockedPost).toHaveBeenCalledWith('LineUp/RuntimeBindings/9001/Validate')
    expect(mockedPut).toHaveBeenCalledWith('LineUp/RuntimeBindings/9001/Monitoring', { enabled: true })
    expect(validation.goodCount).toBe(validation.totalCount)
    expect(enabled.monitoringEnabled).toBe(true)
  })
})
