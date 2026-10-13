# 标签属性分析进度条实现计划

## 目标
修改 `handleAnalyzeTagAttributes` 方法，参考 `ProjectUpload.vue` 的异步进度轮询方式，增加进度条显示功能。

## 当前状态分析

### 现有代码结构
1. **`index.vue`**: 主页面，包含 `handleAnalyzeTagAttributes` 方法，目前是同步调用
2. **`ProjectUpload.vue`**: 已实现的异步进度轮询组件，包含完整的进度显示 UI 和轮询逻辑
3. **`variableAccess.ts`**: API 模块，目前 `analyzeTagAttributes` 是同步调用

### ProjectUpload.vue 的关键实现模式
- 使用 `taskId` 跟踪任务
- 通过 `pollProgress` 方法轮询进度
- 进度状态包含：status、percentage、message、totalFiles、processedFiles 等
- 状态值：0=Pending, 1=Processing, 2=Saving, 3=Completed, 4=Failed

## 实现步骤

### 步骤 1: 扩展 API 模块
**文件**: `d:\项目\HSJM_AI_PLC\web\src\api\modules\AI_PLC\variableAccess.ts`

添加两个新方法：
```typescript
// 启动后台分析任务
startBackgroundAnalyze: (projectId: any) =>
  api.post(`${routerName}/StartBackgroundAnalyze/${projectId}`),

// 查询分析进度
getAnalyzeProgress: (taskId: string) =>
  api.get(`${routerName}/GetAnalyzeProgress`, {
    params: { taskId },
  }),
```

### 步骤 2: 创建分析进度对话框组件
**文件**: `d:\项目\HSJM_AI_PLC\web\src\views\PLC\components\AnalyzeProgressDialog.vue`

参考 `ProjectUpload.vue` 的结构创建新组件，包含：
- Props: `modelValue` (对话框显示状态), `projectId` (项目ID)
- Emits: `update:modelValue`, `success`
- 进度状态接口定义
- 轮询逻辑 (`pollProgress`, `startPolling`, `stopPolling`)
- 进度显示 UI（进度条、状态标签、详细信息）

### 步骤 3: 修改主页面
**文件**: `d:\项目\HSJM_AI_PLC\web\src\views\PLC\index.vue`

修改内容：
1. 导入新创建的 `AnalyzeProgressDialog` 组件
2. 添加控制变量：
   ```typescript
   const analyzeVisible = ref(false)
   const analyzeProjectId = ref<string | number>('')
   ```
3. 修改 `handleAnalyzeTagAttributes` 方法：
   ```typescript
   const handleAnalyzeTagAttributes = (row: any) => {
     analyzeProjectId.value = row.id
     analyzeVisible.value = true
   }
   ```
4. 在模板中添加组件：
   ```html
   <AnalyzeProgressDialog 
     v-model="analyzeVisible" 
     :project-id="analyzeProjectId" 
     @success="getList" 
   />
   ```

## 文件变更清单

| 文件路径 | 操作 |
|---------|------|
| `src/api/modules/AI_PLC/variableAccess.ts` | 修改 - 添加 2 个 API 方法 |
| `src/views/PLC/components/AnalyzeProgressDialog.vue` | 新建 - 分析进度对话框组件 |
| `src/views/PLC/index.vue` | 修改 - 集成进度对话框组件 |

## 注意事项
- 后端需要提供对应的 API 接口支持（`StartBackgroundAnalyze` 和 `GetAnalyzeProgress`）
- 进度状态结构需要与后端保持一致
- 组件卸载时需要清理轮询定时器
