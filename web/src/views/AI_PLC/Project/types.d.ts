export interface DetailFormProps {
    id?: number | string
    projectName?: string
    brand?: string
    model?: string
    version?: string
    sourcePath?: string
    sourceHash?: string
    status?: number
    templateVersion?: string
    fileCount?: number
    successCount?: number
    failCount?: number
    lastParseRunID?: number
    remark?: string
    factoryID?: number
    productionLineID?: number
    workcellID?: number
    productionLineCode?: string
}
