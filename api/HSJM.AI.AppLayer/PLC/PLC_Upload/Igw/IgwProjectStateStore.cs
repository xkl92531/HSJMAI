using HSJM.AI.IDbService;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace HSJM.AI.AppLayer.PLC.PLC_Upload.Igw
{
    /// <summary>注册 runner / GRAPH 注册器对项目级注册状态的依赖（生产走 IPLC_IgwRegistrationStateService，单测用内存实现）</summary>
    public interface IIgwProjectStateStore
    {
        /// <summary>项目从未注册过（状态表无该项目行）时返回 null——调用方必须对结果做空值判断。</summary>
        Task<IgwRegistrationStateRow> GetStateAsync(long projectId);

        Task SaveIdentityAsync(long projectId, IgwRegistrationIdentity identity);

        Task SaveGraphFailureAsync(long projectId, DateTime? failedAt, string error);

        Task<IReadOnlyList<IgwPeerProjectRow>> ListPeerProjectsAsync(long projectId);
    }

    /// <summary>生产适配器</summary>
    public sealed class IgwProjectStateStore : IIgwProjectStateStore
    {
        private readonly IPLC_IgwRegistrationStateService _service;

        public IgwProjectStateStore(IPLC_IgwRegistrationStateService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        public Task<IgwRegistrationStateRow> GetStateAsync(long projectId) => _service.GetAsync(projectId);

        public Task SaveIdentityAsync(long projectId, IgwRegistrationIdentity identity) => _service.SaveIdentityAsync(projectId, identity);

        public Task SaveGraphFailureAsync(long projectId, DateTime? failedAt, string error) =>
            _service.SaveGraphFailureAsync(projectId, failedAt, error);

        public Task<IReadOnlyList<IgwPeerProjectRow>> ListPeerProjectsAsync(long projectId) => _service.ListPeerProjectsAsync(projectId);
    }
}
