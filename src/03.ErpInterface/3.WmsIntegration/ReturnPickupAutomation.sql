-- [담당업무 3] 반품 회수 지시 자동화 — 통합 스케줄러 도메인으로 등록
--
-- ERP 에서 반품 요청(IF_RTN_*)을 수신하면 WMS 에 "회수 대기" 상태로 쌓인다.
-- 이전에는 담당자가 매일 오전 화면에서 회수 지시를 수동으로 냈다.
-- 통합 스케줄러(01.UnifiedScheduler)에 RETURN_PICKUP 도메인을 등록하는 것으로 자동화했다.
-- 코드 배포 없이 — 아래 두 행이 전부다. 파생 Job 은 ReturnPickupInstructionJob (01/2.ExecutionPipeline/Jobs).

INSERT sch.DomainSchedule (DomainCode, GroupCode, CronExpression, QueueName, TimeoutSeconds, MaxRetry, ParametersJson, BrandCodesCsv)
VALUES ('RETURN_PICKUP', 'ERP_IF', '0 30 8 * * ?', 'erp-interface', 600, 2,
        N'{"centerCode":"C01","carrier":"DEFAULT"}',
        'BRAND_A,BRAND_B,BRAND_C,BRAND_D,BRAND_E');            -- 운영 전환 완료된 5개 브랜드

-- 브랜드 C 만 다른 센터에서 회수 — 오버라이드 한 줄
INSERT sch.BrandScheduleOverride (DomainCode, BrandCode, ParameterOverridesJson)
VALUES ('RETURN_PICKUP', 'BRAND_C', N'{"centerCode":"C03"}');
