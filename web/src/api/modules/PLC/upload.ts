import api from '@/api/index'

const routerName = 'PLC_Upload'

export default {
  // 同步上传（保留兼容）
  uploadAndParse: (formData: FormData) =>
    api.post(`${routerName}/UploadAndParse`, formData, {
      headers: {
        'Content-Type': 'multipart/form-data',
      },
    }),

  // 异步后台上传（推荐）
  startBackgroundUpload: (formData: FormData) =>
    api.post(`${routerName}/StartBackgroundUpload`, formData, {
      headers: {
        'Content-Type': 'multipart/form-data',
      },
    }),

  // 查询上传进度
  getProgress: (taskId: string) =>
    api.get(`${routerName}/GetProgress`, {
      params: { taskId },
    }),

  uploadIOTagsCsv: (formData: FormData) =>
    api.post(`${routerName}/UploadIOTagsCsv`, formData, {
      headers: {
        'Content-Type': 'multipart/form-data',
      },
    }),

  // 批量注册 PLC 标签（V2：按项目 ioturl 自动分派网关类型——
  // 新 IGW → 符号式 /Tag/AddTags 注册；老网关 → 委托原 ExecBatchRegistrationTag，行为不变）
  execBatchRegistrationTag: (id: any) =>
    api.post(`${routerName}/ExecBatchRegistrationTagV2/${id}`, {
    }),

  // 重跑单个 LU 的约束（XMLV2：用项目保存的 XML 包全项目生成，只替换该 LU 所在程序段的约束；
  // 没有保存包 / 映射不上直接失败、不改任何东西）。refreshAi=true 时该范围的普通网络 AI 与规则措辞不走缓存
  rerunSingleLu: (luId: number | string, refreshAi = false) =>
    api.post(`${routerName}/RerunSingleLu/${luId}`, {}, { params: { refreshAi } }),

  // 重跑单个程序块的约束（XMLV2：同上，范围 = 该块全部程序段）
  rerunBlock: (blockId: number | string, refreshAi = false) =>
    api.post(`${routerName}/RerunBlock/${blockId}`, {}, { params: { refreshAi } }),

  // 重跑 GRAPH 步位注册（判定 → 台账 → 网关注册 → 读值验证），免重新上传 zip。
  // 工站绑定关系变更后按最新绑定重新分组注册时使用
  rerunGraphStepRegistration: (projectId: number | string) =>
    api.post(`${routerName}/RerunGraphStepRegistration/${projectId}`, {}),
}
