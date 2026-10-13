import api from '@/api/index'

const routerName = 'PLC_Project'

export default {
    list: (data: { page: number, limit: number, ProjectName?: string }) =>
        api.get(`${routerName}/GetListPaged`, {
            params: data,
        }),

    detail: (id: number | string) =>
        api.get(`${routerName}/Get`, {
            params: {
                id,
            },
        }),

    create: (data: any) => api.post(`${routerName}/Insert`, data, {}),

    edit: (data: any) =>
        api.post(`${routerName}/UpdateById/${data.id}`, data, {}),

    delete: (id: number | string) => api.delete(`${routerName}/Delete/${id}`, {}),

    batchDelete: (ids: any) =>
        api.delete(`${routerName}/DeleteBatch`, {
            data: ids,
        }),

    // 产线下拉选项（数据源：基础资料表 Basic_ProductionLine）
    productionLines: () => api.get(`${routerName}/GetProductionLineOptions`),

    // PLC 设备下拉选项（数据源：基础资料表 Basic_Device；可按产线过滤）
    plcDevices: (productionLineId?: number | string) =>
        api.get(`${routerName}/GetPlcDeviceOptions`, {
            params: { productionLineId },
        }),

    export: (data: any) =>
        api.get(`${routerName}/ExportToExcel`, {
            params: data,
        }),
}
