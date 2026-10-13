import type { McpDiagnosisToolName } from '../../utils/mcp/mcpDiagnosisClient'

export interface McpDiagnosisPreset {
  toolName: McpDiagnosisToolName
  arguments: Record<string, unknown>
}

export const DEFAULT_MCP_DIAGNOSIS_PRESET = 'lineWithTraditionalModel' as const

export const MCP_DIAGNOSIS_PRESETS = {
  energyFlow: {
    toolName: 'diagnose_energy_flow',
    arguments: {
      productionLineId: 1,
      userDescription: '',
      forceDeepAnalysis: false,
      diagnosticId: 0,
    },
  },
  lineWithConstraintNoModel: {
    toolName: 'diagnose_line_stoppage',
    arguments: {
      projectIds: [],
      stationCodes: [],
      useLlmAnalysis: false,
      forceLlmSummaryWhenNoAbnormal: false,
      useAgentDiagnosis: false,
      includeConstraintAnalysis: true,
    },
  },
  lineWithTraditionalModel: {
    toolName: 'diagnose_line_stoppage',
    arguments: {
      projectIds: [],
      stationCodes: [],
      useLlmAnalysis: true,
      forceLlmSummaryWhenNoAbnormal: true,
      useAgentDiagnosis: false,
      includeConstraintAnalysis: true,
    },
  },
  lineWithAgent: {
    toolName: 'diagnose_line_stoppage',
    arguments: {
      projectIds: [],
      stationCodes: [],
      useLlmAnalysis: true,
      forceLlmSummaryWhenNoAbnormal: true,
      useAgentDiagnosis: true,
      agentMaxIterations: 12,
      includeConstraintAnalysis: true,
    },
  },
} as const satisfies Record<string, McpDiagnosisPreset>
