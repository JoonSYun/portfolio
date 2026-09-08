namespace Portfolio.UnifiedScheduler.Infrastructure.Database
{
    /// <summary>
    /// <c>appsettings.{Environment}.json</c> 의 <c>ConnectionStrings</c> 키 이름 모음.
    /// <see cref="Microsoft.Extensions.Configuration.ConfigurationExtensions.GetConnectionString"/> 및
    /// <see cref="SchedulerConnectionFactory.CreateAsync"/> 호출 시 문자열 하드코딩 대신 이 상수를 사용한다.
    /// 상수 값은 설정 파일의 키와 정확히 일치해야 한다(대소문자 포함).
    /// </summary>
    /// <remarks>
    /// 실제 운영에서는 WMS 운영 DB 외에 브랜드별 ERP 인터페이스 DB(IF 단계 SP 호출용) 키가 여러 개 더 있다.
    /// 포트폴리오에서는 카탈로그명을 일반화하고 대표 키만 남겼다.
    /// </remarks>
    public static class DBNames
    {
        /// <summary>WMS 운영 DB. 온라인 주문 마스터/상세·처리이력 등 대부분 도메인 잡의 최종 INSERT/UPDATE 타깃.</summary>
        public const string WmsMain = "WmsMain";

        /// <summary>Hangfire SQL Server 스토리지 전용 DB. 키 이름이 Hangfire 설정에 묶여 있어 변경 금지.</summary>
        public const string BackGroundJob = "BackGroundJob";

        /// <summary>브랜드 ERP 인터페이스 DB. IF 단계 SP 호출용 — 회수요청(RECALL) 계열 Job 의 진입 DB.</summary>
        public const string ErpInterface = "ErpInterface";
    }
}
