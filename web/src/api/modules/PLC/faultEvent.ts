import api from '@/api/index'

const routerName = 'PLC_FaultEvent'

export default {
  list: (params: {
    page: number
    limit: number
    projectId?: number | string
    variableName?: string
    onlyOpen?: boolean
    startTime?: string
    endTime?: string
  }) => api.get(`${routerName}/GetListPaged`, { params }),

  pull: (params: { projectId: number | string; afterTime?: string; limit?: number }) =>
    api.get(`${routerName}/Pull`, { params }),
}
