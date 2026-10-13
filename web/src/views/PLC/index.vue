<route lang="yaml">
meta:
  title: PLC 解析管理
  icon: ant-design:code-sandbox-outlined
</route>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import ProjectAPI from '@/api/modules/PLC/project'
import ProjectUpload from './components/ProjectUpload.vue'
import IOTagUpload from './components/IOTagUpload.vue'
import AnalyzeProgressDialog from './components/AnalyzeProgressDialog.vue'
import MonitoringConfigDialog from './components/MonitoringConfigDialog.vue'
import variableAccess from '@/api/modules/AI_PLC/variableAccess'
import UploadAPI from '@/api/modules/PLC/upload'
import { useI18n } from 'vue-i18n'
import messages from './index.lang'

const { t, mergeLocaleMessage } = useI18n()

// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const router = useRouter()
const loading = ref(false)
const tableData = ref([])
const pagination = ref({
  page: 1,
  limit: 10,
  total: 0,
})

// Search
const search = ref({
  projectName: '',
})

// Upload Dialog
const uploadVisible = ref(false)
const currentProjectId = ref<string | number>('')

// IO Tag Upload Dialog
const ioTagUploadVisible = ref(false)

// Analyze Progress Dialog
const analyzeVisible = ref(false)
const analyzeProjectId = ref<string | number>('')

const monitoringConfigVisible = ref(false)
const monitoringConfigProjectId = ref<string | number>('')

// Create/Edit Dialog
const dialogVisible = ref(false)
const formRef = ref()
const formType = ref<'create' | 'edit'>('create')
const formData = ref({
  id: '',
  projectName: '',
  brand: '',
  product: '',
  productVersion: '',
  stationId: '',
  iotUrl: '',
  plcIp: '',
  plcPort: null as number | null,
  productionLineId: null as number | string | null,
  plcDeviceId: null as number | string | null,
})

const rules = {
  projectName: [{ required: true, message: t('plc_project.projectNameRequired'), trigger: 'blur' }],
  // Add other rules as needed
}

// ========== 基础资料下拉选项（各自基础表） ==========
// 产线选项：Basic_ProductionLine；PLC 设备选项：Basic_Device
const productionLineOptions = ref<any[]>([])
const plcDeviceOptions = ref<any[]>([])

const loadBindingOptions = async () => {
  try {
    const [lines, devices] = await Promise.all([
      ProjectAPI.productionLines() as any,
      ProjectAPI.plcDevices() as any,
    ])
    productionLineOptions.value = lines || []
    plcDeviceOptions.value = devices || []
  } catch (error) {
    console.error('加载产线/PLC设备基础数据失败', error)
    productionLineOptions.value = []
    plcDeviceOptions.value = []
  }
}

// 按选中产线过滤设备（Basic_Device.lineId；未选产线显示全部）
const filteredDeviceOptions = computed(() => {
  if (!formData.value.productionLineId) return plcDeviceOptions.value
  return plcDeviceOptions.value.filter(
    (d: any) => d.lineId == null || d.lineId === formData.value.productionLineId
  )
})

// 选择 PLC 设备：IP 自动回填（PlcIp 以基础表为准），新建且未填设备名时带出 DeviceName
const handleDeviceChange = (deviceId: number | string | null) => {
  if (deviceId == null || deviceId === '') {
    return
  }
  const device = plcDeviceOptions.value.find((d: any) => d.id === deviceId)
  if (device) {
    if (device.ipAddress) {
      formData.value.plcIp = device.ipAddress
    }
    if (!formData.value.stationId && device.deviceName) {
      formData.value.stationId = device.deviceName
    }
  }
}

// 切换产线：已选设备不属于新产线时清空设备选择（保留已回填的 IP 供参考）
const handleLineChange = () => {
  const lineId = formData.value.productionLineId
  if (lineId && formData.value.plcDeviceId) {
    const dev = plcDeviceOptions.value.find((d: any) => d.id === formData.value.plcDeviceId)
    if (dev && dev.lineId != null && dev.lineId !== lineId) {
      formData.value.plcDeviceId = null
    }
  }
}

const getList = async () => {
  loading.value = true
  try {
    const res = await ProjectAPI.list({
      page: pagination.value.page,
      limit: pagination.value.limit,
      ProjectName: search.value.projectName
    })
    if (res) {
      tableData.value = res.data || []
      pagination.value.total = res.total || 0
    }
  } catch (error) {
    console.error(error)
  } finally {
    loading.value = false
  }
}

