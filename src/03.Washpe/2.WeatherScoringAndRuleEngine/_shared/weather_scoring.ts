// [담당업무 2] 스코어링 엔진 — Kill-switch 5종 + 연속 보간 감점 7종, 시간별·일별 점수 집계.

/**
 * DWI (Detailing Weather Index) 스코어링 엔진.
 *
 * Dart `GetWashIndexUseCase` + `ForecastScoreCubit` 로직을 1:1 포팅.
 *
 * Base score: 100
 * Kill switches → score = 0
 * 7 graduated penalties (continuous interpolation)
 */

import type { HourlyWeatherDto, DailyForecastDto } from "./kma_api.ts";

// ─── Types ───────────────────────────────────────────────────────────────────

export type DustLevel = "good" | "moderate" | "bad" | "veryBad" | "hazardous";
export type WashStatus = "perfect" | "good" | "fair" | "poor" | "avoid";

export interface WeatherCondition {
  temperatureCelsius: number;
  precipitationChance: number;
  humidityPercent: number;
  dustLevel: DustLevel;
  weatherCode: number;
  windSpeed: number;
  windGust: number;
  rainVolume: number;
  snowVolume: number;
  visibility: number;
  isNight: boolean;
  /** Unix epoch seconds (UTC) */
  recordedAt: number;
}

export interface ScorePenalty {
  factor: string; // "precipitation" | "temperature" | "humidity" | "dustLevel" | "windSpeed" | "rainVolume" | "visibility"
  valueLabel: string;
  deduction: number;
  isKillSwitch: boolean;
  isTriggered: boolean;
}

export interface DwiResult {
  score: number;
  status: WashStatus;
  recommendationTitle: string;
  description: string;
  penalties: ScorePenalty[];
  condition: WeatherCondition;
}

export interface HourlyScoreDto {
  dateTime: number; // epoch seconds
  score: number;
  status: WashStatus;
  skyStatus: number;  // 1=맑음, 3=구름많음, 4=흐림
  precipType: number; // 0=없음, 1=비, 2=비/눈, 3=눈, 4=소나기
  temperature: number;
  penalties: ScorePenalty[];      // 7개 감점 요인 상세
  condition: WeatherCondition;    // 해당 시간 날씨 조건 전체
}

export interface DailyScoreDto {
  date: string; // "YYYY-MM-DD"
  score: number;
  status: WashStatus;
  tempMin: number;
  tempMax: number;
  precipChance: number;
  skyStatus: number;
}

// ─── WashStatus ──────────────────────────────────────────────────────────────

export function statusFromScore(score: number): WashStatus {
  if (score >= 80) return "perfect";
  if (score >= 60) return "good";
  if (score >= 40) return "fair";
  if (score >= 20) return "poor";
  return "avoid";
}

// ─── Dust helpers ────────────────────────────────────────────────────────────

function isBadOrWorse(d: DustLevel): boolean {
  return d === "bad" || d === "veryBad" || d === "hazardous";
}

function dustDisplayName(d: DustLevel): string {
  switch (d) {
    case "good":      return "Good";
    case "moderate":  return "Moderate";
    case "bad":       return "Bad";
    case "veryBad":   return "Very Bad";
    case "hazardous": return "Hazardous";
  }
}

// ─── Linear interpolation ────────────────────────────────────────────────────

function lerp(
  value: number, fromLow: number, fromHigh: number,
  toLow: number, toHigh: number,
): number {
  const t = Math.max(0, Math.min(1, (value - fromLow) / (fromHigh - fromLow)));
  return Math.round(toLow + t * (toHigh - toLow));
}

// ─── Graduated penalty functions ─────────────────────────────────────────────

function precipitationPenalty(percent: number): number {
  if (percent <= 15) return 0;
  if (percent <= 30) return lerp(percent, 15, 30, 0, 10);
  if (percent <= 45) return lerp(percent, 30, 45, 10, 25);
  return lerp(percent, 45, 60, 25, 35);
}

function temperaturePenalty(temp: number): number {
  if (temp >= 10 && temp <= 25) return 0;
  // Cold side
  if (temp < 10) {
    if (temp >= 5) return lerp(temp, 10, 5, 0, 5);
    if (temp >= 0) return lerp(temp, 5, 0, 5, 15);
    return lerp(temp, 0, -3, 15, 25);
  }
  // Hot side
  if (temp <= 30) return lerp(temp, 25, 30, 0, 8);
  if (temp <= 35) return lerp(temp, 30, 35, 8, 18);
  return lerp(temp, 35, 40, 18, 25);
}

