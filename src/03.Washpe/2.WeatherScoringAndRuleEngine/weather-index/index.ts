// [담당업무 2] DWI 단일 엔드포인트 — KMA 격자 단위 PostgreSQL 캐시(사용자 간 공유), 외부 API 부분 실패 허용, 위치별 allSettled.

/**
 * Edge Function: weather-index
 *
 * 단일 요청으로 current + hourly + daily DWI 점수 + chemical advice 반환.
 * KMA 격자 기반 서버 캐시로 크로스유저 공유.
 *
 * POST /weather-index
 * Body: { locations: [{ latitude, longitude, cityName }], timezoneOffset?: number }
 *
 * timezoneOffset: 클라이언트 UTC 오프셋(분). 기본값 540 (KST).
 * 날짜 그룹핑 · 과거 데이터 필터에 사용.
 */

import { supabase } from '../_shared/supabase.ts';
import { getCorsHeaders } from '../_shared/cors.ts';
import { jsonResponse, errorResponse } from '../_shared/response.ts';
import { tryVerifyAuth } from '../_shared/auth.ts';
import { writeRequestLog, detectPlatform } from '../_shared/request_logger.ts';
import { toGrid } from '../_shared/kma_grid.ts';
import {
  fetchHourlyForecast,
  fetchDailyForecast,
  fetchOpenMeteoSupplement,
  fetchOpenMeteoAirQuality,
  mergeWithOpenMeteo,
  type HourlyWeatherDto,
  type DailyForecastDto,
} from '../_shared/kma_api.ts';
import {
  calculateDwi,
  calculateHourlyScores,
  calculateDailyScoreFromForecast,
  aggregateHourlyToDaily,
  hourlyToCondition,
  type DwiResult,
  type HourlyScoreDto,
  type DailyScoreDto,
} from '../_shared/weather_scoring.ts';
import {
  buildChemicalAdvice,
  type ChemicalRow,
  type CatalogRow,
} from '../_shared/chemical_advice.ts';
import { getRulesCached, type AdviceRule } from '../_shared/rule_engine.ts';

const KMA_API_KEY = Deno.env.get('KMA_API_KEY')!;

// ── Cache TTLs ───────────────────────────────────────────────────────────────

const HOURLY_TTL_MINUTES = 30;
const DAILY_TTL_MINUTES = 180; // 3 hours

// ── In-memory catalog cache (Deno isolate warm-start 시 재사용) ──────────────

let catalogCache: { data: CatalogRow[]; fetchedAt: number } | null = null;
const CATALOG_TTL_MS = Number(Deno.env.get('CATALOG_CACHE_TTL_MS') ?? String(60 * 60 * 1000)); // default 1 hour

// ── Types ────────────────────────────────────────────────────────────────────

interface LocationInput {
  latitude: number;
  longitude: number;
  cityName: string;
}

interface CachedWeatherData {
  hourly?: HourlyWeatherDto[];
  daily?: DailyForecastDto[];
}

// ── Date helpers ─────────────────────────────────────────────────────────

/** UTC epoch(초) + offset(ms) → 클라이언트 로컬 날짜 "YYYY-MM-DD". */
function localDateKey(epochSec: number, offsetMs: number): string {
  const d = new Date(epochSec * 1000 + offsetMs);
  const y = d.getUTCFullYear();
  const m = String(d.getUTCMonth() + 1).padStart(2, '0');
  const day = String(d.getUTCDate()).padStart(2, '0');
  return `${y}-${m}-${day}`;
}

// ── Main handler ─────────────────────────────────────────────────────────────

Deno.serve(async (req: Request) => {
  if (req.method === 'OPTIONS') {
    return new Response('ok', { headers: getCorsHeaders(req) });
  }

  const start = performance.now();
  let userId: string | null | undefined;
  let statusCode = 500;

  try {
    // 1. Auth (선택적 — 게스트도 날씨 조회 가능)
    ({ userId } = await tryVerifyAuth(req));

    // 2. Parse input
    const body = await req.json() as {
      locations: LocationInput[];
      timezoneOffset?: number;
    };
    const { locations } = body;
    if (!Array.isArray(locations) || locations.length === 0) {
      return jsonResponse(req, { error: 'locations array is required' }, 400);
    }
    if (locations.length > 10) {
      return jsonResponse(req, { error: 'locations array exceeds maximum size of 10' }, 400);
    }
    // 클라이언트 UTC 오프셋(분). 기본값 540 (KST).
    const timezoneOffset: number = typeof body.timezoneOffset === 'number'
      ? body.timezoneOffset
      : 540;
    const offsetMs = timezoneOffset * 60_000;

    // 3. Fetch user's chemicals + catalog + advice rules (parallel)
    const [chemicals, catalog, rules] = await Promise.all([
      fetchUserChemicals(userId),
      getCatalogCached(),
      getRulesCached(supabase),
    ]);

    // 4. Process each location (parallel, partial failure OK)
    const settled = await Promise.allSettled(
      locations.map((loc) => processLocation(loc, chemicals, catalog, rules, offsetMs)),
    );

    const results = settled
      .filter((r): r is PromiseFulfilledResult<LocationResult> =>
        r.status === 'fulfilled',
      )
      .map((r) => r.value);

    // Log failures
    settled.forEach((r, i) => {
      if (r.status === 'rejected') {
        console.error(
          `weather-index: ${locations[i].cityName} failed:`,
          r.reason,
        );
      }
    });

    statusCode = 200;
    return jsonResponse(req, { results });
  } catch (err) {
    statusCode = 500;
    return errorResponse(req, err, 'weather-index');
  } finally {
    writeRequestLog({
      user_id: userId ?? undefined,
      function_name: 'weather-index',
      action: 'weather_index',
      duration_ms: Math.round(performance.now() - start),
      status_code: statusCode,
      success: statusCode < 400,
      platform: detectPlatform(req),
    }).catch(() => {});
  }
});