const handleCreate = () => {
  formType.value = 'create'
  formData.value = {
    id: '',
    projectName: '',
    brand: '',
    product: '',
    productVersion: '',
    stationId: '',
    iotUrl: '',
    plcIp: '',
    plcPort: null as number | null,
    productionLineId: null as number | string | null,
    plcDeviceId: null as number | string | null,
  }
  loadBindingOptions()
  dialogVisible.value = true
}

const handleEdit = (row: any) => {
  formType.value = 'edit'
  formData.value = { ...row }
  loadBindingOptions()
  dialogVisible.value = true
}

const handleSubmit = async () => {
  if (!formRef.value) return
  await formRef.value.validate(async (valid: boolean) => {
    if (valid) {
      try {
        if (formType.value === 'create') {
          await ProjectAPI.create(formData.value)
          ElMessage.success(t('common.createSuccess'))
        } else {
          await ProjectAPI.edit(formData.value)
          ElMessage.success(t('common.updateSuccess'))
        }
        dialogVisible.value = false
        getList()
      } catch (error) {
        console.error(error)
      }
    }
  })
}

const handleUpload = (row: any) => {
  currentProjectId.value = row.id
  uploadVisible.value = true
}

const handleUploadSuccess = () => {
  // uploadVisible.value = false // Handled by component emit
  getList()
}

const handleIOTagUpload = (row: any) => {
  currentProjectId.value = row.id
  ioTagUploadVisible.value = true
}

const handleDetail = (row: any) => {
  router.push({
    path: `/plc/detail`,
    query: { id: row.id }
  })
}

const handleMonitoringConfig = (row: any) => {
  monitoringConfigProjectId.value = row.id
  monitoringConfigVisible.value = true
}

const handleSignalHistory = (row: any) => {
  router.push({
    path: '/plc/signal-history',
    query: { projectId: row.id },
  })
}

const handleGraphStructure = (row: any) => {
  router.push({
    path: '/lineup/graphs',
    query: { plcProjectId: row.id },
  })
}

const handleMoreCommand = (command: string, row: any) => {
  const actions: Record<string, (value: any) => void> = {
    monitoringConfig: handleMonitoringConfig,
    history: handleSignalHistory,
    io: handleIOTagUpload,
    edit: handleEdit,
    analyze: handleAnalyzeTagAttributes,
    register: handleExecBatchRegistrationTag,
    alarmVariable: handleAlarmVariable,
    delete: handleDelete,
  }
  actions[command]?.(row)
}

const handleAlarmVariable = (row: any) => {
  router.push({
    path: '/plc/alarm-variable',
    query: { projectId: row.id },
  })
}

const handleCylinderSignalCollector = () => {
  router.push({
    path: '/plc/job-center',
  })
}

const handleConstraintConfig = () => {
  router.push({
    path: '/plc/constraint-config',
  })
}

const handleDelete = async (row: any) => {
  try {
    await ElMessageBox.confirm(t('plc_project.deleteConfirmMsg'), t('plc_project.warningTitle'), {
      confirmButtonText: t('plc_project.confirmOk'),
      cancelButtonText: t('common.cancel'),
      type: 'warning',
    })
    await ProjectAPI.delete(row.id)
    ElMessage.success(t('common.deleteSuccess'))
    getList()
  } catch (error) {
    // cancel
  }
}

const onPageChange = (val: number) => {
  pagination.value.page = val
  getList()
}

const onSizeChange = (val: number) => {
  pagination.value.limit = val
  getList()
}

onMounted(() => {
  getList()
})

const handleAnalyzeTagAttributes = (row: any) => {
  analyzeProjectId.value = row.id
  analyzeVisible.value = true
}
const loadingxx = ref(false)
const handleExecBatchRegistrationTag = (row: any) => {
  loadingxx.value = true
  // V2（2026-09-17 改后台执行）：IGW 项目后端秒回——backResult 为「已提交后台全量注册（N 个 DB 块）…」
  // 或「同项目已有注册任务在执行中」；失败（未配置网关/plcip 为空/老网关报错）由 axios 拦截器
  // 统一 toast 并 reject，这里只展示成功消息。老网关项目仍同步完成，消息兜底旧文案。
  UploadAPI.execBatchRegistrationTag(row.id).then((res: any) => {
    ElMessage.success(res?.backResult || res?.data || t('plc_project.batchRegisterSuccess'))
  }).catch(() => {
    // 业务失败已由拦截器提示，这里静默（仅终止链路，避免未处理拒绝）
  }).finally(() => {
    loadingxx.value = false
  })
}
</script>

