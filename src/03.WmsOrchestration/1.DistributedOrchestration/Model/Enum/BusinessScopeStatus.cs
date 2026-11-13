namespace Portfolio.WmsOrchestration.Model
{
    /// <summary>
    /// 비즈니스 로직 처리 결과 상태
    /// - ORCHESTRATION STATUS와 독립적으로 관리
    /// - 실제 업무 처리 성공/실패 여부를 나타냄
    /// </summary>
    public enum BusinessScopeStatus
    {
        /// <summary>대기 중</summary>
        PENDING,

        /// <summary>완전 성공 (모든 식별자 처리 성공)</summary>
        COMPLETED,

        /// <summary>부분 성공 (일부 식별자 실패)</summary>
        PARTIAL_SUCCESS,

        /// <summary>완전 실패 (모든 식별자 처리 실패)</summary>
        FAILED
    }
}