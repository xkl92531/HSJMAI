/** 刷新自定义列 */
function refreshCustomColumns(oldColumns, customConfig, $grid) {
  let isReload = false
  let customColumns = []
  // 根据保存好的配置进行重置列顺序
  customConfig.forEach((col) => {
    const colConfig = oldColumns.find(x => x.field == col.field)
    if (colConfig) {
      isReload = true
      colConfig.width = col.width
      colConfig.fixed = col.fixed
      colConfig.visible = col.visible
      customColumns.push(colConfig)
    }
  })
  const otherColumns = oldColumns.filter(x => !customConfig.find(y => y.field == x.field))
  if (otherColumns.length > 0) {
    customColumns = customColumns.concat(otherColumns)
  }
  if (isReload) { $grid.loadColumn(customColumns) }
}

export default {
  refreshCustomColumns,
}