<template>
  <div class="h-full">
    <FaPageMain class="h-full flex flex-col" v-loading="loadingxx">
      <FaSearchBar :show-toggle="false">
        <template #default>
          <el-form :inline="true" :model="search" class="w-full">
            <el-form-item :label="$t('plc_project.projectName')">
              <el-input v-model="search.projectName" :placeholder="$t('plc_project.projectNamePlaceholder')" clearable @keyup.enter="getList" />
            </el-form-item>
            <el-form-item>
              <el-button type="primary" @click="getList">
                <FaIcon name="i-ep:search" class="mr-1" />
                {{ $t('common.search') }}
              </el-button>
              <el-button type="primary" @click="handleCreate">
                <FaIcon name="i-ep:plus" class="mr-1" />
                {{ $t('plc_project.createProject') }}
              </el-button>
              <el-button type="warning" @click="handleConstraintConfig">
                <FaIcon name="i-ep:set-up" class="mr-1" />
                {{ $t('plc_project.constraintConfig') }}
              </el-button>
            </el-form-item>
          </el-form>
        </template>
      </FaSearchBar>

      <el-divider border-style="dashed" class="my-4" />

      <el-table v-loading="loading" :data="tableData" style="width: 100%; flex: 1;" stripe border>
        <el-table-column prop="projectName" :label="$t('plc_project.projectName')" min-width="150" />
        <el-table-column prop="brand" :label="$t('plc_project.brand')" width="120" />
        <el-table-column prop="product" :label="$t('plc_project.product')" width="120" />
        <el-table-column prop="productVersion" :label="$t('plc_project.version')" width="100" />
        <el-table-column prop="stationId" :label="$t('plc_project.stationId')" width="120" />
        <el-table-column prop="productionLineName" :label="$t('plc_project.productionLine')" width="130" />
        <el-table-column prop="plcDeviceName" :label="$t('plc_project.plcDevice')" width="110" />
        <el-table-column prop="createTime" :label="$t('common.createTime')" width="180" />
        <el-table-column :label="$t('common.operate')" width="300" fixed="right" align="center">
          <template #default="{ row }">
            <el-button type="primary" link @click="handleDetail(row)">
              {{ $t('common.detail') }}
            </el-button>
            <el-button type="success" link @click="handleUpload(row)">
              {{ $t('plc_project.uploadParse') }}
            </el-button>
            <el-button type="primary" link @click="handleGraphStructure(row)">
              {{ $t('plc_project.graphStructure') }}
            </el-button>
            <el-dropdown trigger="click" @command="handleMoreCommand($event, row)">
              <el-button link>
                {{ $t('plc_project.more') }} <FaIcon name="i-ep:arrow-down" class="ml-1" />
              </el-button>
              <template #dropdown>
                <el-dropdown-menu>
                  <el-dropdown-item command="monitoringConfig">{{ $t('plc_project.monitoringConfig') }}</el-dropdown-item>
                  <el-dropdown-item command="history">{{ $t('plc_project.signalHistory') }}</el-dropdown-item>
                  <el-dropdown-item command="io">{{ $t('plc_project.importIOTags') }}</el-dropdown-item>
                  <el-dropdown-item command="edit">{{ $t('plc_project.editProject') }}</el-dropdown-item>
                  <el-dropdown-item command="analyze">{{ $t('plc_project.analyzeTagAttrs') }}</el-dropdown-item>
                  <el-dropdown-item command="register">{{ $t('plc_project.registerPlcTags') }}</el-dropdown-item>
                  <el-dropdown-item command="alarmVariable">{{ $t('plc_project.alarmVariableMgmt') }}</el-dropdown-item>
                  <el-dropdown-item command="delete" divided>{{ $t('plc_project.deleteProject') }}</el-dropdown-item>
                </el-dropdown-menu>
              </template>
            </el-dropdown>
          </template>
        </el-table-column>
      </el-table>

      <div class="mt-4 flex justify-end">
        <el-pagination v-model:current-page="pagination.page" v-model:page-size="pagination.limit"
          :total="pagination.total" :page-sizes="[10, 20, 50, 100]" layout="total, sizes, prev, pager, next, jumper"
          @current-change="onPageChange" @size-change="onSizeChange" />
      </div>
    </FaPageMain>

    <!-- Upload Dialog -->
    <ProjectUpload v-model="uploadVisible" :project-id="currentProjectId" @success="handleUploadSuccess" />

    <!-- IO Tag Upload Dialog -->
    <IOTagUpload v-model:visible="ioTagUploadVisible" :project-id="currentProjectId" @success="getList" />

    <!-- Analyze Progress Dialog -->
    <AnalyzeProgressDialog v-model="analyzeVisible" :project-id="analyzeProjectId" @success="getList" />

    <MonitoringConfigDialog v-model="monitoringConfigVisible" :project-id="monitoringConfigProjectId" />

    <!-- Create/Edit Dialog -->
    <el-dialog v-model="dialogVisible" :title="formType === 'create' ? $t('plc_project.createProject') : $t('plc_project.editProject')" width="500px">
      <el-form ref="formRef" :model="formData" :rules="rules" label-width="100px">
        <el-form-item :label="$t('plc_project.projectName')" prop="projectName">
          <el-input v-model="formData.projectName" :placeholder="$t('plc_project.projectNamePlaceholder')" />
        </el-form-item>
        <el-form-item :label="$t('plc_project.brand')" prop="brand">
          <el-input v-model="formData.brand" :placeholder="$t('plc_project.brandPlaceholder')" />
        </el-form-item>
        <el-form-item :label="$t('plc_project.product')" prop="product">
          <el-input v-model="formData.product" :placeholder="$t('plc_project.productPlaceholder')" />
        </el-form-item>
        <el-form-item :label="$t('plc_project.version')" prop="productVersion">
          <el-input v-model="formData.productVersion" :placeholder="$t('plc_project.versionPlaceholder')" />
        </el-form-item>
        <el-form-item :label="$t('plc_project.productionLineField')" prop="productionLineId">
          <el-select v-model="formData.productionLineId" :placeholder="$t('plc_project.productionLinePlaceholder')" clearable filterable
            style="width: 100%" @change="handleLineChange">
            <el-option v-for="line in productionLineOptions" :key="line.id" :label="line.name || line.code"
              :value="line.id" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('plc_project.deviceNameField')" prop="stationId">
          <el-input v-model="formData.stationId" :placeholder="$t('plc_project.deviceNamePlaceholder')" />
        </el-form-item>
        <el-form-item :label="$t('plc_project.iotUrlField')" prop="iotUrl">
          <el-input v-model="formData.iotUrl" :placeholder="$t('plc_project.iotUrlPlaceholder')" />
        </el-form-item>
        <el-form-item :label="$t('plc_project.plcDevice')" prop="plcDeviceId">
          <el-select v-model="formData.plcDeviceId" :placeholder="$t('plc_project.plcDevicePlaceholder')" clearable filterable
            style="width: 100%" @change="handleDeviceChange">
            <el-option v-for="dev in filteredDeviceOptions" :key="dev.id"
              :label="dev.ipAddress ? $t('plc_project.deviceWithIp', { name: dev.deviceName, ip: dev.ipAddress }) : dev.deviceName" :value="dev.id" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('plc_project.plcIpField')" prop="plcIp">
          <el-input v-model="formData.plcIp"
            :placeholder="$t('plc_project.plcIpPlaceholder')" />
        </el-form-item>
        <el-form-item :label="$t('plc_project.plcPortField')" prop="plcPort">
          <el-input-number v-model="formData.plcPort" :min="0" :max="65535" step-strictly style="width: 100%" :placeholder="$t('plc_project.plcPortPlaceholder')" />
        </el-form-item>
      </el-form>
      <template #footer>
        <span class="dialog-footer">
          <el-button @click="dialogVisible = false">{{ $t('common.cancel') }}</el-button>
          <el-button type="primary" @click="handleSubmit">{{ $t('plc_project.confirmOk') }}</el-button>
        </span>
      </template>
    </el-dialog>
  </div>
</template>
