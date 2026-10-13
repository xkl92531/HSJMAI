using HSJM.AI.Core;
using System;
using System.Collections.Generic;
using System.Text;

namespace HSJM.AI.IDbService
{
    public interface IIotService
    {
        /// <summary>
        /// 读取变量
        /// </summary>
        /// <param name="readDto"></param>
        /// <returns></returns>
        Task<HttpResponseResultModel<List<PLC_ReadTagResult>>> ReadTags(string host, PLC_ReadTagParame readDto);

        /// <summary>
        /// 一次性添加组里面所有变量
        /// </summary>
        /// <param name="host">IOT网关地址</param>
        /// <param name="deviceName">设备名称</param>
        /// <param name="groupName">DB块名称</param>
        /// <returns></returns>
        Task<HttpResponseResultModel<string>> AddGroupTagItems(string host, string deviceName, string groupName);

        /// <summary>
        /// 手动添加I点,Q点
        /// </summary>
        /// <param name="host">IOT网关地址</param>
        /// <param name="deviceName">设备名称</param>
        /// <param name="tagIems">注册标签列表</param>
        /// <returns></returns>
        Task<HttpResponseResultModel<List<ManualAddTagItemsResult>>> ManualAddTagItems(string host, string deviceName, List<IQTagItem> tagIems);

        /// <summary>
        /// 探测网关类型：新 IGW（/Tag/* 契约）或老网关（/DriverInfo/* 契约）。
        /// 探测顺序：GET /swagger/v1/swagger.json 的 paths 判定；swagger 不可用时以 GET /Tag/AddTags 返回 405（路由存在但方法不符）判定。
        /// 任一探测失败均回退 <see cref="GatewayFlavor.Legacy"/>（老网关行为）。结果按规范化 baseUrl 内存缓存 5 分钟。
        /// </summary>
        /// <param name="host">IOT网关地址（可带/不带 scheme、尾斜杠）</param>
        Task<GatewayFlavor> DetectGatewayAsync(string host);

        /// <summary>
        /// 新 IGW：批量注册点位（POST /Tag/AddTags，幂等 add-or-update）。
        /// 返回与老 ManualAddTagItems 同构的逐标签结果；新 IGW 为整批语义（失败时整批 isSuccess=false），成功时按入参顺序全部 IsGood=true。
        /// </summary>
        /// <param name="host">IOT网关地址</param>
        /// <param name="tags">新 IGW 契约点位列表</param>
        Task<HttpResponseResultModel<List<ManualAddTagItemsResult>>> ManualAddTagItemsV2(string host, List<DcsTagUpdateModel> tags);

        /// <summary>
        /// 新 IGW：批量读点位（POST /Tag/ReadTagsAsList，body=[tagName...]，返回 {backResult:[{key,value}]}）。
        /// 内部按 <see cref="DcsIgwConsts.ReadBatchLimit"/>（1000）分批并合并；返回结果与 tagNames 按索引对齐，
        /// 值已过 <see cref="DcsIgwMapper.NormalizeDcsValue"/> 宽松解析（bool/数值）。
        /// </summary>
        /// <param name="host">IOT网关地址</param>
        /// <param name="tagNames">点位名列表（建议用 DcsIgwConsts.BuildTagReadAlias 构造的逻辑别名）</param>
        /// <param name="keepPolling">
        /// 2026-09-22 IGW 契约：默认只取值、不把点送进扫描计划；持续盯同一批点的调用方
        /// （LineUp 设备监控）传 true 才会保持在活动窗口内走缓存。详见 <see cref="IgwReadPath"/>。
        /// </param>
        /// <param name="requestTimeout">
        /// 2026-09-23 单次 HTTP 请求超时（写入 HttpRequestModel.TimeOut，覆盖 HttpClient.Timeout；每 1000 点一批各自计时）。
        /// 调用方只在外面包 WaitAsync 而不传它的话，调用方放弃等待后请求本体仍会跑到 HttpClient 默认的 100 s——
        /// IGW 同一台 PLC 的请求是串行执行的，这些没人等的请求会堵住后面所有读取。
        /// 超时返回 IsSuccess=false、HttpStatusCode=408（RequestTimeout），调用方据此判断不要立即重发。
        /// null = 保持原行为（HttpClient 默认 100 s）。
        /// </param>
        Task<HttpResponseResultModel<List<PLC_ReadTagResult>>> ReadTagsAsList(
            string host, List<string> tagNames, bool keepPolling = false, TimeSpan? requestTimeout = null);

        /// <summary>
        /// 新 IGW（1.6.78+）：POST /Tag/ReadTagsWithQuality 单次调用（调用方保证 1..1000 个别名），逐点返回值与质量。
        /// 不分批、不重试、不吞异常：网络异常/超时原样抛出；非 2xx 时 Envelope=null（旧 IGW 返回 404）。
        /// </summary>
        /// <param name="requestTimeout">
        /// D-1 修复回合 1：请求级超时（写入 HttpRequestModel.TimeOut，覆盖 HttpClient.Timeout）。诊断读取器
        /// 自身对这次调用有一层 WaitAsync 超时（默认 15s）；不下传这个超时的话，HTTP 请求本体会继续跑到
        /// HttpClient 默认的 100s 才真正结束——调用方已经不再等待它，但连接/线程仍然占用到 100s。传 null 时
        /// 保持原行为（不覆盖，走 HttpClient 默认超时），不影响未传此参数的既有调用方。
        /// </param>
        Task<IgwTransportResult<List<DcsTagQualityItem>>> ReadTagsWithQualityAsync(string host, List<string> aliases, TimeSpan? requestTimeout = null);

        /// <summary>
        /// 新 IGW：POST /Tag/ReadTagsAsList 单次调用并返回原始 envelope（isSuccess=false 时 ErrorMessage 为
        /// 「点位质量异常:{别名} …」「点位不存在:{别名}」等），供诊断读取定位坏点。不分批、不重试、不吞异常。
        /// </summary>
        /// <param name="requestTimeout">见 <see cref="ReadTagsWithQualityAsync"/> 的同名参数说明。</param>
        Task<IgwTransportResult<List<DcsTagReadItem>>> ReadTagsAsListRawAsync(string host, List<string> aliases, TimeSpan? requestTimeout = null);
    }
}
