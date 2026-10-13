using HSJM.AI.Core;
using HSJM.AI.IDbService;

namespace HSJM.AI.DbService
{
    public class PLC_FaultEventService : AbstractBaseService<PLC_FaultEventEntity>, IPLC_FaultEventService, IAutoInject
    {
    }
}
