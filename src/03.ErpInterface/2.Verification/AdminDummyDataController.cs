// [담당업무 2] 관리자 페이지 (ASP.NET Core + React) — 인터페이스별 더미 전문 생성 → 검증 → (선택) 전송
// 현업/QA 가 개발자 없이 시나리오를 돌릴 수 있도록 웹으로 노출했다.

using Microsoft.AspNetCore.Mvc;
using Portfolio.ErpInterface.CoreAbstraction.Operations;
using Portfolio.ErpInterface.Verification;

namespace Portfolio.ErpInterface.AdminPage
{
    [ApiController, Route("api/admin/interface")]
    public sealed class AdminDummyDataController : ControllerBase
    {
        private readonly SchemaDummyDataGenerator _generator;
        private readonly InterfaceValidator _validator;
        private readonly InterfaceTransmitter _transmitter;

        public AdminDummyDataController(SchemaDummyDataGenerator g, InterfaceValidator v, InterfaceTransmitter t)
        { _generator = g; _validator = v; _transmitter = t; }

        /// <summary>인터페이스 코드로 유효 더미 전문 생성 (실제 마스터 코드 샘플링).</summary>
        [HttpPost("{operationCode}/generate")]
        public IActionResult Generate(string operationCode, [FromQuery] int listSize = 3) => operationCode switch
        {
            "IF_ORD_011" => Ok(_generator.Generate<OnlineOrderRequest>(listSize)),
            "IF_STK_003" => Ok(_generator.Generate<StockQueryRequest>(listSize)),
            _ => NotFound(operationCode)
        };

        /// <summary>전문 검증만 — 전송 없음. 오퍼레이션과 동일한 규칙을 쓴다.</summary>
        [HttpPost("IF_ORD_011/validate")]
        public IActionResult ValidateOrder([FromBody] OnlineOrderRequest req) =>
            Ok(_validator.Validate(req, OnlineOrderRules.Validate));

        /// <summary>검증 통과 전문만 전송 (상대 준비 전에는 스텁 엔드포인트).</summary>
        [HttpPost("IF_ORD_011/send")]
        public async Task<IActionResult> SendOrder([FromBody] OnlineOrderRequest req)
        {
            var report = _validator.Validate(req, OnlineOrderRules.Validate);
            if (!report.IsValid) return BadRequest(report.ToString());
            return Ok(await _transmitter.SendAsync<OnlineOrderRequest, OnlineOrderResponse>("ReceiveOnlineOrder", req, report));
        }
    }
}
