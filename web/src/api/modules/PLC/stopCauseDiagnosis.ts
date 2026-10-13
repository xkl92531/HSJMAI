import api from '@/api/index'

const routerName = 'StopCauseDiagnosis'
const requestConfig = {
  timeout: 1000 * 60 * 10,
}

export default {
  extractStations: (data: any) =>
    api.post(`${routerName}/extract-stations`, data, requestConfig),

  previewStationMap: (data: any) =>
    api.post(`${routerName}/preview-station-map`, data, requestConfig),

  discoverAnchors: (data: any) =>
    api.post(`${routerName}/discover-anchors`, data, requestConfig),

  compilePreview: (data: any) =>
    api.post(`${routerName}/compile-preview`, data, requestConfig),

  startFullAnalysis: (data: any) =>
    api.post(`${routerName}/start-full-analysis`, data, requestConfig),

  getFullAnalysisProgress: (params: any) =>
    api.get(`${routerName}/full-analysis-progress`, {
      params,
      ...requestConfig,
    }),

  getPersistedWriterAstDetails: (params: any) =>
    api.get(`${routerName}/persisted-writer-ast-details`, {
      params,
      ...requestConfig,
    }),

  getPersistedWriterAstPaged: (params: any) =>
    api.get(`${routerName}/persisted-writer-ast-paged`, {
      params,
      ...requestConfig,
    }),

  getPersistedStationMap: (params: any) =>
    api.get(`${routerName}/persisted-station-map`, {
      params,
      ...requestConfig,
    }),

  getPersistedSemanticFindingsPaged: (params: any) =>
    api.get(`${routerName}/persisted-semantic-findings-paged`, {
      params,
      ...requestConfig,
    }),

  getDeviceFocusSignals: (params: any) =>
    api.get(`${routerName}/device-focus-signals`, {
      params,
      ...requestConfig,
    }),

  refreshDeviceFocusSignals: (data: any) =>
    api.post(`${routerName}/device-focus-signals/refresh`, data, requestConfig),

  getAstSemanticProgress: (params: any) =>
    api.get(`${routerName}/ast-semantic-progress`, {
      params,
      ...requestConfig,
    }),

  getMonitoringStations: (params: any) =>
    api.get(`${routerName}/monitoring/stations`, {
      params,
      ...requestConfig,
    }),

  getAllStationChanges: (params: any) =>
    api.get(`${routerName}/monitoring/all-station-changes`, {
      params,
      ...requestConfig,
    }),

  getMonitoringStationOverview: (params: any) =>
    api.get(`${routerName}/monitoring/station-overview`, {
      params,
      ...requestConfig,
    }),

  getMonitoringSignals: (params: any) =>
    api.get(`${routerName}/monitoring/signals`, {
      params,
      ...requestConfig,
    }),

  getMonitoringChanges: (params: any) =>
    api.get(`${routerName}/monitoring/changes`, {
      params,
      ...requestConfig,
    }),

  getMonitoringBundles: (params: any) =>
    api.get(`${routerName}/monitoring/bundles`, {
      params,
      ...requestConfig,
    }),

  getMonitoringEvents: (params: any) =>
    api.get(`${routerName}/monitoring/events`, {
      params,
      ...requestConfig,
    }),

  getMonitoringHistory: (params: any) =>
    api.get(`${routerName}/monitoring/history`, {
      params,
      ...requestConfig,
    }),

  getMonitoringSampleLibrary: (params: any) =>
    api.get(`${routerName}/monitoring/sample-library`, {
      params,
      ...requestConfig,
    }),

  getMonitoringPatternTemplates: (params: any) =>
    api.get(`${routerName}/monitoring/pattern-templates`, {
      params,
      ...requestConfig,
    }),

  rebuildMonitoringPatternTemplates: (data: any) =>
    api.post(`${routerName}/monitoring/pattern-templates/rebuild`, data, requestConfig),

  diagnosePendingMonitoringEvents: (data: any) =>
    api.post(`${routerName}/monitoring/diagnose-pending`, data, requestConfig),

  getMonitoringSnapshot: (params: any) =>
    api.get(`${routerName}/monitoring/snapshot`, {
      params,
      ...requestConfig,
    }),

  runMonitoringCycle: (data: any) =>
    api.post(`${routerName}/monitoring/run-cycle`, data, requestConfig),

  seedMonitoringSignals: (data: any) =>
    api.post(`${routerName}/monitoring/seed-signals`, data, requestConfig),

  batchSeedMonitoringSignals: (data: any) =>
    api.post(`${routerName}/monitoring/batch-seed-signals`, data, requestConfig),

  liveReadMonitoringSignals: (data: any) =>
    api.post(`${routerName}/monitoring/live-read`, data, requestConfig),

  batchDiscover: (data: any) =>
    api.post(`${routerName}/batch-discover`, data, requestConfig),

  // ==================== 工位监控配置 CRUD ====================
  getMonitoringConfigs: (params: any) =>
    api.get(`${routerName}/monitoring/configs`, { params, ...requestConfig }),

  createMonitoringConfig: (data: any) =>
    api.post(`${routerName}/monitoring/configs`, data, requestConfig),

  updateMonitoringConfig: (data: any) =>
    api.put(`${routerName}/monitoring/configs`, data, requestConfig),

  deleteMonitoringConfig: (id: number) =>
    api.delete(`${routerName}/monitoring/configs/${id}`, requestConfig),

  toggleMonitoringConfig: (id: number, enabled: boolean) =>
    api.post(`${routerName}/monitoring/configs/${id}/toggle?enabled=${enabled}`, null, requestConfig),

  // ==================== Worker 运行时控制 ====================
  getWorkerStatus: () =>
    api.get(`${routerName}/monitoring/worker/status`, requestConfig),

  pauseWorker: () =>
    api.post(`${routerName}/monitoring/worker/pause`, null, requestConfig),

  resumeWorker: () =>
    api.post(`${routerName}/monitoring/worker/resume`, null, requestConfig),

  /** 全线停机诊断：并行排查所有工位，LLM 跨工位根因分析 */
  diagnoseLineStoppage: (data: any) =>
    api.post(`${routerName}/diagnose-line-stoppage`, data, requestConfig),

  /** 查询未分配工位的块（覆盖度诊断） */
  getUnassignedBlocks: (params: any) =>
    api.get(`${routerName}/unassigned-blocks`, { params, ...requestConfig }),
}