// ── Per-location processing ──────────────────────────────────────────────────

interface LocationResult {
  location: LocationInput;
  current: {
    score: number;
    status: string;
    recommendationTitle: string;
    description: string;
    matchingAdvice: string[];
    penalties: DwiResult['penalties'];
    condition: DwiResult['condition'];
  };
  hourlyScores: HourlyScoreDto[];
  dailyScores: DailyScoreDto[];
}

async function processLocation(
  loc: LocationInput,
  chemicals: ChemicalRow[],
  catalog: CatalogRow[],
  rules: AdviceRule[],
  offsetMs: number,
): Promise<LocationResult> {
  const grid = toGrid(loc.latitude, loc.longitude);
  const controller = new AbortController();
  const signal = controller.signal;

  // ── Hourly data (KMA 단기예보 + Open-Meteo 보조) ─────────────────────────
  let hourlyData: HourlyWeatherDto[];

  const cachedHourly = await getCache(grid.nx, grid.ny, 'hourly', HOURLY_TTL_MINUTES);
  if (cachedHourly?.hourly) {
    hourlyData = cachedHourly.hourly;
  } else {
    // KMA 필수, Open-Meteo 보조 (graceful degradation)
    const kmaHourly = await fetchHourlyForecast(grid, KMA_API_KEY, signal);

    const [supplements, airQuality] = await Promise.allSettled([
      fetchOpenMeteoSupplement(loc.latitude, loc.longitude, signal),
      fetchOpenMeteoAirQuality(loc.latitude, loc.longitude, signal),
    ]).then(([s, a]) => [
      s.status === 'fulfilled' ? s.value : [],
      a.status === 'fulfilled' ? a.value : [],
    ]);

    hourlyData = mergeWithOpenMeteo(kmaHourly, supplements, airQuality);
    await setCache(grid.nx, grid.ny, 'hourly', { hourly: hourlyData });
  }

  // ── Daily data (KMA 중기예보) ─────────────────────────────────────────────
  let dailyData: DailyForecastDto[] | null = null;

  const cachedDaily = await getCache(grid.nx, grid.ny, 'daily', DAILY_TTL_MINUTES);
  if (cachedDaily?.daily) {
    dailyData = cachedDaily.daily;
  } else {
    try {
      dailyData = await fetchDailyForecast(loc.cityName, KMA_API_KEY, signal);
      if (dailyData && dailyData.length > 0) {
        await setCache(grid.nx, grid.ny, 'daily', { daily: dailyData });
      }
    } catch {
      // 중기예보 실패 시 빈 리스트로 degradation
      dailyData = null;
    }
  }

  // ── Current condition (closest to now) ────────────────────────────────────
  const now = Math.floor(Date.now() / 1000);
  const closestHourly = hourlyData.reduce((prev, curr) =>
    Math.abs(curr.unixTimestamp - now) < Math.abs(prev.unixTimestamp - now)
      ? curr
      : prev,
  );
  const currentCondition = hourlyToCondition(closestHourly);
  const currentDwi = calculateDwi(currentCondition);
  const matchingAdvice = buildChemicalAdvice(
    rules, currentCondition, chemicals, catalog, currentDwi.score,
  );

  // ── Hourly scores (현재 정시 이후만) ──────────────────────────────────────
  const currentHourEpoch = now - (now % 3600);
  const futureHourlyData = hourlyData.filter(
    (h) => h.unixTimestamp >= currentHourEpoch,
  );
  const hourlyScores = calculateHourlyScores(futureHourlyData);

  // ── Daily scores (단기 집계 + 중기) ───────────────────────────────────────
  const shortTermDaily = aggregateHourlyToDaily(hourlyScores, futureHourlyData, offsetMs);
  const midDailyScores = (dailyData ?? []).map(calculateDailyScoreFromForecast);

  // 중기와 병합 (단기 우선, 날짜 중복 제거)
  const midDates = new Set(midDailyScores.map((d) => d.date));
  const allDailyScores = [
    ...shortTermDaily.filter((d) => !midDates.has(d.date)),
    ...midDailyScores,
  ].sort((a, b) => a.date.localeCompare(b.date));

  // 클라이언트 시간대 기준 "오늘" 이전 날짜 제거
  const todayLocal = localDateKey(now, offsetMs);
  const dailyScores = allDailyScores.filter((d) => d.date >= todayLocal);

  return {
    location: loc,
    current: {
      score: currentDwi.score,
      status: currentDwi.status,
      recommendationTitle: currentDwi.recommendationTitle,
      description: currentDwi.description,
      matchingAdvice,
      penalties: currentDwi.penalties,
      condition: currentDwi.condition,
    },
    hourlyScores,
    dailyScores,
  };
}

