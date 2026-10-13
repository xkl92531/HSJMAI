import api from '@/api/index'

const routerName = 'Enum'

export default {
  getList: (enumName: string) => api.get(`${routerName}/getList?v=${new Date().getTime()}&enumName=${enumName}`),

}
