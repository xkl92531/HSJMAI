# Workflow任务编排系统 - 使用指南

## 📋 概述

基于Microsoft Agent Framework官方最佳实践构建的任务编排系统，提供5种编排模式，支持数据库持久化、Checkpoint恢复。

### 核心特性

✅ **5种官方编排模式**
- 条件路由（Conditional Routing）
- FanOut/FanIn并行
- 顺序链（Sequential Chain）
- 混合编排（Hybrid）
- 流式输出（Streaming）

✅ **生产级功能**
- Workflow自动编排（替代手动Task.WhenAll）
- 数据库持久化（集成AI_Agent_Workflow表）
- Checkpoint支持（暂停/恢复）
- 实时流式输出（SSE）

✅ **与旧实现对比**
- 代码量减少 **60%**
- 可维护性显著提升
- 内置错误处理和可观测性

---

## 🚀 快速开始

### 1. 配置依赖注入

在 `Program.cs` 或 `RegisterService.cs` 中注册服务：

```csharp
// 1. 注册IChatClient（已有）
services.AddSingleton<IChatClient>(sp =>
{
    var apiKey = configuration["OpenAI:ApiKey"];
    var endpoint = configuration["OpenAI:Endpoint"];
    
    return new OpenAIClient(apiKey)
        .GetOpenAIResponseClient("gpt-4o-mini");
});

// 2. 注册Checkpoint Store（可选：数据库版或内存版）
// 开发测试用内存版
services.AddSingleton<IWorkflowCheckpointStore, InMemoryCheckpointStore>();

// 生产环境用数据库版（需实现数据访问逻辑）
// services.AddSingleton<IWorkflowCheckpointStore, SqlWorkflowCheckpointStore>();

// 3. 注册WorkflowOrchestrationApp
services.AddScoped<WorkflowOrchestrationApp>();
```

### 2. API端点

所有端点已在 `WorkflowController` 中定义：

| 端点 | 方法 | 说明 |
|------|------|------|
| `/api/workflow/conditional-routing` | POST | 条件路由（推荐） |
| `/api/workflow/fanout-fanin` | POST | 全专家并行 |
| `/api/workflow/sequential-chain` | POST | 顺序链（Plan-Execute-Review） |
| `/api/workflow/hybrid-orchestration` | POST | 混合编排（最强大） |
| `/api/workflow/conditional-routing-streaming` | POST | 流式输出 |
| `/api/workflow/auto` | POST | 自动选择模式 |
| `/api/workflow/status/{workflowId}` | GET | 查询状态 |
| `/api/workflow/resume/{workflowId}` | POST | 恢复执行 |

---

## 📖 使用示例

### 示例1: 条件路由（最常用）

**场景**: 智能判断需要哪些专家，自动并行执行

```bash
curl -X POST http://localhost:5000/api/workflow/conditional-routing \
  -H "Content-Type: application/json" \
  -d '{
    "input": "帮我分析：明天北京的天气如何？适合出行吗？建议几点出发？",
    "enableCheckpoint": true
  }'
```

**执行流程**:
```
用户输入
  ↓
Router分析 → 决策: needsWeather=true, needsTimeInfo=true
  ↓
WeatherAdvisor (并行)     TimeExpert (并行)
  ↓                          ↓
      Aggregator (自动汇总)
              ↓
          Coordinator
              ↓
         最终答案
```

**响应示例**:
```json
{
  "workflowId": "workflow-abc123",
  "workflowType": "ConditionalRouting",
  "status": "Completed",
  "finalOutput": "根据天气和时间分析，建议明天上午9点出发...",
  "executionTimeMs": 3500,
  "stepsExecuted": ["Router", "Experts", "Aggregator", "Coordinator"]
}
```

---

### 示例2: FanOut/FanIn（全面分析）

**场景**: 需要多个专家同时提供意见

```bash
curl -X POST http://localhost:5000/api/workflow/fanout-fanin \
  -H "Content-Type: application/json" \
  -d '{
    "input": "全面分析：当前时间？北京天气？上海天气？2+3*4的结果？"
  }'
```

**执行流程**:
```
用户输入
  ↓
4个专家并行执行（DataAnalyst, TimeExpert, WeatherAdvisor, Developer）
  ↓
Aggregator自动汇总
  ↓
Coordinator
  ↓
综合答案
```

---

### 示例3: 顺序链（规划-执行-审核）

**场景**: 需要逐步推进的任务

```bash
curl -X POST http://localhost:5000/api/workflow/sequential-chain \
  -H "Content-Type: application/json" \
  -d '{
    "input": "制定一个学习Python的计划，并评估这个计划的可行性"
  }'
```

**执行流程**:
```
用户输入
  ↓
Planner: 制定学习计划
  ↓ (自动传递消息)
Executor: 执行计划（可调用工具）
  ↓ (自动传递消息)
Reviewer: 审核并提出改进建议
  ↓
最终方案
```