function humidityPenalty(percent: number): number {
  if (percent <= 50) return 0;
  if (percent <= 65) return lerp(percent, 50, 65, 0, 5);
  if (percent <= 80) return lerp(percent, 65, 80, 5, 12);
  if (percent <= 90) return lerp(percent, 80, 90, 12, 18);
  return lerp(percent, 90, 100, 18, 20);
}

function dustPenalty(d: DustLevel): number {
  switch (d) {
    case "good":      return 0;
    case "moderate":  return 8;
    case "bad":       return 18;
    case "veryBad":   return 25;
    case "hazardous": return 30;
  }
}

function windPenalty(speed: number, gust: number): number {
  let base: number;
  if (speed <= 3.0) {
    base = 0;
  } else if (speed <= 6.0) {
    base = lerp(speed, 3, 6, 0, 5);
  } else if (speed <= 10.0) {
    base = lerp(speed, 6, 10, 5, 12);
  } else if (speed <= 15.0) {
    base = lerp(speed, 10, 15, 12, 18);
  } else {
    base = 20;
  }
  // Gust bonus: if gust is 1.5x wind and above 8 m/s
  if (gust > speed * 1.5 && gust > 8.0) {
    base = Math.min(base + 3, 20);
  }
  return base;
}

function rainVolumePenalty(mm: number): number {
  if (mm <= 0) return 0;
  if (mm <= 0.5) return lerp(mm, 0, 0.5, 0, 5);
  if (mm <= 2.0) return lerp(mm, 0.5, 2, 5, 15);
  return lerp(mm, 2, 5, 15, 25);
}

function visibilityPenalty(meters: number): number {
  if (meters >= 8000) return 0;
  if (meters >= 5000) return lerp(meters, 8000, 5000, 0, 5);
  if (meters >= 2000) return lerp(meters, 5000, 2000, 5, 10);
  if (meters >= 1000) return lerp(meters, 2000, 1000, 10, 15);
  return 15;
}

// ─── Visibility label ────────────────────────────────────────────────────────

function visibilityLabel(meters: number): string {
  if (meters >= 1000) return `${(meters / 1000).toFixed(1)}km`;
  return `${meters}m`;
}

// ─── Kill reason & description helpers ───────────────────────────────────────

function killReason(
  c: WeatherCondition,
  kills: {
    rainChanceKill: boolean;
    freezeKill: boolean;
    heavyRainKill: boolean;
    heavySnowKill: boolean;
    thunderstormKill: boolean;
  },
): string | null {
  if (kills.thunderstormKill) {
    return "뇌우 경보 — 안전을 위해 야외 작업을 삼가세요.";
  }
  if (kills.rainChanceKill) {
    return `강수 확률 ${c.precipitationChance}% — 지금 세차해도 곧 비에 씻겨 나갑니다.`;
  }
  if (kills.heavyRainKill) {
    return `강수량 ${c.rainVolume.toFixed(1)}mm — 비가 내리는 중 세차는 의미 없습니다.`;
  }
  if (kills.heavySnowKill) {
    return `적설량 ${c.snowVolume.toFixed(1)}mm — 눈이 오는 중 세차는 불가합니다.`;
  }
  if (kills.freezeKill) {
    return `기온 ${c.temperatureCelsius.toFixed(1)}°C — 영하에서는 물이 차체에 얼 수 있어 세차가 위험합니다.`;
  }
  return null;
}

function buildTitle(status: WashStatus, kill: string | null): string {
  if (kill !== null) return "🚫 오늘은 세차를 피하세요";
  switch (status) {
    case "perfect": return "✨ 세차하기 완벽한 날!";
    case "good":    return "👍 세차하기 좋은 날";
    case "fair":    return "⚠️ 보통 — 주의하여 진행";
    case "poor":    return "🌧 세차 조건이 좋지 않음";
    case "avoid":   return "🚫 세차를 피하는 것이 좋습니다";
  }
}

