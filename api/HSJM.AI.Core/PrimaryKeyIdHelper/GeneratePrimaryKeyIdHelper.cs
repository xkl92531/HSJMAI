using System.Threading;
using System.Threading.Tasks;

namespace HSJM.AI.Core
{
    /// <summary>
    /// 生成主键id类
    /// </summary>
    public static class GeneratePrimaryKeyIdHelper
    {
        static Snowflake snowflake;

        static GeneratePrimaryKeyIdHelper()
        {
            int machinId = AppsettingsConfig.MachineId ?? 1;
            int datacenterId = AppsettingsConfig.DataCenterId ?? 1;
            snowflake = new Snowflake(machinId, datacenterId);
        }

        /// <summary>
        /// 生成主键id
        /// </summary>
        /// <returns></returns>
        public static long GetPrimaryKeyId()
        {
            return snowflake.GetId();
        }

        private static AsyncLock _lockObj = new AsyncLock();
        /// <summary>
        /// 生成主键id(带线程锁)
        /// </summary>
        /// <returns></returns>
        public static long GetLockPrimaryKeyId()
        {
            using (_lockObj.LockAsync().Result)
            {
                return snowflake.GetId();
            }


        }
    }
}