**关键优势**:
- ✅ 消息自动传递（无需手动拼接上下文）
- ✅ 每个Agent可调用工具
- ✅ 支持流式输出

---

### 示例4: 混合编排（最强大）

**场景**: 复杂任务，需要智能分析 + 条件路由 + 并行处理

```bash
curl -X POST http://localhost:5000/api/workflow/hybrid-orchestration \
  -H "Content-Type: application/json" \
  -d '{
    "input": "我想开发一个天气预报小程序，需要什么技术栈？帮我规划开发流程并评估时间成本",
    "enableCheckpoint": true
  }'
```

**执行流程**:
```
用户输入
  ↓
Analyzer: 分析复杂度 → complexity=complex, needsPlanning=true
  ↓
Planner: 制定详细计划（条件触发）
  ↓
推荐专家并行执行（Developer + TimeExpert）
  ↓
Aggregator汇总
  ↓
Coordinator
  ↓
完整方案
```

---

### 示例5: 流式输出（实时反馈）

**场景**: 需要实时看到执行进度

```javascript
// 前端JavaScript代码
const eventSource = new EventSource('/api/workflow/conditional-routing-streaming', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({
    input: '分析当前时间、北京天气，并计算 123*456 的结果'
  })
});

eventSource.onmessage = (event) => {
  const chunk = JSON.parse(event.data);
  
  switch (chunk.type) {
    case 'status':
      console.log('[状态]', chunk.content);
      break;
    case 'step':
      console.log('[步骤]', chunk.content);
      break;
    case 'agent_output':
      console.log(`[${chunk.agentName}]`, chunk.content);
      break;
    case 'final':
      console.log('[最终答案]', chunk.content);
      eventSource.close();
      break;
  }
};
```

**输出示例**:
```
[状态] [工作流启动] ID: workflow-xyz
[步骤] [Step 1] Router分析中...
[Router决策] {"needsDataAnalysis": true, "needsTimeInfo": true}
[激活专家] DataAnalyst, TimeExpert
[DataAnalyst] 123*456 = 56088
[TimeExpert] 当前时间是 2026-01-14 15:30:00
[最终答案] 当前时间是下午3点30分，计算结果为56088...
```

---

## 🔧 数据库集成

### Checkpoint Store配置

#### 选项1: 内存版（开发测试）

```csharp
services.AddSingleton<IWorkflowCheckpointStore, InMemoryCheckpointStore>();
```

#### 选项2: SQL Server版（生产环境）

**步骤1**: 实现数据访问逻辑

在 `SqlWorkflowCheckpointStore.cs` 中替换TODO注释部分：

```csharp
public async Task SaveWorkflowStartAsync(string workflowId, string input, string workflowType)
{
    // 实际实现 - 插入到 AI_Agent_Workflow 表
    var workflow = new AI_Agent_Workflow
    {
        ID = long.Parse(workflowId),
        Name = $"{workflowType} - {DateTime.Now:yyyy-MM-dd HH:mm}",
        Description = input,
        Status = 1, // 执行中
        ThinkingMode = workflowType,
        TriggerType = "User",
        StartTime = DateTime.Now,
        CreateTime = DateTime.Now,
        CreaterID = GetCurrentUserId(),
        CreaterName = GetCurrentUserName()
    };
    
    await _workflowRepository.Insert(workflow);
}
```

**步骤2**: 注册服务

```csharp
services.AddSingleton<IWorkflowCheckpointStore>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<SqlWorkflowCheckpointStore>>();
    // 注入你的Repository或DbContext
    return new SqlWorkflowCheckpointStore(logger);
});
```

### 数据库字段映射

| Workflow字段 | 数据库表字段 | 说明 |
|-------------|-------------|------|
| WorkflowId | AI_Agent_Workflow.ID | 主键 |
| WorkflowType | ThinkingMode | ConditionalRouting/FanOutFanIn等 |
| Status | Status | 0-待规划, 1-规划中, 2-执行中, 3-已完成, 4-失败 |
| FinalOutput | FinalOutput | 最终结果 |
| ExecutionContext | ExecutionContext | Checkpoint数据（JSON） |
| ExecutionTimeMs | ActualDuration | 实际执行时长（秒） |

---

## 🆚 新旧实现对比

### 旧实现（示例7 - 手动编排）

```csharp
❌ 问题:
1. 手动解析JSON（正则表达式）
2. 手动判断并行/串行（if-else）
3. 手动Task.WhenAll
4. 手动拼接上下文
5. 代码量大、难维护

代码行数: ~200行
可维护性: 低
```

### 新实现（Workflow编排）

```csharp
✅ 优势:
1. WorkflowBuilder声明式定义
2. 条件边自动路由
3. Workflow自动并行
4. 消息自动传递
5. 内置错误处理

代码行数: ~80行
可维护性: 高
```

**对比表**:

