import { ElMessage } from 'element-plus'
import formConfigApi from '@/api/modules/Basic/CustomFormConfig'
import common from '@/utils/common'

// 是否保存到数据库
const isSaved2Db = true
let isCreate = true
let formConfigData = {}
const formCode = 'LoginLog'// 表单CODE
function getCustomConfig(tableOptions, $ref) {
  if (!isSaved2Db) { return }
  const $grid = $ref.value
  formConfigApi.getFirst({ formCode, attr1: localStorage.getItem('account') }).then((res) => {
    if (res.data) {
      formConfigData = res.data
      isCreate = false
      const formColumns = JSON.parse(res.data.formColumnJson)
      console.log($grid)
      common.refreshCustomColumns(tableOptions.columns, formColumns, $grid)
    }
  })
}
// getCustomConfig();
function returnTableConfig(t) {
  const config = {
    border: true,
    size: 'small',
    showHeaderOverflow: true,
    showOverflow: true,
    rowClassName: ({ row, rowIndex, $rowIndex }) => {
      // 特殊行指定样式
      if (row.name == '8') {
        return 'green-cell'
      }
    },
    cellClassName: ({ row, rowIndex, $rowIndex, column, columnIndex, $columnIndex }) => {
      // 特殊单元格指定样式
    },
    keepSource: false,
    id: `full_edit_${new Date().getTime()}`,
    maxHeight: '100%',
    loading: false,
    rowConfig: {
      keyField: 'id',
      isHover: true,
    },
    columnConfig: {
      resizable: true,
    },
    resizableConfig: {
      minWidth: 50,
    },
    loadingConfig: {
      show: true,
    },
    customConfig: {
      storage: true,
      checkMethod({ column }) {
        if (column.type == 'checkbox') { return false }
        // 不能隐藏的列
        if (['filedname'].includes(column.field)) {
          return false
        }
        return true
      },
    },
    sortConfig: {
      trigger: 'cell',
      // sortMethod: ({ data, column, property, order }) => {
      // },
      remote: false,
    },
    filterConfig: {
      remote: false,
      filterMethod: ({ options, values, cellValue, row, column }) => {
      },
    },
    seqConfig: {
      seqMethod({ rowIndex }) {
        return `${rowIndex + 1}`
      },
    },
    virtualXConfig: {
      gt: 0,
      enabled: true,
    },
    virtualYConfig: {
      gt: 0,
      enabled: true,
    },
    exportConfig: {
      remote: false,
      types: ['csv'],
      modes: ['current', 'selected'], // , 'all'

    },
    toolbarConfig: {
      export: true,
      custom: true,
      refresh: false,
      slots: {
        buttons: 'toolbar_buttons',
      },
    },
    columns: [
      { field: 'modifierName', width: 120, title: t('login_form.empName'), sortable: false, sortType: 'string', align: 'left', showOverflow: true, className: '',
      },
      { field: 'account', width: 120, title: t('login_form.account'), sortable: false, sortType: 'string', align: 'left', showOverflow: true, className: '',
      },
      { field: 'loginDate', width: 140, title: t('login_form.loginDate'), sortable: true, sortType: 'string', align: 'left', showOverflow: true, className: '',
      },
      { field: 'loginIP', width: 120, title: t('login_form.loginIP'), sortable: false, sortType: 'string', align: 'left', showOverflow: true, className: '',
      },
      { field: 'loginType', width: 120, title: t('login_form.loginType'), sortable: false, sortType: 'string', align: 'center', showOverflow: true, className: '',
      },
      { field: 'osVersion', width: 120, title: t('login_form.oSVersion'), sortable: false, sortType: 'string', align: 'left', showOverflow: true, className: '',
      },

      { field: 'location', width: 150, title: t('login_form.location'), sortable: false, sortType: 'string', align: 'left', showOverflow: true, className: '',
      },
      { field: 'address', minWidth: 120, title: t('login_form.address'), sortable: false, sortType: 'string', align: 'left', showOverflow: true, className: '',
      },
    ],
    checkboxConfig: {
      // labelField: 'id',
      reserve: true,
      highlight: true,
      range: true,
    },
    toolbarToolClick: () => {

    },

  }

  return config
}

function returnGridEvents(t, listData) {
  return {
    toolbarToolClick(code, tool, $event) {

    },
    custom(data) {
    // console.log('筛选工具栏',data)
      if (data.type == 'reset') {
        if (formConfigData.id) {
          formConfigApi.delete(formConfigData.id).then((res) => {
            if (res.isSuccess) {
              isCreate = true
              ElMessage({
                message: t('common.formConfigSaveSuccess'),
                type: 'success',
              })
              location.reload()
            }
          })
        }
      }
      else if (data.type == 'confirm') {
      // 获取列对应的条件 ，保存列方案
        const colData = data.$grid.getTableColumn()
        // {collectColumn, fullColumn, visibleColumn, tableColumn}
        const cols = colData.fullColumn
        const configSave = []
        // console.log('确认筛选工具栏',cols)
        cols.forEach((col) => {
          const saveCol = { field: col.field, width: col.width, visible: col.visible, fixed: col.fixed }
          if ((col.width == 0 || col.width) && col.field && col.visible) {
            // 存在固定宽度的列可以指定宽度方案
            // console.log(col.field+'列宽',  data.$grid.getColumnWidth(col.field));
            saveCol.width = data.$grid.getColumnWidth(col.field)
          }
          configSave.push(saveCol)
        })

        if (isSaved2Db) {
          const data = { id: formConfigData.id, formCode, attr1: localStorage.getItem('account'), FormColumnJson: JSON.stringify(configSave) }
          if (isCreate) {
            formConfigApi.create(data).then((res) => {
              if (res.isSuccess) {
                formConfigData.id = res.backResult
                isCreate = false
                ElMessage({
                  message: t('common.formConfigSaveSuccess'),
                  type: 'success',
                })
              }
            })
          }
          else {
            formConfigApi.edit(data).then((res) => {
              if (res.isSuccess) {
                isCreate = false
                ElMessage({
                  message: t('common.formConfigSaveSuccess'),
                  type: 'success',
                })
              }
            })
          }
        }
      }
    },
    sortChange({ column, property, order, sortBy, sortList, $event }) {
    // console.log('排序',column, property, order, sortBy, sortList, $event)
    },
    clearSort(sortList, $event) {

    },
    proxyQuery(status) {
      console.log('数据代理查询事件', status)
    },
    proxyDelete() {
      console.log('数据代理删除事件')
    },
    proxySave() {
      console.log('数据代理保存事件')
    },
    checkboxAll(data) {
    // console.log('选中所有',data)
      listData.value.batch.selectionDataList = data.records
    },
    checkboxChange(data) {
    // console.log('选中改变',data)
      listData.value.batch.selectionDataList = data.records
    },
    checkboxRangeEnd(data) {
    // console.log('选中范围结束',data)
      listData.value.batch.selectionDataList = data.records
    },
    currentChange(data) {
    // console.log('选中行改变',data)
    },
  }
}

export default {
  config: returnTableConfig,
  gridEvents: returnGridEvents,
  initCustom: getCustomConfig,
}
