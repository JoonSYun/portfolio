using System.ComponentModel.DataAnnotations;

namespace Portfolio.UnifiedScheduler.Models;

/// <summary>
/// Reindexing Job payload — 레거시 SQL Agent "*_Reindexing" 스텝 흡수.
/// 재색인 대상 테이블/인덱스/fillfactor 는 프로시저(<c>usp_DW_스케쥴러_인덱스재구성_Set</c>) 안에 두므로 브랜드 코드만 받는다.
/// </summary>
public class DBReindexParam : ISchedulerParam
{
    [Required] public string BCODE { get; set; } = "";
}
