using System;
using static Portfolio.WmsOrchestration.Model.Register;

namespace Portfolio.WmsOrchestration.Model
{
    public interface IMasterService<T>
    {
        Task<RespMaster<T>> GetProcessAsync<T>(params object[] arrayvalues);

        Task<RespDetail<T,HistoryData>> GetDetailProcessAsync<T>(params string[] arrayvalues);

        Task<RegisterResp> ProcessAsync<T>(Guid gid, List<T> liReqData, string hostCode, string apiCode, string slipDiv, int currentPage, string jobDiv = "") where T : iMasterEntity;
    }

}
