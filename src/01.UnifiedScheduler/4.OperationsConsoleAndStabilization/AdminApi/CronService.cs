using Cronos;
using Portfolio.Admin.Models;

namespace Portfolio.Admin.Infrastructure;

/// <summary>
/// Thin Cronos wrapper — cron 식 검증 전용.
/// 다음 발화 시각 미리보기는 admin UI 클라이언트(cron-parser)에서 직접 계산한다.
/// </summary>
public class CronService
{
    public CronValidationResult Validate(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return new CronValidationResult { Valid = false, Error = "Expression is empty." };

        try
        {
            CronExpression.Parse(expression);
            return new CronValidationResult { Valid = true };
        }
        catch (Exception ex)
        {
            return new CronValidationResult { Valid = false, Error = ex.Message };
        }
    }
}