function buildDescription(c: WeatherCondition): string {
  const parts: string[] = [];

  if (c.precipitationChance > 30) {
    parts.push(`강수 확률 ${c.precipitationChance}%로 세차 후 비를 맞을 가능성이 있습니다`);
  }
  if (c.temperatureCelsius > 28) {
    parts.push(
      `기온 ${c.temperatureCelsius.toFixed(1)}°C — 고온으로 워터스팟 발생 위험이 높습니다`,
    );
  }
  if (c.temperatureCelsius < 5 && c.temperatureCelsius >= 0) {
    parts.push(
      `기온 ${c.temperatureCelsius.toFixed(1)}°C — 저온으로 세제 성능이 저하될 수 있습니다`,
    );
  }
  if (c.humidityPercent > 70) {
    parts.push(`습도 ${c.humidityPercent}%로 왁스·실런트 작업이 어렵습니다`);
  }
  if (isBadOrWorse(c.dustLevel)) {
    parts.push(
      `미세먼지 ${dustDisplayName(c.dustLevel)} — 먼지 입자로 인한 스크래치 위험이 있습니다`,
    );
  }
  if (c.windSpeed > 6.0) {
    parts.push(
      `바람 ${c.windSpeed.toFixed(1)}m/s — 스프레이 작업에 어려움이 있습니다`,
    );
  }
  if (c.rainVolume > 0 && c.rainVolume < 5) {
    parts.push(
      `시간당 강수량 ${c.rainVolume.toFixed(1)}mm — 가벼운 비가 내리고 있습니다`,
    );
  }
  if (c.visibility < 5000) {
    parts.push(
      `가시거리 ${visibilityLabel(c.visibility)} — 안개 또는 스모그 주의`,
    );
  }

  if (parts.length === 0) {
    return "세차와 디테일링에 이상적인 조건입니다. 오늘 멋지게 광을 내보세요!";
  }
  return `${parts.join(". ")}.`;
}

// ─── Main DWI calculation ────────────────────────────────────────────────────

export function calculateDwi(c: WeatherCondition): DwiResult {
  // Kill Switches
  const rainChanceKill = c.precipitationChance > 60;
  const freezeKill = c.temperatureCelsius < 0;
  const heavyRainKill = c.rainVolume > 0;
  const heavySnowKill = c.snowVolume > 0;
  const thunderstormKill =
    c.weatherCode === 95 || c.weatherCode === 96 || c.weatherCode === 99;

  const anyKill =
    rainChanceKill || freezeKill || heavyRainKill || heavySnowKill || thunderstormKill;

  // Graduated Penalties
  let precipDed = 0, tempDed = 0, humidDed = 0, dustDed = 0;
  let windDed = 0, rainVolDed = 0, visDed = 0;

  if (!anyKill) {
    precipDed = precipitationPenalty(c.precipitationChance);
    tempDed = temperaturePenalty(c.temperatureCelsius);
    humidDed = humidityPenalty(c.humidityPercent);
    dustDed = dustPenalty(c.dustLevel);
    windDed = windPenalty(c.windSpeed, c.windGust);
    rainVolDed = rainVolumePenalty(c.rainVolume);
    visDed = visibilityPenalty(c.visibility);
  }

  const totalDeduction =
    precipDed + tempDed + humidDed + dustDed + windDed + rainVolDed + visDed;
  const score = anyKill ? 0 : Math.max(0, Math.min(100, 100 - totalDeduction));

  // Penalty breakdown (always 7 entries)
  const penalties: ScorePenalty[] = [
    {
      factor: "precipitation",
      valueLabel: `${c.precipitationChance}%`,
      deduction: rainChanceKill ? -100 : -precipDed,
      isKillSwitch: rainChanceKill,
      isTriggered: rainChanceKill || precipDed > 0,
    },
    {
      factor: "temperature",
      valueLabel: `${c.temperatureCelsius.toFixed(1)}°C`,
      deduction: freezeKill ? -100 : -tempDed,
      isKillSwitch: freezeKill,
      isTriggered: freezeKill || tempDed > 0,
    },
    {
      factor: "humidity",
      valueLabel: `${c.humidityPercent}%`,
      deduction: -humidDed,
      isKillSwitch: false,
      isTriggered: humidDed > 0,
    },
    {
      factor: "dustLevel",
      valueLabel: dustDisplayName(c.dustLevel),
      deduction: -dustDed,
      isKillSwitch: false,
      isTriggered: dustDed > 0,
    },
    {
      factor: "windSpeed",
      valueLabel: `${c.windSpeed.toFixed(1)}m/s`,
      deduction: -windDed,
      isKillSwitch: false,
      isTriggered: windDed > 0,
    },
    {
      factor: "rainVolume",
      valueLabel: `${c.rainVolume.toFixed(1)}mm`,
      deduction: heavyRainKill ? -100 : -rainVolDed,
      isKillSwitch: heavyRainKill,
      isTriggered: heavyRainKill || rainVolDed > 0,
    },
    {
      factor: "visibility",
      valueLabel: visibilityLabel(c.visibility),
      deduction: -visDed,
      isKillSwitch: false,
      isTriggered: visDed > 0,
    },
  ];

  const status = statusFromScore(score);
  const kill = killReason(c, {
    rainChanceKill, freezeKill, heavyRainKill, heavySnowKill, thunderstormKill,
  });

  return {
    score,
    status,
    recommendationTitle: buildTitle(status, kill),
    description: kill ?? buildDescription(c),
    penalties,
    condition: c,
  };
}

