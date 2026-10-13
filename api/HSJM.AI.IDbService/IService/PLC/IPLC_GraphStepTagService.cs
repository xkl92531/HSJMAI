//使用平台信息: ID:NETCORE_WebApi  描述:NETCORE_WebApi
//代码版本信息: ID:V4  描述:V4_SqlSugar 添加时间:2026/6/15
namespace HSJM.AI.IDbService
{
    /// <summary>
    /// GRAPH步位注册台账服务接口
    /// </summary>
    public interface IPLC_GraphStepTagService : IBaseService<PLC_GraphStepTagEntity>
    {
        /// <summary>
        /// 查询指定项目、指定实例 DB 名下全部未删除的步位台账记录
        /// （始终带项目过滤，不同项目可能存在同名 DB）
        /// </summary>
        /// <param name="projectId">项目ID</param>
        /// <param name="dbName">实例 DB 块名（如 DB161_HomeGraph）</param>
        Task<List<PLC_GraphStepTagEntity>> GetByProjectAndDbNameAsync(long projectId, string dbName);
    }
}
