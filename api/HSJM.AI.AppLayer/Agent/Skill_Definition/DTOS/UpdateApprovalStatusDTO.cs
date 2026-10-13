namespace HSJM.AI.AppLayer.Agent.DTOS
{
    /// <summary>
    /// 更新技能审批状态DTO
    /// </summary>
    public class UpdateApprovalStatusDTO
    {
        /// <summary>
        /// 技能ID
        /// </summary>
        public long Id { get; set; }

        /// <summary>
        /// 审批状态: Pending/Approved/Rejected
        /// </summary>
        public string ApprovalStatus { get; set; }
    }
}