// ─── Hourly scores ───────────────────────────────────────────────────────────

/** WMO weather code → 하늘상태 (1=맑음, 3=구름많음, 4=흐림). */
export function wmoToSkyStatus(code: number): number {
  if (code <= 1) return 1;  // Clear
  if (code <= 3) return 3;  // Partly cloudy → 구름많음
  if (code <= 48) return 4; // Fog → 흐림
  return 4;                 // Rain/snow/thunderstorm → 흐림
}

/** WMO weather code → 강수형태 (0=없음, 1=비, 2=비/눈, 3=눈, 4=소나기). */
export function wmoPrecipType(
  code: number, rain: number, snow: number,
): number {
  if (code >= 95) return 1; // Thunderstorm → 비
  if (code >= 80) return 4; // Showers → 소나기
  if (code >= 71 && code <= 77) return 3; // Snow
  if (code >= 66 && code <= 67) return 2; // Freezing rain → 비/눈
  if (code >= 51 && code <= 65) return 1; // Rain/drizzle
  // Fallback: check volumes
  if (rain > 0 && snow > 0) return 2;
  if (snow > 0) return 3;
  if (rain > 0) return 1;
  return 0;
}

/** PM2.5 µg/m³ → DustLevel 변환. Open-Meteo AQI 기준. */
export function pm25ToDustLevel(pm25: number): DustLevel {
  if (pm25 <= 12) return "good";
  if (pm25 <= 35.4) return "moderate";
  if (pm25 <= 55.4) return "bad";
  if (pm25 <= 150.4) return "veryBad";
  return "hazardous";
}

/** HourlyWeatherDto → WeatherCondition 변환. */
export function hourlyToCondition(h: HourlyWeatherDto): WeatherCondition {
  return {
    temperatureCelsius: h.temperatureCelsius,
    precipitationChance: h.precipitationPercent,
    humidityPercent: h.humidityPercent,
    dustLevel: pm25ToDustLevel(h.pm25 ?? 0),
    weatherCode: h.weatherCode ?? 0,
    windSpeed: h.windSpeed,
    windGust: h.windGust ?? 0,
    rainVolume: h.rainVolume ?? 0,
    snowVolume: h.snowVolume ?? 0,
    visibility: h.visibility ?? 10000,
    isNight: !(h.isDay ?? true),
    recordedAt: h.unixTimestamp,
  };
}

/** HourlyWeatherDto → HourlyScoreDto 변환 (DWI 점수 포함). */
export function calculateHourlyScores(
  hourlyData: HourlyWeatherDto[],
): HourlyScoreDto[] {
  return hourlyData.map((h) => {
    const condition = hourlyToCondition(h);
    const dwi = calculateDwi(condition);
    const skyStatus = condition.weatherCode === 0
      ? 1
      : wmoToSkyStatus(condition.weatherCode);
    const precipType = wmoPrecipType(
      condition.weatherCode, condition.rainVolume, condition.snowVolume,
    );

    return {
      dateTime: h.unixTimestamp,
      score: dwi.score,
      status: dwi.status,
      skyStatus,
      precipType,
      temperature: h.temperatureCelsius,
      penalties: dwi.penalties,
      condition,
    };
  });
}

