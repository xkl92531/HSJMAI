import api from '../index'

export default {
  // 登录
  login: (data: {
    account: string
    password: string
  }) => api.post('auth/login', data, {
  }),

  // 获取权限
  permission: () => api.get('SysUser/GetPermissions', {
  }),

  // 修改密码
  passwordEdit: (data: {
    password: string
    newPassword: string
    checkPassword: string
  }) => api.post('SysUser/ChangePassword', { OldPwd: data.password, NewPwd: data.newPassword, ConfirmPwd: data.checkPassword }, {
  }),

  // 获取偏好设置
  preferences: () => api.get('user/preferences', {
    baseURL: '/mock/',
  }),

  // 修改偏好设置
  preferencesEdit: (preferences: string) => api.post('user/preferences/edit', {
    preferences,
  }, {
    baseURL: '/mock/',
  }),

  // 获取标签栏固定标签页数据
  tabbar: () => api.get('user/tabbar', {
    baseURL: '/mock/',
  }),

  // 修改标签栏固定标签页数据
  tabbarEdit: (tabbar: string) => api.post('user/tabbar/edit', {
    tabbar,
  }, {
    baseURL: '/mock/',
  }),

  // 获取收藏夹
  favorites: () => api.get('user/favorites', {
    baseURL: '/mock/',
  }),

  // 修改收藏夹
  favoritesEdit: (favorites: string) => api.post('user/favorites/edit', {
    favorites,
  }, {
    baseURL: '/mock/',
  }),

  list: (data: { title?: string, from: number, limit: number }) =>
    api.get(`SysUser/getListPaged`, {
      params: data,
    }),

  detail: (id: number | string) =>
    api.get(`SysUser/get`, {
      params: {
        id,
      },
    }),
  details: data =>
    api.get(`SysUser/getList`, {
      params: data,
    }),
  create: (data: any) => api.post(`SysUser/Insert`, data, {}),

  edit: (data: any) =>
    api.post(`SysUser/UpdateById/${data.id}`, data, {}),

  delete: (id: number | string) => api.delete(`SysUser/delete/${id}`, {}),

  batchDelete: (ids: any) =>
    api.delete(`SysUser/deleteBatch`, {
      data: ids,
    }),

  getEntity: (id: number | string) =>
    api.get(`SysUser/getEntity/${id}`, {
      params: {
        id,
      },
    }),

  passwordReset: (data: any) => api.post(`SysUser/PasswordReset`, data, {}),

  updateStatus: (data: any) => api.post(`SysUser/UpdateStatus`, data, {}),

  savePur: (data: any) => api.post(`SysUser/SavePur`, data, {}),
}
