// [담당업무 3] 위경도 → 기상청 5km 격자(LCC 투영) — 캐시 키이자 KMA API 입력.

/**
 * 위경도 → 기상청 격자좌표 (nx, ny) 변환.
 *
 * Lambert Conformal Conic (LCC) 투영 기반.
 * KMA 공식 C 코드를 TypeScript로 직접 포팅.
 *
 * 검증값: 서울 (37.5665, 126.978) → (60, 127)
 */

// LCC 투영 상수
const RE = 6371.00877;  // 지구 반경 (km)
const GRID = 5.0;       // 격자 간격 (km)
const SLAT1 = 30.0;     // 투영 위도 1 (°)
const SLAT2 = 60.0;     // 투영 위도 2 (°)
const OLON = 126.0;     // 기준점 경도 (°)
const OLAT = 38.0;      // 기준점 위도 (°)
const XO = 43.0;        // 기준점 X 좌표
const YO = 136.0;       // 기준점 Y 좌표

const DEG_RAD = Math.PI / 180.0;

export interface GridPoint {
  nx: number;
  ny: number;
}

export function toGrid(lat: number, lon: number): GridPoint {
  const re = RE / GRID;
  const slat1 = SLAT1 * DEG_RAD;
  const slat2 = SLAT2 * DEG_RAD;
  const olon = OLON * DEG_RAD;
  const olat = OLAT * DEG_RAD;

  const sn =
    Math.log(Math.cos(slat1) / Math.cos(slat2)) /
    Math.log(
      Math.tan(Math.PI * 0.25 + slat2 * 0.5) /
      Math.tan(Math.PI * 0.25 + slat1 * 0.5),
    );
  const sf =
    (Math.pow(Math.tan(Math.PI * 0.25 + slat1 * 0.5), sn) *
      Math.cos(slat1)) /
    sn;
  const ro =
    (re * sf) / Math.pow(Math.tan(Math.PI * 0.25 + olat * 0.5), sn);

  const ra =
    (re * sf) / Math.pow(Math.tan(Math.PI * 0.25 + lat * DEG_RAD * 0.5), sn);
  let theta = lon * DEG_RAD - olon;
  if (theta > Math.PI) theta -= 2.0 * Math.PI;
  if (theta < -Math.PI) theta += 2.0 * Math.PI;
  theta *= sn;

  const nx = Math.floor(ra * Math.sin(theta) + XO + 0.5);
  const ny = Math.floor(ro - ra * Math.cos(theta) + YO + 0.5);
  return { nx, ny };
}
