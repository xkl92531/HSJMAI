using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using HSJM.AI.Core;
using HSJM.AI.IDbService;
using HSJM.AI.Utility;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace HSJM.AI.AppLayer.Agent.Services
{
    /// <summary>
    /// LLM 模型管理服务
    /// 职责：模型推荐、健康检查、成本统计、降级策略
    /// 参考：阶段1-智能体核心框架实现指南.md - 第5章 模型调度与监控
    /// </summary>
    public class LLMModelService : ISelfScopedAutoInject
    {
        private readonly ILLM_ModelService _modelService;
        private readonly ILogger<LLMModelService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IAiModelPurposeResolver _purposeResolver;

        // 缓存 ChatClient 对应的自定义请求参数（由 CreateChatClientByModelIdAsync 自动填充）
        private readonly ConcurrentDictionary<ChatClient, ChatCompletionOptions> _clientOptionsCache = new();

        public LLMModelService(
            ILLM_ModelService modelService,
            ILogger<LLMModelService> logger,
            IHttpClientFactory httpClientFactory,
            IAiModelPurposeResolver purposeResolver)
        {
            _modelService = modelService;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _purposeResolver = purposeResolver;
        }

        #region 模型推荐

        /// <summary>
        /// 兼容旧调用入口；实际模型由网站的 default_chat 用途绑定确定。
        /// 不按价格、创建时间或启用状态自动改选其他模型。
        /// </summary>
        public async Task<LLM_ModelEntity?> RecommendModelAsync(
            string modelType = "Chat",
            int requiredContextSize = 4096,
            bool prioritizeCost = true)
        {
            _logger.LogInformation("[模型推荐] 开始推荐: Type={Type}, ContextSize={Size}, CostPriority={Priority}", 
                modelType, requiredContextSize, prioritizeCost);

            try
            {
                if (!string.Equals(modelType, "Chat", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"平台默认用途不支持模型类型: {modelType}");

                var binding = await _purposeResolver.ResolveAsync(AiModelPurposes.DefaultChat);
                var model = binding.Model;
                if (model.ContextWindow is not int contextWindow || contextWindow < requiredContextSize)
                    throw new InvalidOperationException(
                        $"平台默认聊天模型 {model.Id} 的上下文窗口不足 {requiredContextSize}");

                _logger.LogInformation("[模型选择] 使用网站默认聊天绑定: ModelId={ModelId}, Revision={Revision}",
                    model.Id, binding.Revision);
                return model;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[模型选择] 网站默认聊天绑定无效");
                throw;
            }
        }

        #endregion

        #region 健康检查

        /// <summary>
        /// 检查模型健康状态
        /// 
        /// 实现思路：
        /// 1. 如果 HealthCheckUrl 不为空，发送 HTTP GET 请求
        /// 2. 根据响应更新状态：
        ///    - 200 OK → "Healthy"
        ///    - 超时/错误 → "Unhealthy"
        ///    - 响应时间 > 2000ms → "Slow"
        /// 3. 更新 LastHealthCheckTime
        /// 4. 记录连续失败次数
        /// 
        /// 对应表：AI_LLM_Model（LastHealthCheckTime 字段）
        /// 参考：指南 5.1 - 健康检查
        /// </summary>
        public async Task<string> CheckModelHealthAsync(long modelId)
        {
            _logger.LogInformation("[健康检查] 开始检查模型: ModelId={ModelId}", modelId);

            try
            {
                // Step 1: 加载模型配置
                var model = await _modelService.GetByIdAsync(modelId);
                if (model == null)
                {
                    _logger.LogWarning("[健康检查] 模型不存在: ModelId={ModelId}", modelId);
                    return "NotFound";
                }

                string healthStatus;
                var startTime = DateTime.UtcNow;

                // Step 2: 执行健康检查
                if (string.IsNullOrWhiteSpace(model.HealthCheckUrl))
                {
                    // 无健康检查端点，根据模型类型判断
                    if (model.IsLocal == true)
                    {
                        healthStatus = "Healthy"; // 本地模型默认健康
                        _logger.LogInformation("[健康检查] 本地模型，默认健康: {ModelName}", model.ModelName);
                    }
                    else
                    {
                        healthStatus = "Unknown"; // 远程模型无端点，状态未知
                        _logger.LogWarning("[健康检查] 无健康检查端点: {ModelName}", model.ModelName);
                    }
                }
                else
                {
                    // 发送 HTTP 健康检查请求
                    var httpClient = _httpClientFactory.CreateClient("LLMHealthCheck");
                    
                    try
                    {
                        var response = await httpClient.GetAsync(model.HealthCheckUrl);
                        var elapsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds;

                        if (response.IsSuccessStatusCode)
                        {
                            healthStatus = elapsedMs > 2000 ? "Slow" : "Healthy";
                            _logger.LogInformation("[健康检查] 检查成功: {ModelName}, Status={Status}, Time={Time}ms", 
                                model.ModelName, healthStatus, elapsedMs);
                        }
                        else
                        {
                            healthStatus = "Unhealthy";
                            _logger.LogWarning("[健康检查] HTTP错误: {ModelName}, StatusCode={StatusCode}", 
                                model.ModelName, response.StatusCode);
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        healthStatus = "Timeout";
                        _logger.LogWarning("[健康检查] 超时: {ModelName}, Url={Url}", 
                            model.ModelName, model.HealthCheckUrl);
                    }
                    catch (Exception ex)
                    {
                        healthStatus = "Unhealthy";
                        _logger.LogError(ex, "[健康检查] 请求异常: {ModelName}, Url={Url}", 
                            model.ModelName, model.HealthCheckUrl);
                    }
                }

                // Step 3: 更新最后检查时间
                model.LastHealthCheckTime = DateTime.UtcNow;
                await _modelService.UpdateAsync(model);

                return healthStatus;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[健康检查] 检查失败: ModelId={ModelId}", modelId);
                return "Error";
            }
        }

        /// <summary>
        /// 批量健康检查
        /// 
        /// 用途：定时任务调用，检查所有启用的模型
        /// </summary>
        public async Task<Dictionary<long, string>> CheckAllModelsHealthAsync()
        {
            // TODO: 批量健康检查
            // 1. var models = await _modelService.GetListAsync(m => m.IsEnabled)
            // 2. var tasks = models.Select(m => CheckModelHealthAsync(m.ID))
            // 3. var results = await Task.WhenAll(tasks)
            // 4. return models.Zip(results).ToDictionary(x => x.First.ID, x => x.Second)
            
            _logger.LogInformation("批量检查模型健康 (占位实现)");
            
            throw new NotImplementedException("批量健康检查逻辑待实现");
        }

        #endregion

        #region 成本统计

        /// <summary>
        /// 记录模型使用统计
        /// 
        /// 实现思路：
        /// 1. 加载模型配置
        /// 2. 计算成本：
        ///    - InputCost = inputTokens * CostPerInputToken
        ///    - OutputCost = outputTokens * CostPerOutputToken
        ///    - TotalCost = InputCost + OutputCost
        /// 3. 更新统计字段（需扩展表或单独统计表）：
        ///    - TotalCalls++
        ///    - TotalInputTokens += inputTokens
        ///    - TotalOutputTokens += outputTokens
        ///    - TotalCost += cost
        /// 
        /// 注意：当前表结构未包含统计字段，建议：
        /// - 方案1：扩展 AI_LLM_Model 表添加统计字段
        /// - 方案2：创建 AI_LLM_Usage_Stats 统计表
        /// 
        /// 对应表：AI_LLM_Model（或新建统计表）
        /// 参考：指南 5.1 - 统计
        /// </summary>
        public async Task<decimal> RecordUsageAsync(
            long modelId,
            int inputTokens,
            int outputTokens,
            TimeSpan duration)
        {
            _logger.LogInformation("[使用统计] 记录调用: ModelId={ModelId}, Input={Input}, Output={Output}, Duration={Duration}ms", 
                modelId, inputTokens, outputTokens, duration.TotalMilliseconds);

            try
            {
                // Step 1: 加载模型配置
                var model = await _modelService.GetByIdAsync(modelId);
                if (model == null)
                {
                    _logger.LogWarning("[使用统计] 模型不存在: ModelId={ModelId}", modelId);
                    return 0m;
                }

                // Step 2: 计算成本
                var inputCost = inputTokens * (model.CostPerInputToken ?? 0m);
                var outputCost = outputTokens * (model.CostPerOutputToken ?? 0m);
                var totalCost = inputCost + outputCost;

                _logger.LogInformation("[使用统计] 成本计算: {ModelName}, InputCost={InputCost:F6}, OutputCost={OutputCost:F6}, Total={Total:F6}", 
                    model.ModelName, inputCost, outputCost, totalCost);

                // Step 3: 记录统计（暂时仅日志，未来可扩展为统计表）
                // TODO: 将来创建 AI_LLM_Usage_Stats 表后，写入统计数据
                // await _usageStatsService.RecordAsync(new UsageStats {
                //     ModelID = modelId,
                //     InputTokens = inputTokens,
                //     OutputTokens = outputTokens,
                //     Cost = totalCost,
                //     Duration = duration,
                //     RecordTime = DateTime.UtcNow
                // });

                return totalCost;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[使用统计] 记录失败: ModelId={ModelId}", modelId);
                return 0m;
            }
        }

        /// <summary>
        /// 获取模型成本报表
        /// 
        /// 实现思路：
        /// 1. 查询统计数据（按时间范围）
        /// 2. 返回 JSON 格式报表：
        ///    {
        ///      "TotalCalls": 1500,
        ///      "TotalTokens": 1000000,
        ///      "TotalCost": 15.50,
        ///      "AvgCostPerCall": 0.0103,
        ///      "TopModels": [...]
        ///    }
        /// 
        /// 对应表：统计表
        /// 用途：成本分析、预算控制
        /// </summary>
        public async Task<Dictionary<string, object>> GetCostReportAsync(
            DateTime? startTime = null,
            DateTime? endTime = null)
        {
            // TODO: 成本报表生成
            
            _logger.LogInformation("获取成本报表: StartTime={Start}, EndTime={End} (占位实现)", 
                startTime, endTime);
            
            throw new NotImplementedException("成本报表逻辑待实现");
        }

        #endregion

        #region 降级策略

        /// <summary>
        /// 获取降级模型
        /// 
        /// 实现思路：
        /// 1. 加载主模型配置
        /// 2. 检查 FallbackModelID 字段
        /// 3. 递归查找可用的备用模型（避免循环引用）
        /// 4. 返回第一个 IsEnabled = 1 的备用模型
        /// 
        /// 对应表：AI_LLM_Model（FallbackModelID 字段）
        /// 参考：指南 5.2 - 模型降级策略
        /// </summary>
        public async Task<LLM_ModelEntity?> GetFallbackModelAsync(long primaryModelId)
        {
            return await GetFallbackModelInternalAsync(primaryModelId, new HashSet<long>());
        }

        /// <summary>
        /// 内部递归方法，带循环检测
        /// </summary>
        private async Task<LLM_ModelEntity?> GetFallbackModelInternalAsync(
            long modelId, 
            HashSet<long> visitedModels)
        {
            _logger.LogInformation("[降级策略] 查找备用模型: ModelId={ModelId}", modelId);

            try
            {
                // 防止循环引用
                if (visitedModels.Contains(modelId))
                {
                    _logger.LogWarning("[降级策略] 检测到循环引用: ModelId={ModelId}", modelId);
                    return null;
                }
                visitedModels.Add(modelId);

                // Step 1: 加载主模型配置
                var model = await _modelService.GetByIdAsync(modelId);
                if (model == null)
                {
                    _logger.LogWarning("[降级策略] 模型不存在: ModelId={ModelId}", modelId);
                    return null;
                }

                // Step 2: 检查是否配置了备用模型
                if (model.FallbackModelID == null || model.FallbackModelID <= 0)
                {
                    _logger.LogInformation("[降级策略] 未配置备用模型: {ModelName}", model.ModelName);
                    return null;
                }

                // Step 3: 加载备用模型
                var fallbackModel = await _modelService.GetByIdAsync(model.FallbackModelID.Value);
                if (fallbackModel == null)
                {
                    _logger.LogWarning("[降级策略] 备用模型不存在: FallbackModelID={FallbackId}", model.FallbackModelID);
                    return null;
                }

                // Step 4: 检查备用模型是否启用
                if (fallbackModel.IsEnabled == true && !fallbackModel.IsDeleted)
                {
                    _logger.LogInformation("[降级策略] 找到可用备用模型: {FallbackName} (ID={FallbackId})", 
                        fallbackModel.ModelName, fallbackModel.Id);
                    return fallbackModel;
                }

                // Step 5: 递归查找下一级备用模型
                _logger.LogInformation("[降级策略] 备用模型不可用，继续递归: {FallbackName}", fallbackModel.ModelName);
                return await GetFallbackModelInternalAsync(fallbackModel.Id, visitedModels);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[降级策略] 查找失败: ModelId={ModelId}", modelId);
                return null;
            }
        }

        /// <summary>
        /// 标记模型为不可用
        /// 
        /// 用途：模型连续失败时自动禁用
        /// </summary>
        public async Task<bool> DisableModelAsync(long modelId, string reason)
        {
            // TODO: 禁用模型
            // 1. var model = await _modelService.GetByIdAsync(modelId)
            // 2. model.IsEnabled = false
            // 3. await _modelService.UpdateAsync(model)
            // 4. 写入审计日志
            
            _logger.LogWarning("禁用模型: ModelId={ModelId}, Reason={Reason} (占位实现)", 
                modelId, reason);
            
            throw new NotImplementedException("禁用模型逻辑待实现");
        }

        #endregion

        #region 模型配置管理

        /// <summary>
        /// 创建模型配置
        /// 
        /// 实现思路：
        /// 1. 校验必填字段（ModelName, ModelCode, Provider）
        /// 2. 检查 ModelCode 唯一性
        /// 3. 加密 ApiKey（使用 AES 或其他加密算法）
        /// 4. 写入 AI_LLM_Model
        /// 
        /// 对应表：AI_LLM_Model
        /// </summary>
        public async Task<long> CreateModelAsync(
            string modelName,
            string modelCode,
            string provider,
            string? baseUrl = null,
            string? apiKey = null)
        {
            // TODO: 创建模型配置
            // 1. 校验参数
            // 2. 检查 ModelCode 唯一性
            // 3. 加密 ApiKey
            // 4. 插入数据库
            
            _logger.LogInformation("创建模型: Name={Name}, Code={Code}, Provider={Provider} (占位实现)", 
                modelName, modelCode, provider);
            
            throw new NotImplementedException("创建模型逻辑待实现");
        }

        /// <summary>
        /// 更新模型参数
        /// 
        /// 支持热更新：Temperature, MaxTokens, CostPerToken 等
        /// </summary>
        public async Task<bool> UpdateModelParamsAsync(
            long modelId,
            Dictionary<string, object> parameters)
        {
            // TODO: 更新模型参数
            
            _logger.LogInformation("更新模型参数: ModelId={ModelId}, ParamsCount={Count} (占位实现)", 
                modelId, parameters.Count);
            
            throw new NotImplementedException("更新参数逻辑待实现");
        }

        #endregion

        #region 聊天功能

        /// <summary>
        /// 基础聊天（非流式）
        /// 直接使用 OpenAI Chat API（兼容 OpenAI/DeepSeek/vLLM/Ollama）
        /// </summary>
        /// <param name="chatClient">ChatClient 实例（从数据库配置创建）</param>
        /// <param name="userMessage">用户消息</param>
        /// <param name="systemPrompt">系统提示（可选）</param>
        /// <param name="options">聊天选项（从 BuildChatCompletionOptions 构建，可选）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns></returns>
        public async Task<ChatResponse> ChatAsync(
            ChatClient chatClient,
            string userMessage,
            string systemPrompt = "你是一个有帮助的AI助手。",
            ChatCompletionOptions options = null,
            CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;
            _logger.LogInformation("[聊天] 开始非流式聊天");

            try
            {
                // 构造消息
                var messages = new List<ChatMessage>
                {
                    new SystemChatMessage(systemPrompt),
                    new UserChatMessage(userMessage)
                };

                // 调用 OpenAI API（自动应用自定义参数）
                options ??= _clientOptionsCache.GetValueOrDefault(chatClient) ?? new ChatCompletionOptions();
                var completion = await chatClient.CompleteChatAsync(messages, options, cancellationToken);

                var duration = DateTime.UtcNow - startTime;
                _logger.LogInformation("[聊天] 非流式聊天完成: Duration={Duration}ms, Tokens={Tokens}",
                    duration.TotalMilliseconds, completion.Value.Usage?.TotalTokenCount ?? 0);

                return new ChatResponse
                {
                    Content = completion.Value.Content[0].Text ?? string.Empty,
                    FinishReason = completion.Value.FinishReason.ToString(),
                    InputTokens = completion.Value.Usage?.InputTokenCount ?? 0,
                    OutputTokens = completion.Value.Usage?.OutputTokenCount ?? 0,
                    TotalTokens = completion.Value.Usage?.TotalTokenCount ?? 0,
                    Duration = duration
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[聊天] 非流式聊天失败");
                throw;
            }
        }

        /// <summary>
        /// 多模态聊天（支持图片）
        /// 用于 VL (Vision Language) 模型，如 Qwen-VL、GPT-4V 等
        /// </summary>
        /// <param name="chatClient">ChatClient 实例</param>
        /// <param name="userMessage">用户消息</param>
        /// <param name="imageUrls">图片URL列表（可以是 base64 data URI 或 http URL）</param>
        /// <param name="systemPrompt">系统提示</param>
        /// <param name="options">聊天选项（从 BuildChatCompletionOptions 构建，可选）</param>
        /// <param name="cancellationToken">取消令牌</param>
        public async Task<ChatResponse> ChatWithImagesAsync(
            ChatClient chatClient,
            string userMessage,
            List<string> imageUrls,
            string systemPrompt = "你是一个有帮助的AI助手，可以理解和分析图片。",
            ChatCompletionOptions options = null,
            CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;
            _logger.LogInformation("[多模态聊天] 开始: ImageCount={ImageCount}", imageUrls?.Count ?? 0);

            try
            {
                // 构造多模态消息内容
                var contentParts = new List<ChatMessageContentPart>();

                // 添加图片
                if (imageUrls != null && imageUrls.Count > 0)
                {
                    foreach (var imageUrl in imageUrls)
                    {
                        if (imageUrl.StartsWith("data:image/"))
                        {
                            // Base64 格式：data:image/png;base64,xxxxx
                            var parts = imageUrl.Split(',');
                            if (parts.Length == 2)
                            {
                                var mimeType = parts[0].Replace("data:", "").Replace(";base64", "");
                                var base64Data = parts[1];
                                var imageBytes = Convert.FromBase64String(base64Data);
                                contentParts.Add(ChatMessageContentPart.CreateImagePart(
                                    new BinaryData(imageBytes), mimeType));
                            }
                        }
                        else
                        {
                            // HTTP URL 格式
                            contentParts.Add(ChatMessageContentPart.CreateImagePart(new Uri(imageUrl)));
                        }
                    }
                }

                // 添加文本消息
                contentParts.Add(ChatMessageContentPart.CreateTextPart(userMessage));

                // 构造消息列表
                var messages = new List<ChatMessage>
                {
                    new SystemChatMessage(systemPrompt),
                    new UserChatMessage(contentParts)
                };

                // 调用 API（自动应用自定义参数）
                options ??= _clientOptionsCache.GetValueOrDefault(chatClient) ?? new ChatCompletionOptions();
                var completion = await chatClient.CompleteChatAsync(messages, options, cancellationToken);

                var duration = DateTime.UtcNow - startTime;
                _logger.LogInformation("[多模态聊天] 完成: Duration={Duration}ms, Tokens={Tokens}",
                    duration.TotalMilliseconds, completion.Value.Usage?.TotalTokenCount ?? 0);

                return new ChatResponse
                {
                    Content = completion.Value.Content[0].Text ?? string.Empty,
                    FinishReason = completion.Value.FinishReason.ToString(),
                    InputTokens = completion.Value.Usage?.InputTokenCount ?? 0,
                    OutputTokens = completion.Value.Usage?.OutputTokenCount ?? 0,
                    TotalTokens = completion.Value.Usage?.TotalTokenCount ?? 0,
                    Duration = duration
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[多模态聊天] 失败");
                throw;
            }
        }

        /// <summary>
        /// 流式聊天
        /// 直接使用 OpenAI Streaming API
        /// </summary>
        public async IAsyncEnumerable<string> ChatStreamingAsync(
            ChatClient chatClient,
            string userMessage,
            string systemPrompt = "你是一个有帮助的AI助手。",
            ChatCompletionOptions options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("[聊天] 开始流式聊天");

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(systemPrompt),
                new UserChatMessage(userMessage)
            };

            // 流式调用（自动应用自定义参数）
            options ??= _clientOptionsCache.GetValueOrDefault(chatClient) ?? new ChatCompletionOptions();
            await foreach (var update in chatClient.CompleteChatStreamingAsync(messages, options, cancellationToken))
            {
                foreach (var contentPart in update.ContentUpdate)
                {
                    if (!string.IsNullOrEmpty(contentPart.Text))
                    {
                        yield return contentPart.Text;
                    }
                }
            }

            _logger.LogInformation("[聊天] 流式聊天完成");
        }

        /// <summary>
        /// 根据模型ID创建 ChatClient
        /// 支持: OpenAI / DeepSeek / vLLM / Ollama
        /// 自动从模型的 CustomRequestParams 注入 HTTP 管道策略（对所有调用路径生效，含 IChatClient/Agent）
        /// </summary>
        public async Task<ChatClient> CreateChatClientByModelIdAsync(long modelId)
        {
            var model = await _modelService.GetByIdAsync(modelId);
            if (model == null)
            {
                throw new InvalidOperationException($"模型不存在: ModelId={modelId}");
            }

            if (model.IsEnabled != true || model.IsDeleted)
            {
                throw new InvalidOperationException($"模型已禁用或删除: {model.ModelName}");
            }

            _logger.LogInformation("[ChatClient] 动态创建: ModelId={ModelId}, Provider={Provider}",
                modelId, model.Provider);

            // 将 CustomRequestParams 通过 HTTP 管道策略注入（而非 _clientOptionsCache），
            // 确保 ChatAsync / IChatClient / ChatClientAgent 等所有调用路径都能生效
            var client = CreateChatClient(model, model.CustomRequestParams);

            return client;
        }

        /// <summary>
        /// 从 CustomRequestParams JSON 构建 ChatCompletionOptions
        /// 所有参数通过 Patch.Set() 直接注入到请求体
        /// 仅跳过 model、messages、stream（由 SDK/调用方控制）
        /// </summary>
        public ChatCompletionOptions BuildChatCompletionOptions(string customRequestParamsJson)
        {
            var options = new ChatCompletionOptions();

            if (string.IsNullOrWhiteSpace(customRequestParamsJson))
            {
                return options;
            }

            try
            {
                using var doc = JsonDocument.Parse(customRequestParamsJson);
                var root = doc.RootElement;

                // model、messages、stream 由 SDK 和调用方式控制，不需要从配置注入
                var skipKeys = new HashSet<string> { "model", "messages", "stream" };

                int injectedCount = 0;
                var injectedKeys = new List<string>();

#pragma warning disable SCME0001 // JsonPatch 实验性 API
                foreach (var prop in root.EnumerateObject())
                {
                    if (skipKeys.Contains(prop.Name))
                        continue;

                    // JsonPatch.Set 的 jsonPath 参数是 ReadOnlySpan<byte>（UTF-8）
                    var pathBytes = System.Text.Encoding.UTF8.GetBytes($"$.{prop.Name}");

                    switch (prop.Value.ValueKind)
                    {
                        case JsonValueKind.String:
                            options.Patch.Set(pathBytes, prop.Value.GetString());
                            break;
                        case JsonValueKind.Number:
                            if (prop.Value.TryGetInt32(out int intVal))
                                options.Patch.Set(pathBytes, intVal);
                            else
                                options.Patch.Set(pathBytes, prop.Value.GetDouble());
                            break;
                        case JsonValueKind.True:
                            options.Patch.Set(pathBytes, true);
                            break;
                        case JsonValueKind.False:
                            options.Patch.Set(pathBytes, false);
                            break;
                        case JsonValueKind.Null:
                            options.Patch.SetNull(pathBytes);
                            break;
                        case JsonValueKind.Object:
                        case JsonValueKind.Array:
                            // 复杂对象/数组：传入 UTF8 原始 JSON 字节
                            options.Patch.Set(pathBytes, System.Text.Encoding.UTF8.GetBytes(prop.Value.GetRawText()));
                            break;
                    }

                    injectedCount++;
                    injectedKeys.Add(prop.Name);
                }
#pragma warning restore SCME0001

                if (injectedCount > 0)
                {
                    _logger.LogInformation("[ChatOptions] 已注入 {Count} 个自定义参数: {Keys}",
                        injectedCount, string.Join(", ", injectedKeys));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ChatOptions] 解析 CustomRequestParams 失败, 使用默认参数");
            }

            return options;
        }

        /// <summary>
        /// 创建 ChatClient（根据数据库模型配置）
        /// 统一使用 OpenAI API 格式（兼容 OpenAI/DeepSeek/vLLM/Ollama）
        /// </summary>
        /// <param name="model">模型实体</param>
        /// <param name="customParamsJson">CustomRequestParams JSON（非空时注入 HTTP 管道策略，对所有调用路径生效）</param>
        private ChatClient CreateChatClient(LLM_ModelEntity model, string? customParamsJson = null)
        {
            _logger.LogInformation("[ChatClient] 创建: Provider={Provider}, Model={Model}", model.Provider, model.ModelCode);

            // 构造 OpenAI Client Options
            var options = new OpenAIClientOptions();
            
            if (!Uri.TryCreate(model.BaseUrl, UriKind.Absolute, out var endpoint) ||
                (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException($"模型 {model.Id} 未配置有效连接地址");
            if (string.IsNullOrWhiteSpace(model.ModelCode))
                throw new InvalidOperationException($"模型 {model.Id} 未配置模型编码");
            options.Endpoint = endpoint;

            // API Key（Ollama 可以用空字符串）
            string apiKey = model.ApiKey ?? "dummy-key";
            if (model.Provider?.ToLower() == "ollama" && string.IsNullOrWhiteSpace(model.ApiKey))
            {
                apiKey = "ollama"; // Ollama 不需要真实密钥，但不能为空
            }

            // 注入自定义参数 HTTP 管道策略：对该 Client 的所有请求生效（含 IChatClient/Agent 路径）
            if (!string.IsNullOrWhiteSpace(customParamsJson))
            {
                options.AddPolicy(
                    new InjectJsonPropertiesPolicy(customParamsJson, _logger),
                    PipelinePosition.PerCall);
                _logger.LogInformation("[ChatClient] 已注册 CustomRequestParams HTTP 注入策略");
            }

            // 创建 OpenAI Client（兼容所有 OpenAI API 格式的服务）
            var client = new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey), options);
            
            // 获取 ChatClient
            return client.GetChatClient(model.ModelCode);
        }

        #endregion
    }

    /// <summary>
    /// HTTP 管道策略：将 CustomRequestParams JSON 的顶层字段注入到每个请求体中。
    /// 仅注入请求体中尚不存在的字段（调用方显式传入的参数优先）。
    /// 适用于 chat_template_kwargs 等模型专用扩展参数，对 ChatAsync / IChatClient / ChatClientAgent 等所有调用路径统一生效。
    /// </summary>
    internal sealed class InjectJsonPropertiesPolicy : PipelinePolicy
    {
        private readonly byte[] _extraPropsBytes;
        private readonly ILogger _logger;

        internal InjectJsonPropertiesPolicy(string customRequestParamsJson, ILogger logger)
        {
            _logger = logger;
            try
            {
                // 预验证 JSON 有效性，并预存 UTF-8 字节
                using var validate = JsonDocument.Parse(customRequestParamsJson);
                _extraPropsBytes = Encoding.UTF8.GetBytes(customRequestParamsJson);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[InjectPolicy] CustomRequestParams 不是有效 JSON，策略将不注入任何内容");
                _extraPropsBytes = Array.Empty<byte>();
            }
        }

        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            TryInjectBody(message);
            ProcessNext(message, pipeline, currentIndex);
        }

        public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            TryInjectBody(message);
            await ProcessNextAsync(message, pipeline, currentIndex);
        }

        private void TryInjectBody(PipelineMessage message)
        {
            if (_extraPropsBytes.Length == 0) return;
            if (message.Request?.Content == null) return;

            try
            {
                // 读取原始请求体
                using var bodyStream = new MemoryStream();
                message.Request.Content.WriteTo(bodyStream, default);
                var bodyBytes = bodyStream.ToArray();
                if (bodyBytes.Length == 0) return;

                var originalBody = Encoding.UTF8.GetString(bodyBytes);
                var originalPreview = originalBody.Length > 4000 ? originalBody.Substring(0, 4000) + "...[truncated]" : originalBody;

                _logger.LogInformation("[InjectPolicy] Request before inject: Method={Method}, Uri={Uri}, Length={Length}, Preview={Preview}",
                    message.Request.Method,
                    message.Request.Uri,
                    bodyBytes.Length,
                    originalPreview);
                WriteLogHelper.WriteLogsAsync(
                    $"[InjectPolicy][Before] Method={message.Request.Method}, Uri={message.Request.Uri}, Length={bodyBytes.Length}\n{originalPreview}",
                    "LLM_Request_BeforeInject");

                // 仅处理 JSON Object 请求体
                using var bodyDoc = JsonDocument.Parse(bodyBytes);
                if (bodyDoc.RootElement.ValueKind != JsonValueKind.Object) return;

                using var extraDoc = JsonDocument.Parse(_extraPropsBytes);
                if (extraDoc.RootElement.ValueKind != JsonValueKind.Object) return;

                // 合并：已有字段优先，额外字段仅在键不存在时注入
                var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using var mergedStream = new MemoryStream();
                using var writer = new Utf8JsonWriter(mergedStream);
                writer.WriteStartObject();
                foreach (var prop in bodyDoc.RootElement.EnumerateObject())
                {
                    prop.WriteTo(writer);
                    existingKeys.Add(prop.Name);
                }
                foreach (var prop in extraDoc.RootElement.EnumerateObject())
                {
                    if (!existingKeys.Contains(prop.Name))
                        prop.WriteTo(writer);
                }
                writer.WriteEndObject();
                writer.Flush();

                var mergedBytes = mergedStream.ToArray();
                var mergedBody = Encoding.UTF8.GetString(mergedBytes);
                var mergedPreview = mergedBody.Length > 4000 ? mergedBody.Substring(0, 4000) + "...[truncated]" : mergedBody;

                _logger.LogInformation("[InjectPolicy] Request after inject: Method={Method}, Uri={Uri}, Length={Length}, Preview={Preview}",
                    message.Request.Method,
                    message.Request.Uri,
                    mergedBytes.Length,
                    mergedPreview);
                WriteLogHelper.WriteLogsAsync(
                    $"[InjectPolicy][After] Method={message.Request.Method}, Uri={message.Request.Uri}, Length={mergedBytes.Length}\n{mergedPreview}",
                    "LLM_Request_AfterInject");

                message.Request.Content = BinaryContent.Create(new BinaryData(mergedBytes));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[InjectPolicy] 请求体注入失败，不影响原始请求");
            }
        }
    }

    /// <summary>
    /// 聊天响应
    /// </summary>
    public class ChatResponse
    {
        public string Content { get; set; } = string.Empty;
        public string FinishReason { get; set; } = string.Empty;
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int TotalTokens { get; set; }
        public TimeSpan Duration { get; set; }
    }
}