| 维度 | 旧实现 | 新实现 |
|------|-------|-------|
| 任务规划 | LLM生成JSON → 正则解析 | Workflow条件边自动路由 |
| 并行执行 | 手动Task.WhenAll | Workflow自动识别 |
| A2A交互 | 字符串拼接上下文 | 消息自动传递 |
| 错误处理 | 手动try-catch | Workflow内置 |
| 可观测性 | 无 | 内置Telemetry |
| Checkpoint | 无 | 原生支持 |
| 代码量 | ~200行 | ~80行 |

---

## 🎯 最佳实践

### 1. 选择合适的模式

| 场景 | 推荐模式 |
|------|---------|
| 一般查询（不确定需要哪些专家） | ConditionalRouting |
| 需要多方面综合分析 | FanOutFanIn |
| 需要规划-执行-审核流程 | SequentialChain |
| 复杂任务（开发、架构设计） | HybridOrchestration |
| 需要实时反馈 | Streaming |
| 不知道用哪个 | Auto |

### 2. 启用Checkpoint的时机

```csharp
// 长时间运行的任务（>30秒）
enableCheckpoint: true

// 快速查询（<5秒）
enableCheckpoint: false
```

### 3. 错误处理

所有模式都返回统一的 `WorkflowResult`：

```csharp
if (result.Status == "Failed")
{
    Console.WriteLine($"执行失败: {result.ErrorMessage}");
}
```

### 4. 性能优化

- **条件路由**: 只激活需要的专家（最高效）
- **FanOut/FanIn**: 全专家并行（吞吐量大，但成本高）
- **混合模式**: 智能平衡（推荐复杂场景）

---

## 🐛 故障排查

### 问题1: Workflow执行失败

**症状**: `Status = "Failed"`

**排查步骤**:
1. 检查 `ErrorMessage` 字段
2. 查看日志: `ILogger<WorkflowOrchestrationApp>`
3. 验证IChatClient配置是否正确

### 问题2: Router无法识别专家

**症状**: 所有专家都不执行

**解决方案**:
- Router的instructions需要明确输出JSON格式
- 检查 `ShouldRouteToExpert` 方法的JSON解析逻辑

### 问题3: Checkpoint无法保存

**症状**: 数据库中没有记录

**解决方案**:
1. 确认 `IWorkflowCheckpointStore` 已注册
2. 检查数据库连接
3. 实现 `SqlWorkflowCheckpointStore` 的TODO部分

---

## 📚 扩展开发

### 添加新的专家Agent

在 `CreateExpertAgents()` 中添加：

```csharp
["ImageAnalyst"] = new ChatClientAgent(
    _chatClient,
    instructions: "你是图像分析专家...",
    tools: [AIFunctionFactory.Create(AnalyzeImage)],
    name: "ImageAnalyst")
```

### 创建自定义Executor

参考 `SmartAggregationExecutor`：

```csharp
public sealed class CustomExecutor : 
    Executor<ChatMessage>("CustomExecutor"), 
    IResettableExecutor
{
    public override async ValueTask HandleAsync(
        ChatMessage message, 
        IWorkflowContext context, 
        CancellationToken cancellationToken = default)
    {
        // 自定义逻辑
        await context.YieldOutputAsync(processedMessage, cancellationToken);
    }
}
```

### 集成更多工具

```csharp
tools: [
    AIFunctionFactory.Create(GetCurrentTime),
    AIFunctionFactory.Create(Calculate),
    AIFunctionFactory.Create(GetWeather),
    AIFunctionFactory.Create(QueryDatabase), // 新工具
    AIFunctionFactory.Create(CallExternalAPI)  // 新工具
]
```

---

## 🔗 参考资源

### 官方文档
- [Workflows Overview](https://learn.microsoft.com/agent-framework/user-guide/workflows/overview)
- [Conditional Routing示例](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/GettingStarted/Workflows/WorkflowsWithConditionalRouting)
- [Checkpointing指南](https://learn.microsoft.com/agent-framework/user-guide/workflows/checkpointing)

### 相关文件
- 核心实现: [WorkflowOrchestrationApp.cs](./MAF.DEMO.AppLayer/AI/WorkflowOrchestrationApp.cs)
- API控制器: [WorkflowController.cs](./MAF.DEMO/Controllers/AI/WorkflowController.cs)
- Checkpoint Store: [WorkflowCheckpointStore.cs](./MAF.DEMO.AppLayer/AI/Services/WorkflowCheckpointStore.cs)
- 模型定义: [WorkflowModels.cs](./MAF.DEMO.AppLayer/AI/Models/WorkflowModels.cs)

---

## 📞 支持

遇到问题？
1. 查看 [MAF分析报告-与官方对比.md](./MAF分析报告-与官方对比.md)
2. 参考官方示例: https://github.com/microsoft/agent-framework
3. 加入官方Discord: https://discord.gg/b5zjErwbQM

---

**最后更新**: 2026年1月14日  
**版本**: v1.0.0  
**基于**: Microsoft Agent Framework official patterns
