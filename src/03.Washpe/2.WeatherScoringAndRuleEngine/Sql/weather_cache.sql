-- [담당업무 2] 격자 단위 서버 캐시 테이블.

-- weather_cache: 서버사이드 날씨 캐시 (KMA 5km 격자 기반, 크로스유저 공유)
CREATE TABLE weather_cache (
  grid_nx       INT NOT NULL,
  grid_ny       INT NOT NULL,
  forecast_type TEXT NOT NULL,  -- 'hourly' | 'daily'
  data_json     JSONB NOT NULL,
  fetched_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (grid_nx, grid_ny, forecast_type)
);

-- RLS: 20260319100000_weather_cache_rls.sql에서 활성화
-- 정책 없음 → anon/authenticated 차단, service_role은 RLS 우회

-- 캐시 정화: pg_cron으로 1시간마다 6시간 이상 된 row 삭제
-- SELECT cron.schedule('weather-cache-cleanup', '0 * * * *',
--   $$DELETE FROM weather_cache WHERE fetched_at < now() - INTERVAL '6 hours'$$
-- );
