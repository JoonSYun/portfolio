using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;

namespace Portfolio.ErpInterface.CoreAbstraction
{
    /// <summary>
    /// [담당업무 1] 반복 관심사의 공통화 — 24개 오퍼레이션의 상위 Core 클래스.
    ///
    /// 조회 9종·수신 15종 어느 인터페이스든 하는 일은 같았다:
    ///   전문 받기 → 유효성 검증 → 데이터 복원(코드 정규화·기본값·널 보정) → 처리 → 예외를 ERP 규격 응답으로 변환.
    /// 이 네 가지를 여기서 한 번만 구현한다. 파생 오퍼레이션은 <see cref="Handle"/> 과
    /// 필요한 검증 규칙만 쓴다 → 신규 인터페이스 추가 비용 최소화.
    /// </summary>
    public abstract class InterfaceOperationBase<TRequest, TResponse>
        where TRequest : ErpRequestBase
        where TResponse : ErpResponseBase, new()
    {
        public abstract string OperationCode { get; }        // 예: IF_ORD_001

        public TResponse Execute(TRequest request)
        {
            var log = InterfaceLog.Begin(OperationCode, request.BrandCode, request.TransactionId);
            try
            {
                // ① 유효성 검증 — 공통 규칙(필수값·브랜드·전문 버전) + 파생 규칙
                var errors = CommonValidator.Validate(request).Concat(Validate(request)).ToList();
                if (errors.Count > 0)
                    return log.Complete(Reject(errors));

                // ② 데이터 복원 — 상대 시스템이 보낸 값을 자사 규격으로 정규화
                Restore(request);

                // ③ 처리 — 파생 오퍼레이션의 고유 로직
                var response = Handle(request);
                response.ResultCode = "0000";
                response.TransactionId = request.TransactionId;
                return log.Complete(response);
            }
            catch (SqlException ex) when (ex.Number == 1205)   // 데드락
            {
                return log.Fail(Translate("9101", "일시적 잠금 충돌, 재전송 요청", ex));
            }
            catch (BusinessRuleException ex)
            {
                return log.Fail(Translate(ex.Code, ex.Message, ex));
            }
            catch (Exception ex)
            {
                // ④ 예외 변환 — 내부 예외를 ERP 가 이해하는 규격 코드로. 스택은 로그에만.
                return log.Fail(Translate("9999", "내부 처리 오류", ex));
            }
        }

        /// <summary>파생: 추가 검증 규칙. 기본은 없음.</summary>
        protected virtual IEnumerable<ValidationError> Validate(TRequest request) => Enumerable.Empty<ValidationError>();

        /// <summary>파생: 데이터 복원. 기본은 공통 복원(코드 트림·대문자·널→기본값)만.</summary>
        protected virtual void Restore(TRequest request) => CommonRestorer.Restore(request);

        /// <summary>파생: 고유 처리.</summary>
        protected abstract TResponse Handle(TRequest request);

        private static TResponse Reject(List<ValidationError> errors) => new TResponse
        {
            ResultCode = "1001",
            ResultMessage = string.Join("; ", errors.Select(e => $"{e.Field}: {e.Message}"))
        };

        private static TResponse Translate(string code, string message, Exception ex) => new TResponse
        {
            ResultCode = code,
            ResultMessage = message,
            ErrorDetail = ex.GetType().Name   // 상대에게는 타입명까지만
        };
    }

    // ---- 전문 규격 ------------------------------------------------------------------

    public abstract class ErpRequestBase
    {
        public string TransactionId { get; set; }
        public string BrandCode { get; set; }
        public string InterfaceVersion { get; set; }
        public DateTime SentAt { get; set; }
    }

    public abstract class ErpResponseBase
    {
        public string TransactionId { get; set; }
        public string ResultCode { get; set; }       // 0000 성공 / 1xxx 검증 / 9xxx 시스템
        public string ResultMessage { get; set; }
        public string ErrorDetail { get; set; }
    }

    public sealed class ValidationError
    {
        public string Field { get; }
        public string Message { get; }
        public ValidationError(string field, string message) { Field = field; Message = message; }
    }

    public sealed class BusinessRuleException : Exception
    {
        public string Code { get; }
        public BusinessRuleException(string code, string message) : base(message) { Code = code; }
    }

    public static class CommonValidator
    {
        public static IEnumerable<ValidationError> Validate(ErpRequestBase r)
        {
            if (string.IsNullOrWhiteSpace(r.TransactionId)) yield return new ValidationError("TransactionId", "필수");
            if (string.IsNullOrWhiteSpace(r.BrandCode)) yield return new ValidationError("BrandCode", "필수");
            if (r.InterfaceVersion != "1.2") yield return new ValidationError("InterfaceVersion", "지원하지 않는 버전 " + r.InterfaceVersion);
        }
    }

    public static class CommonRestorer
    {
        public static void Restore(ErpRequestBase r)
        {
            r.BrandCode = r.BrandCode?.Trim().ToUpperInvariant();
            if (r.SentAt == default) r.SentAt = DateTime.Now;
        }
    }

    public sealed class InterfaceLog
    {
        public static InterfaceLog Begin(string op, string brand, string txId) => new InterfaceLog();
        public T Complete<T>(T response) => response;
        public T Fail<T>(T response) => response;
    }
}
