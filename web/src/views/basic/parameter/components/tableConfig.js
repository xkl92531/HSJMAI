import XEUtils from 'xe-utils'
import { ElMessage } from 'element-plus'
import formConfigApi from '@/api/modules/Basic/CustomFormConfig'

// 是否保存到数据库
const isSaved2Db = true
let isCreate = true
let formConfigData = {}
const formCode = 'SystemParam'// 表单CODE
let _t
function getCustomConfig(tableOptions, $ref) {
  if (!isSaved2Db) { return }
  const $grid = $ref.value
  formConfigApi.getFirst({ formCode, attr1: localStorage.getItem('account') }).then((res) => {
    if (res) {
      formConfigData = res
      isCreate = false
      const formColumns = JSON.parse(res.formColumnJson)
      let isReload = false
      tableOptions.columns.forEach((col) => {
        const colConfig = formColumns.find(x => x.field == col.field)
        if (colConfig) {
          isReload = true
          col.width = colConfig.width
          col.fixed = colConfig.fixed
          col.visible = colConfig.visible
        }
      })
      if (isReload) { $grid.loadColumn(tableOptions.columns) }
    // $grid.refreshColumn()
    }
  })
}
// getCustomConfig();
function returnTableConfig(t) {
  _t = t
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
    height: 600,
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
    tooltipConfig: {
      enterable: true,
      showAll: true,
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
    toolbarConfig: {
      custom: true,
      refresh: false,
      slots: {
        buttons: 'toolbar_buttons',
      },
    },
    columns: [
      { type: 'seq', fixed: 'left', field: 'seq', width: 40 },
      { type: 'checkbox', field: 'checkbox', title: '', fixed: 'left', width: 50 },
      { field: 'paramCode', width: 120, title: t('systemParam_form.paramCode'), sortable: true, sortType: 'string', align: 'center', showOverflow: true, className: '',
      },
      { field: 'paramName', width: 120, title: t('systemParam_form.paramName'), sortable: true, sortType: 'string', align: 'center', showOverflow: true, className: '',
      },
      { field: 'paramValue', width: 120, title: t('systemParam_form.paramValue'), sortable: true, sortType: 'string', align: 'center', showOverflow: true, className: '',
      },
      { field: 'remarks', width: 120, title: t('systemParam_form.remarks'), sortable: true, sortType: 'string', align: 'center', showOverflow: true, className: '',
      },
      { field: 'status', width: 120, title: t('systemParam_form.status'), sortable: true, sortType: 'string', align: 'center', showOverflow: true, className: '',
      },
      // { field: 'name', width: 50, title: 'Name', sortable: true ,sortType:'string',align:'center',fixed:'left',showOverflow:true, className:''
      // , slots: {
      //  filter: 'name_filter'
      // }, filterMultiple: true,filters:[{ data:'' }],   filterMethod ({ option, cellValue }) {
      //  return cellValue.indexOf(option.data)>-1
      // }
      // },
      // {
      //   field: 'role',
      //   title: 'Role',
      //   sortable: true,
      //   titlePrefix: { useHTML: true, content: '点击链接：<a class="link" href="" target="_blank"></a>',message: '' },
      //   filters: [
      //     { label: '前端开发', value: '前端' },
      //     { label: '后端开发', value: '后端' },
      //     { label: '测试', value: '测试' },
      //     { label: '程序员鼓励师', value: '程序员鼓励师' }
      //   ],
      //   filterMultiple: false,
      //   editRender: { name: 'input', attrs: { placeholder: '请输入角色' } }
      // },
      // {
      //   field: 'amount',
      //   title: 'Amount',
      //   formatter ({ cellValue }) {
      //     return cellValue ? `￥${XEUtils.commafy(XEUtils.toNumber(cellValue), { digits: 2 })}` : ''
      //   }
      // },
      {
        field: 'createTime',
        title: 'Create Date',
        minwidth: 140,
        visible: true,
        sortable: true,
        formatter({ cellValue }) {
          return XEUtils.toDateString(cellValue, 'yyyy-MM-dd')
        },
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
      if (data.type == 'reset' || data.type == 'confirm') {
      // 获取列对应的条件 ，保存列方案
        const colData = data.$grid.getTableColumn()
        // {collectColumn, fullColumn, visibleColumn, tableColumn}
        const cols = colData.fullColumn
        const configSave = []
        // console.log('确认筛选工具栏',cols)
        cols.forEach((col) => {
          const saveCol = { field: col.field, width: col.width, visible: col.visible, fixed: col.fixed }
          if ((col.width == 0 || col.width) && col.field) {
            // 存在固定宽度的列可以指定宽度方案
            // console.log(col.field+'列宽',  data.$grid.getColumnWidth(col.field));
            saveCol.width = data.$grid.getColumnWidth(col.field)
          }
          configSave.push(saveCol)
        })

        if (isSaved2Db) {
          const data = { id: formConfigData.id, formCode, attr1: storage.local.get('account'),
            FormColumnJson: JSON.stringify(configSave) }
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
