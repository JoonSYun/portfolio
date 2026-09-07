using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Portfolio.ErpInterface.CoreAbstraction;

namespace Portfolio.ErpInterface.Verification
{
    /// <summary>
    /// [담당업무 2] 검증기 — 전문이 우리 규격을 통과하는지만 본다. 어디로도 보내지 않는다.
    /// 생성기 → 검증기 만으로 상대 시스템 없이 전 시나리오(정상·필수값 누락·수량 이상·버전 불일치)를 사전 검증했다.
    /// </summary>
    public sealed class InterfaceValidator
    {
        public ValidationReport Validate<TRequest>(TRequest request, Func<TRequest, IEnumerable<ValidationError>> operationRules)
            where TRequest : ErpRequestBase
        {
            var errors = CommonValidator.Validate(request).Concat(operationRules(request)).ToList();
            return new ValidationReport(request.TransactionId, errors);
        }

        /// <summary>시나리오 매트릭스 — 정상 전문에 결함을 하나씩 주입해 검증기가 잡는지 확인.</summary>
        public IEnumerable<ScenarioResult> RunScenarios<TRequest>(TRequest valid, IEnumerable<(string Name, Action<TRequest> Mutate)> scenarios,
            Func<TRequest, IEnumerable<ValidationError>> operationRules, Func<TRequest, TRequest> clone) where TRequest : ErpRequestBase
        {
            yield return new ScenarioResult("정상", Validate(valid, operationRules).IsValid, expectValid: true);
            foreach (var s in scenarios)
            {
                var mutated = clone(valid);
                s.Mutate(mutated);
                yield return new ScenarioResult(s.Name, Validate(mutated, operationRules).IsValid, expectValid: false);
            }
        }
    }

    /// <summary>
    /// 전송기 — 검증과 완전히 분리. 검증된 전문만 받아 실제 엔드포인트(또는 스텁)로 보낸다.
    /// 상대가 준비되면 Endpoint 만 바꾼다. 검증 코드는 한 줄도 안 바뀐다.
    /// </summary>
    public sealed class InterfaceTransmitter
    {
        private readonly ISoapEndpoint _endpoint;
        public InterfaceTransmitter(ISoapEndpoint endpoint) { _endpoint = endpoint; }

        public async Task<TResponse> SendAsync<TRequest, TResponse>(string operation, TRequest request, ValidationReport report)
            where TRequest : ErpRequestBase where TResponse : ErpResponseBase
        {
            if (!report.IsValid) throw new InvalidOperationException("검증 실패 전문은 전송하지 않는다: " + report);
            return await _endpoint.InvokeAsync<TRequest, TResponse>(operation, request);
        }
    }

    public interface ISoapEndpoint { Task<TResponse> InvokeAsync<TRequest, TResponse>(string operation, TRequest request); }

    public sealed class ValidationReport
    {
        public string TransactionId { get; }
        public IReadOnlyList<ValidationError> Errors { get; }
        public bool IsValid => Errors.Count == 0;
        public ValidationReport(string txId, List<ValidationError> errors) { TransactionId = txId; Errors = errors; }
        public override string ToString() => IsValid ? "OK" : string.Join("; ", Errors.Select(e => e.Field + ": " + e.Message));
    }

    public sealed class ScenarioResult
    {
        public string Name { get; }
        public bool Passed { get; }
        public ScenarioResult(string name, bool actualValid, bool expectValid) { Name = name; Passed = actualValid == expectValid; }
    }
}
