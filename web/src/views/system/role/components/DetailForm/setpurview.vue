<script setup lang="ts">
import type { VxeTableInstance } from 'vxe-table'
import { ElMessage } from 'element-plus'
import { useI18n } from 'vue-i18n'
import api from '@/api/modules/system/menu'
import apiRole from '@/api/modules/system/role'
import system from '@/api/modules/system/system'
import messages from '../../lang'

const props = defineProps({
  id: {
    type: [Number, String],
    default: '',
  },
})
const emits = defineEmits<{
  openLoading: []
  closeLoading: []
}>()

const { t, mergeLocaleMessage } = useI18n()
// 遍历所有的语言和它们的消息
for (const locale in messages) {
  mergeLocaleMessage(locale, messages[locale])
}

const loadingtext = ref(t('role_form.loading'))
const multipleTableRef = ref<VxeTableInstance>()
const form = ref({
  roleId: props.id,
  title: '',
  systemId: '',
  parentid: '',
  buttonIdList: [],
  menuList: [],
})

const data = ref({
  loading: false,
  formMode: 'dialog',
  formModePurview: 'drawer',
  // 列表数据
  dataList: [],
  checkKeys: [],
})

// 菜单选择框

onMounted(() => {
  getSystemList()
})

function setChoose() {
  multipleTableRef.value.clearCheckboxReserve()
  multipleTableRef.value.clearSelected()
  multipleTableRef.value.clearCheckboxRow()
  form.value.buttonIdList = []
  if (data.value.dataList != null && data.value.dataList.length > 0) {
    const ls = data.value.dataList.filter(it => it.isChecked === true)
    if (ls.length > 0) {
      ls.forEach((row: any) => {
        multipleTableRef.value.setCheckboxRow(row, true)
      })
    }
  }
  data.value.loading = false
}

function getInfo() {
  form.value.buttonIdList = []
  multipleTableRef.value?.clearAll()
  api.getMenuListByRole(form.value).then((res: any) => {
    data.value.dataList = res.backResult.dataList
    setTimeout(() => {
      nextTick(() => {
        setChoose()
      })
    }, 300)
  })
}
const systemData = ref([])

function getSystemList() {
  system.details({ status: 1 }).then((res: any) => {
    systemData.value = res
  })
}

defineExpose({
  submit() {
    return new Promise<void>(() => {
      emits('openLoading')
      const selectRows = multipleTableRef.value.getCheckboxRecords()
      const ss = multipleTableRef.value.getCheckboxIndeterminateRecords()
      form.value.menuList = []
      form.value.buttonIdList = []
      const selectArray = selectRows.concat(ss)
      selectArray.forEach((element: any) => {
        form.value.menuList.push(element.id)
        if (element.buttonIdList) {
          for (let i = 0; i < element.buttonIdList.length; i++) {
            form.value.buttonIdList.push(element.buttonIdList[i])
          }
        }
      })
      apiRole.saveRole(form.value).then((res: any) => {
        if (res.isSuccess) {
          ElMessage.success({
            message: t('common.saveSuccess'),
            center: true,
          })
          getInfo()
        }
      }).finally(() => {
        emits('closeLoading')
      })
    })
  },
})

function swloading(flag) {
  if (flag == 1) {
    loadingtext.value = t('role_form.saving')
  }
  else if (flag == 2) {
    loadingtext.value = t('role_form.setting')
  }
  else {
    loadingtext.value = t('role_form.loading')
  }
}

function checkboxAll({ checked }) {
  data.value.loading = true
  swloading(2)
  data.value.dataList.forEach((item) => {
    item.isChecked = checked
    item.buttonIdList = []
    if (item.menuButtonList && checked) {
      for (let i = 0; i < item.menuButtonList.length; i++) {
        item.buttonIdList.push(item.menuButtonList[i].id)
      }
    }
  })
  data.value.loading = false
}

function checkboxChange(row) {
  swloading(2)
  data.value.loading = true
  // setCheckboxChildren(row.row, row.checked)
  if (row.row.menuButtonList && row.row.menuButtonList.length > 0) {
    row.row.menuButtonList.forEach((it) => {
      it.isChecked = row.checked
      if (row.checked) {
        if (!row.row.buttonIdList.includes(it.id)) {
          row.row.buttonIdList.push(it.id)
        }
      }
      else {
        row.row.buttonIdList = row.row.buttonIdList.filter(x => x !== it.id)
      }
    })
  }
  data.value.loading = false
}

// 递归设置选中
function setCheckboxChildren(row, checked) {
  if (row.children && row.children.length > 0) {
    row.children.forEach((it) => {
      it.isChecked = checked
      it.buttonIdList = []
      if (it.menuButtonList && checked) {
        for (let i = 0; i < it.menuButtonList.length; i++) {
          it.buttonIdList.push(it.menuButtonList[i].id)
        }
      }
      setCheckboxChildren(it, checked)
    })
  }
}

function buttonCheckChange(value, buttonId, row) {
  if (value) {
    multipleTableRef.value.setCheckboxRow(row, true)
  }
  else {
    // row.menuButtonList.filter(x => x.isChecked === true)
    // if (row.menuButtonList !== null && row.menuButtonList.length > 0) {
    //   // const buttonLs = row.menuButtonList.filter(xx => form.value.buttonIdList.includes(xx.id))
    //   // if (buttonLs === undefined || buttonLs === null || buttonLs.length <= 0) {
    //   //   multipleTableRef.value.setCheckboxRow(row, false)
    //   // }
    // }
  }
}
</script>

<template>
  <el-form v-loading="data.loading" label-suffix="：" :element-loading-text="loadingtext">
    <el-row>
      <el-col :span="8">
        <el-form-item :label="t('role_form.selectMenu')" prop="id">
          <ElSelect v-model="form.systemId" :placeholder="t('role_form.pleaseSelectSystem')" @change="getInfo">
            <ElOption
              v-for="item in systemData" :key="item.id" :label="item.name"
              :value="item.id"
            />
          </ElSelect>
        </el-form-item>
      </el-col>
      <el-col :span="24">
        <vxe-table
          ref="multipleTableRef"
          border
          :row-config="{ keyField: 'id' }"
          :column-config="{ resizable: true }"
          :data="data.dataList"
          :tree-config="{ transform: true, expandAll: true, rowField: 'id', parentField: 'parentID' }"
          :checkbox-config="{ labelField: 'menuName', checkStrictly: true, showHeader: true }"
          :max-height="700"
          @checkbox-change="checkboxChange"
          @checkbox-all="checkboxAll"
        >
          <vxe-column type="checkbox" :title="t('role_form.menuName')" width="360" tree-node>
            <template #default="{ row }">
              <FaIcon v-if="row.menuIcon !== null && row.menuIcon !== '' && row.menuIcon !== undefined" :name="row.menuIcon" :size="16" />
              {{ row.menuName }}
            </template>
          </vxe-column>
          <vxe-column field="button" :title="t('role_form.purview')">
            <template #default="{ row }">
              <el-checkbox-group
                v-model="row.buttonIdList"
              >
                <el-checkbox
                  v-for="auth in row.menuButtonList" :key="`${auth.id}`"
                  :label="`${auth.id}`"
                  @change="e => buttonCheckChange(e, auth.id, row)"
                >
                  {{ auth.name }}
                </el-checkbox>
              </el-checkbox-group>
            </template>
          </vxe-column>
        </vxe-table>
      </el-col>
    </el-row>
  </el-form>
</template>