// ─── Daily scores ────────────────────────────────────────────────────────────

/** 중기예보 DailyForecastDto → 간소화 DWI 점수 (기온 + 강수확률 + 하늘상태). */
export function calculateDailyScoreFromForecast(
  f: DailyForecastDto,
): DailyScoreDto {
  let score = 100;
  const precipChance = Math.max(
    f.precipitationChanceAm,
    f.precipitationChancePm,
  );
  const avgTemp = (f.tempMin + f.tempMax) / 2;

  // Kill switch: 강수확률 > 60%
  if (precipChance > 60) {
    return makeDailyScore(f, 0, precipChance);
  }
  // Kill switch: 영하
  if (f.tempMax < 0) {
    return makeDailyScore(f, 0, precipChance);
  }

  // 강수확률 감점
  if (precipChance > 15) {
    score -= Math.round((precipChance - 15) * 35 / 45);
  }

  // 기온 감점 (U-shaped)
  if (avgTemp < 10) {
    score -= Math.max(0, Math.min(25, Math.round((10 - avgTemp) * 25 / 13)));
  } else if (avgTemp > 25) {
    score -= Math.max(0, Math.min(25, Math.round((avgTemp - 25) * 25 / 15)));
  }

  // 하늘상태 감점
  const worstSky = Math.max(f.skyStatusAm, f.skyStatusPm);
  if (worstSky === 4) {
    score -= 5;
  } else if (worstSky === 3) {
    score -= 2;
  }

  score = Math.max(0, Math.min(100, score));
  return makeDailyScore(f, score, precipChance);
}

function makeDailyScore(
  f: DailyForecastDto, score: number, precipChance: number,
): DailyScoreDto {
  return {
    date: f.date,
    score,
    status: statusFromScore(score),
    tempMin: f.tempMin,
    tempMax: f.tempMax,
    precipChance,
    skyStatus: Math.max(f.skyStatusAm, f.skyStatusPm),
  };
}

/** 시간별 데이터를 날짜별로 집계하여 DailyScoreDto 리스트 생성 (단기예보 → 일별). */
/**
 * UTC epoch(초) + offset(ms) → 클라이언트 로컬 날짜 "YYYY-MM-DD".
 * DST 영향 없이 순수 offset 계산.
 */
function localDateKey(epochSec: number, offsetMs: number): string {
  const d = new Date(epochSec * 1000 + offsetMs);
  const y = d.getUTCFullYear();
  const m = String(d.getUTCMonth() + 1).padStart(2, '0');
  const day = String(d.getUTCDate()).padStart(2, '0');
  return `${y}-${m}-${day}`;
}

export function aggregateHourlyToDaily(
  hourlyScores: HourlyScoreDto[],
  hourlyData: HourlyWeatherDto[],
  offsetMs: number,
): DailyScoreDto[] {
  const grouped = new Map<
    string,
    { scores: HourlyScoreDto[]; temps: number[]; precips: number[]; skies: number[] }
  >();

  for (let i = 0; i < hourlyScores.length; i++) {
    const hs = hourlyScores[i];
    const hd = hourlyData[i];
    const dateKey = localDateKey(hs.dateTime, offsetMs);

    if (!grouped.has(dateKey)) {
      grouped.set(dateKey, { scores: [], temps: [], precips: [], skies: [] });
    }
    const g = grouped.get(dateKey)!;
    g.scores.push(hs);
    g.temps.push(hd.temperatureCelsius);
    g.precips.push(hd.precipitationPercent);
    g.skies.push(hs.skyStatus);
  }

  const result: DailyScoreDto[] = [];
  for (const [dateKey, g] of grouped) {
    const avgScore = Math.round(
      g.scores.reduce((sum, s) => sum + s.score, 0) / g.scores.length,
    );
    const tempMin = Math.min(...g.temps);
    const tempMax = Math.max(...g.temps);
    const precipChance = Math.max(...g.precips);
    const skyStatus = Math.max(...g.skies);

    result.push({
      date: dateKey,
      score: avgScore,
      status: statusFromScore(avgScore),
      tempMin,
      tempMax,
      precipChance,
      skyStatus,
    });
  }

  result.sort((a, b) => a.date.localeCompare(b.date));
  return result;
}
