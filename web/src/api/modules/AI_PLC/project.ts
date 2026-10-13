import api from '@/api/index'

const routerName = 'Project'

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

  export: (data: any) =>
    api.get(`${routerName}/ExportToExcel`, {
      params: data,
    }),

  import: (data: FormData) =>
    api.post(`${routerName}/import`, data, {
      headers: {
        'Content-Type': 'multipart/form-data',
      },
    }),

  parseMetadata: (projectId: number | string, files: File | File[], projectName?: string, productionLineCode?: string) => {
    const formData = new FormData()
    // 支持单个文件或多个文件
    const fileArray = Array.isArray(files) ? files : [files]
    for (const file of fileArray) {
      formData.append('files', file)
    }
    if (projectName) {
      formData.append('projectName', projectName)
    }
    if (productionLineCode) {
      formData.append('productionLineCode', productionLineCode)
    }
    return api.post(`${routerName}/ParseMetadata/${projectId}`, formData, {
      headers: {
        'Content-Type': 'multipart/form-data',
      },
    })
  },
}
