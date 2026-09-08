using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Portfolio.WmsOrchestration.Saga
{
    /// <summary>
    /// Process 실행 계획 정보
    /// </summary>
    public class ProcessPlanInfo
    {
        public int Index { get; set; }
        public Guid ProcessId { get; set; }
        public string ProcessType { get; set; } = string.Empty;
        public string IdempotencyKey { get; set; } = string.Empty;
    }
}
