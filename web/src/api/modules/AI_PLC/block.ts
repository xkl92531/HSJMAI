import api from '@/api/index'

const routerName = '/Block'

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

    delete: (id: number | string) => api.delete(`${routerName}/Delete/${id}`),

    batchDelete: (ids: any[]) => api.delete(`${routerName}/DeleteBatch`, { data: ids }),
}
