
using System.Linq.Expressions;
//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/2/7 15:05:56
namespace HSJM.AI.IDbService
{
    public interface IPLC_VariableAccessService : IBaseService<PLC_VariableAccessEntity>
    {
        /// <summary>
        /// 异步软删根据id删除
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        Task<bool> SoftDeleteByIdAsync(object id);


        /// <summary>
        /// 异步根据条件软删
        /// </summary>
        /// <param name="whereExpression"></param>
        /// <returns></returns>
        Task<bool> SoftDeleteAsync(Expression<Func<PLC_VariableAccessEntity, bool>> whereExpression);

        /// <summary>
        /// 异步软删根据ids删除
        /// </summary>
        /// <param name="id">主键</param>
        /// <returns></returns>
        Task<bool> SoftDeleteByIdsAsync(List<long> listId);
         
        /// <summary>
        /// 根据PLC项目读取实时数据
        /// </summary>
        /// <param name="projectId"></param>
        /// <returns></returns>
        Task<List<PLC_ReadTagResult>> GetReadData(long projectId);

        /// <summary>
        /// 根据PLC项目读取指定点位的实时数据
        /// </summary>
        /// <param name="projectId">项目ID</param>
        /// <param name="tagNames">待读取的Tag名称列表</param>
        /// <returns></returns>
        Task<List<PLC_ReadTagResult>> GetReadData(long projectId, List<string> tagNames);
        Task<List<PLC_ReadTagResult>> ReadTagsByFixedDeviceAsync(long projectId, List<string> tagNames);

        /// <summary>
        /// 按完整 BlockName.VariablePath 精确读取 PLC 点位，并按 RequestKey 关联结果。
        /// </summary>
        /// <param name="keepPolling">
        /// 2026-09-22：IGW 的 /Tag 读接口默认只取值、不把点送进扫描计划。
        /// **持续盯同一批点的调用方必须传 true**（当前只有 LineUp 设备监控），否则每轮都现场补读 PLC，
        /// 值是对的但慢，现场容易误判成 PLC/网络问题。读一次就走的调用方（停机诊断快照）保持默认。
        /// </param>
        Task<IReadOnlyList<PLC_ExactReadResult>> ReadExactDataAsync(
            long projectId,
            IReadOnlyCollection<PLC_ExactReadRequest> requests,
            CancellationToken cancellationToken,
            bool keepPolling = false);

        /// <summary>
        /// D-1：约束诊断专用读取（逐点质量；不改 GetReadData 契约）。点集合同 GetReadData（项目 iotag 中
        /// PlcDataTypeList 类型、ConstraintStatus != 6、未删）。老网关沿用 _S7→_Plus 两段读取、坏行不带值；
        /// IGW 项目按台账别名读 IO 点（ReadTagsWithQuality，旧 IGW 回退 ReadTagsAsList 定位坏点）。除取消外不抛异常。
        /// </summary>
        Task<PLC_DiagnosticReadResult> ReadDiagnosticSignalsAsync(long projectId, CancellationToken cancellationToken);

        /// <summary>
        /// PLC 工程重传后失效「精确读取候选回退缓存」（iotag 未命中路径的块接口解析结果，按项目缓存）。
        /// 接口表/块表仅在工程重传时变化；调用本方法后下一轮读取按新元数据重建缓存（另有 TTL 兜底）。
        /// </summary>
        void InvalidateExactCandidateCache(long projectId);

        /// <summary>
        /// 获取属于 DB 块且数据类型为标量的变量名集合（用于 Q 点类型过滤，排除结构体和非 DB 变量）
        /// </summary>
        /// <param name="projectId">项目 ID</param>
        /// <param name="candidateVarNames">待筛选的候选变量名列表（BlockName.VariablePath 格式）</param>
        Task<HashSet<string>> GetScalarDbVarNamesAsync(long projectId, List<string> candidateVarNames);
    }
}