// ── Chemical fetching (in-memory cache, 5min TTL) ───────────────────────────

const chemicalsCache = new Map<string, { data: ChemicalRow[]; fetchedAt: number }>();
const CHEMICALS_TTL_MS = 5 * 60 * 1000; // 5 minutes
const CHEMICALS_CACHE_MAX = Number(Deno.env.get('CHEMICALS_CACHE_MAX') ?? '100');

async function fetchUserChemicals(userId: string | null): Promise<ChemicalRow[]> {
  if (!userId) return []; // 게스트: 개인 약품 추천 제외

  // 인메모리 캐시 확인
  const cached = chemicalsCache.get(userId);
  if (cached && Date.now() - cached.fetchedAt < CHEMICALS_TTL_MS) {
    return cached.data;
  }

  const { data, error } = await supabase
    .from('chemicals')
    .select('id, name, category, current_amount, total_capacity, open_date, purchase_date, expiry_months')
    .eq('user_id', userId);

  if (error) {
    console.error('Failed to fetch chemicals:', error.message);
    return cached?.data ?? [];
  }

  const rows = (data ?? []) as ChemicalRow[];
  chemicalsCache.set(userId, { data: rows, fetchedAt: Date.now() });

  // LRU: 최대 사용자 수 초과 시 가장 오래된 항목 삭제
  if (chemicalsCache.size > CHEMICALS_CACHE_MAX) {
    let oldestKey: string | undefined;
    let oldestTime = Infinity;
    for (const [key, entry] of chemicalsCache) {
      if (entry.fetchedAt < oldestTime) {
        oldestTime = entry.fetchedAt;
        oldestKey = key;
      }
    }
    if (oldestKey) chemicalsCache.delete(oldestKey);
  }

  return rows;
}

// ── Catalog fetching (in-memory cache) ───────────────────────────────────────

async function getCatalogCached(): Promise<CatalogRow[]> {
  if (catalogCache && Date.now() - catalogCache.fetchedAt < CATALOG_TTL_MS) {
    return catalogCache.data;
  }
  const { data, error } = await supabase
    .from('chemical_catalog')
    .select('id, name, brand, category, description')
    .order('category')
    .limit(100);

  const rows = (error ? [] : data ?? []) as CatalogRow[];
  if (error) {
    console.error('Failed to fetch chemical catalog:', error.message);
  }
  catalogCache = { data: rows, fetchedAt: Date.now() };
  return rows;
}

// ── Cache read/write ─────────────────────────────────────────────────────────

async function getCache(
  nx: number, ny: number, forecastType: string, ttlMinutes: number,
): Promise<CachedWeatherData | null> {
  const { data, error } = await supabase
    .from('weather_cache')
    .select('data_json, fetched_at')
    .eq('grid_nx', nx)
    .eq('grid_ny', ny)
    .eq('forecast_type', forecastType)
    .single();

  if (error || !data) return null;

  const fetchedAt = new Date(data.fetched_at).getTime();
  const age = Date.now() - fetchedAt;
  if (age > ttlMinutes * 60 * 1000) return null;

  return data.data_json as CachedWeatherData;
}

async function setCache(
  nx: number, ny: number, forecastType: string, payload: CachedWeatherData,
): Promise<void> {
  const { error } = await supabase
    .from('weather_cache')
    .upsert({
      grid_nx: nx,
      grid_ny: ny,
      forecast_type: forecastType,
      data_json: payload,
      fetched_at: new Date().toISOString(),
    }, {
      onConflict: 'grid_nx,grid_ny,forecast_type',
    });

  if (error) {
    console.error('Cache write failed:', error.message);
  }
}

