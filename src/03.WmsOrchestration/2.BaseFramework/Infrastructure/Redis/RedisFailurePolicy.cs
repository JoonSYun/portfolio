using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Portfolio.WmsOrchestration.Model
{
    /// <summary>
    /// Redis 작업 실패 시 처리 정책
    /// </summary>
    public enum RedisFailurePolicy
    {
        /// <summary>
        /// Redis 장애 시 작업 실패로 처리 (기본값)
        /// 분산 락의 정합성을 보장하지만, Redis 장애 시 비즈니스 로직이 중단됨
        /// </summary>
        FailOnError,

        /// <summary>
        /// Redis 장애 시 작업 성공으로 처리
        /// 비즈니스 연속성을 보장하지만, 분산 락의 정합성이 깨질 수 있음
        /// 주의: WMS 등 중복 처리가 치명적인 시스템에서는 사용 금지
        /// </summary>
        AllowOnError
    }
}
