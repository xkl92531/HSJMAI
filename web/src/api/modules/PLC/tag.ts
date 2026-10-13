import api from '@/api/index'

const routerName = 'PLC_IOTag'

export default {
    list: (params: any) =>
        api.get(`${routerName}/GetListPaged`, {
            params: {
                page: params.page,
                limit: params.limit,
                sortField: params.sortField || 'Id',
                sortType: params.sortType || 'desc',
                ...params,
            },
        }),

    detail: (id: number | string) => api.get(`${routerName}/Get`, { params: { id } }),

    parseByAI: (id: number | string) => api.post(`${routerName}/ParseIOTagByAI/${id}`),
}
