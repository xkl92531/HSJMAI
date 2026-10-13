import api from '@/api/index'

const routerName = 'PLC_LogicUnit'

export default {
    list: (params: any) =>
        api.get(`${routerName}/GetListPaged`, {
            params: {
                page: params.page,
                limit: params.limit,
                sortField: params.sortField || 'NetworkId',
                sortType: params.sortType || 'asc',
                ...params,
            },
        }),

    // Get all logic units for a block (unpaged if needed, or use large limit)
    getByBlockId: (blockId: number | string) =>
        api.get(`${routerName}/GetList`, {
            params: {
                BlockId: blockId
            }
        }),

    getWriterFragmentsPaged: (params: any) =>
        api.get(`${routerName}/GetWriterFragmentsPaged`, {
            params: {
                page: params.page,
                limit: params.limit,
                sortField: params.sortField || 'WriteVarsCount',
                sortType: params.sortType || 'desc',
                ...params,
            },
        }),

    getWriterFragments: (params: any) =>
        api.get(`${routerName}/GetWriterFragments`, {
            params,
        })
}
