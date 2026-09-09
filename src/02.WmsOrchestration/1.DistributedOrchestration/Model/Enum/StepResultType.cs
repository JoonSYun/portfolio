namespace Portfolio.WmsOrchestration.Model
{
    /// <summary>
    /// Step 처리 결과 타입
    /// </summary>
    public enum StepResultType
    {
        /// <summary>전체 성공 → 다음 Step 진행</summary>
        SUCCESS,

        /// <summary>부분 성공 → 실패한 대상 보상 후 다음 Step 진행</summary>
        PARTIAL_SUCCESS,

        /// <summary>비즈니스 실패 → Saga Failed 처리</summary>
        BUSINESS_FAIL
    }
}
