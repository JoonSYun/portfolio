// [담당업무 2] Base Framework — ChunkStepConsumerBase 를 한 번 더 상속한 도메인 베이스. 프로시저 실행 도메인은 ExecuteProcedureAsync 하나만 구현한다.

using MassTransit;
using Microsoft.Extensions.Logging;
using Portfolio.WmsOrchestration.Exceptions;
using Portfolio.WmsOrchestration.Infrastructure;
using Portfolio.WmsOrchestration.Model;
using Portfolio.WmsOrchestration.Saga;
using Portfolio.WmsOrchestration.Service;
using System.Data;

namespace Portfolio.WmsOrchestration.Consumers
{
    /// <summary>
    /// NTISConsumerBase
    /// ----------------
    /// NTIS 도메인 Consumer들의 공통 Base 클래스
    ///
    /// 공통 기능:
    ///   ✔ Redis Lock 획득/해제 (Payload.TargetInfo 기반)
    ///   ✔ 실행 결과 저장 (Payload.TargetInfo 기반)
    ///   ✔ 프로시저 실행 결과 검증
    ///   ✔ 표준 에러 처리
    ///
    /// 파생 클래스가 구현할 것:
    ///   - ExecuteProcedureAsync() : 실제 프로시저 실행 로직만!
    /// </summary>
    public abstract class NTISConsumerBase<TEvent, TPayload>
        : ChunkStepConsumerBase<TEvent, TPayload>
        where TEvent : ChunkStepDomainEventBase
        where TPayload : NTISParamBase
    {
        protected readonly NTISService TisService;
        protected readonly NTISResultService ResultService;
        protected readonly NTISSchedulerRedisManager RedisManager;

        protected NTISConsumerBase(
            ILogger logger,
            NTISService tisService,
            NTISResultService resultService,
            NTISSchedulerRedisManager redisManager)
            : base(logger)
        {
            TisService = tisService;
            ResultService = resultService;
            RedisManager = redisManager;
        }

        /// <summary>
        /// 표준 프로세스 실행 플로우
        /// 1. Payload에서 TargetInfo 추출
        /// 2. Redis Lock 획득
        /// 3. 프로시저 실행
        /// 4. 결과 저장
        /// 5. Redis Lock 해제
        /// </summary>
        protected override async Task<StepConsumerResponse> ProcessAsync(
            ConsumeContext<TEvent> context)
        {
            TPayload? payload = RequestPayload.FirstOrDefault();
            if (payload == null)
            {
                throw new BusinessException("No payload data");
            }

            NTISTargetDto targetInfo = payload.TargetInfo;
            if (targetInfo == null)
            {
                throw new BusinessException("TargetInfo is null in payload");
            }

            try
            {
                // 1. 프로시저 실행 (파생 클래스 구현)
                var result = await ExecuteProcedureAsync(payload);

                // 2. 결과 검증
                bool success = NIFService.IsSuccessResult(result);

                // 3. 결과 저장
                await ResultService.SaveExecutionResultAsync(
                    targetInfo,
                    success ? NTISProcessResult.SUCCESS.ToString() : NTISProcessResult.ERROR.ToString(),
                    DBProcedureService.DataTableToString(result));

                if (!success)
                {
                    // 추후 PartialSuccess로 반환하는 로직으로 변환 검토 필요
                    throw new BusinessException($"{targetInfo.URI} Procedure execution failed");
                }

                return StepConsumerResponse.Success(1);
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    ex,
                    AppLog.Log("[NTISConsumerBase] Error processing NTIS procedure. {Identifier}"),
                    targetInfo.GetLogIdentifier());
                
                // 결과 저장 (실패)
                await ResultService.SaveExecutionResultAsync(
                    targetInfo,
                    NTISProcessResult.ERROR.ToString(),
                    ex.Message);

                throw;
            }
        }

        /// <summary>
        /// 파생 클래스에서 실제 프로시저 실행 로직만 구현
        /// payload.TargetInfo.URI를 사용하여 적절한 프로시저 호출
        /// </summary>
        protected abstract Task<DataTable> ExecuteProcedureAsync(TPayload payload);

        /// <summary>
        /// 기본 보상 로직 (대부분 NTIS는 보상 없음)
        /// </summary>
        protected override Task CompensateAsync(ConsumeContext<TEvent> context)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// 기본 멱등성 체크 (항상 실행)
        /// </summary>
        protected override Task<(IdempotencyResult, ChunkScopeStatus)> CheckIdempotencyAsync(
            ConsumeContext<TEvent> context)
        {
            return Task.FromResult((IdempotencyResult.NONE, ChunkScopeStatus.PENDING));
        }

        /// <summary>
        /// 기본 보상 멱등성 체크
        /// </summary>
        protected override Task<IdempotencyResult> CheckCompensationIdempotencyAsync(
            ConsumeContext<TEvent> context)
        {
            return Task.FromResult(IdempotencyResult.NONE);
        }
    }
}