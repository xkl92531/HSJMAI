import api from '../../index'

export interface ModelPurposeBinding {
  purposeCode: string
  displayName: string
  modelId: string | null
  modelName: string | null
  modelCode: string | null
  modelType: string | null
  revision: number
  status: string
}

export interface ModelPurposeOption {
  id: string
  modelName: string
  modelCode: string
  modelType: string
  isEnabled: boolean
}

export interface ModelPurposeList {
  bindings: ModelPurposeBinding[]
  models: ModelPurposeOption[]
}

export interface SetModelPurposeBinding {
  purposeCode: string
  modelId: string
  expectedRevision: number
}

export default {
  list: () => api.get('AIModelPurposeBinding/List') as unknown as Promise<ModelPurposeList>,
  set: (data: SetModelPurposeBinding) =>
    api.put('AIModelPurposeBinding/Set', data) as unknown as Promise<ModelPurposeBinding>,
}
