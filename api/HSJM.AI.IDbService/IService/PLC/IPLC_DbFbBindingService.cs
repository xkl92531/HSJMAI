//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/6/15
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// DB块与FB绑定关系服务接口
    /// </summary>
    public interface IPLC_DbFbBindingService : IBaseService<PLC_DbFbBindingEntity>
    {
        /// <summary>
        /// 根据 FB 名称返回该 GRAPH 块工步执行位的变量前缀。
        /// 查 ai_plc_dbfbinding（**始终带项目过滤**，不同项目可能存在同名 FB）：
        /// FB161_HomeGraph → DB161_HomeGraph → "DB161_HomeGraph.RT_DATA.EXEC_BITS.EXEC_BITS"
        /// </summary>
        /// <param name="fbName">FB 块名（如 FB161_HomeGraph），忽略大小写与首尾空白</param>
        /// <param name="projectId">项目ID（必填，防同名 FB 跨项目串数据）</param>
        /// <returns>变量前缀；该项目下 FB 无绑定记录时返回 null</returns>
        Task<string> GetStepVarPrefixByFbNameAsync(string fbName, long projectId);

        /// <summary>
        /// 清空工步前缀缓存（上传事务提交后调用，防止 DB 块重传换绑后读到旧 DB 名）
        /// </summary>
        void InvalidateStepPrefixCache();
    }
}
