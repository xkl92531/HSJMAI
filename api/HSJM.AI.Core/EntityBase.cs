namespace HSJM.AI.Core
{

    /// <summary>
    /// 实体基类
    /// </summary>
    public class EntityBase: IEntity<long>
    {

        public EntityBase()
        {
            Id = GeneratePrimaryKeyIdHelper.GetPrimaryKeyId();
        }
        /// <summary>
        /// 唯一标识
        /// </summary>
        public long Id { get; set; }
    }


}
