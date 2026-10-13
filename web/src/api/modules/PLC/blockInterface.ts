import api from '@/api/index'

const routerName = 'PLC_BlockInterface'

export default {
    list: (params: any) =>
        api.get(`${routerName}/GetListPaged`, {
            params: {
                page: params.page || 1,
                limit: params.limit || 10,
                sortField: params.sortField || 'Id',
                sortType: params.sortType || 'asc',
                ...params,
            },
        }),
    getAllByBlockId: (blockId: number | string) =>
        api.get(`${routerName}/GetList`, {
            params: {
                BlockId: blockId
            }
        })
}
