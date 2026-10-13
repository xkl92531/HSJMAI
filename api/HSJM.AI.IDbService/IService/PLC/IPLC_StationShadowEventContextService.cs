using System.Threading.Tasks;

namespace HSJM.AI.IDbService
{
    public interface IPLC_StationShadowEventContextService : IBaseService<PLC_StationShadowEventContextEntity>
    {
        /// <summary>
        /// 确保事件现场固化表存在（CodeFirst InitTables + event_id 索引，幂等，进程内只执行一次）。
        /// </summary>
        Task EnsureSchemaAsync();
    }
}
