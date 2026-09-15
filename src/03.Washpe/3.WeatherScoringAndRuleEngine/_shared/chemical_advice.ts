// [담당업무 3] 규칙 엔진 진입점 (타입 + 위임).

/**
 * Chemical advice 생성 — DB-driven Rule Engine 위임.
 *
 * advice_rules 테이블의 규칙을 rule_engine.ts가 평가하여 맞춤 추천을 생성한다.
 * 이 파일은 타입 export + buildChemicalAdvice 진입점만 유지.
 */

import type { WeatherCondition } from "./weather_scoring.ts";
import { evaluateAllRules, type AdviceRule } from "./rule_engine.ts";

// ─── Types ───────────────────────────────────────────────────────────────────

/** chemicals 테이블 row (서버에서 조회). */
export interface ChemicalRow {
  id: string;
  name: string;
  category: string; // ChemicalCategory enum name
  current_amount: number;
  total_capacity: number;
  open_date: string | null;      // ISO date
  purchase_date: string;         // ISO date
  expiry_months: number | null;
}

/** chemical_catalog 테이블 row (서버에서 조회, 경량 subset). */
export interface CatalogRow {
  id: string;
  name: string;
  brand: string;
  category: string;
  description: string | null;
}

// ─── Main ────────────────────────────────────────────────────────────────────

/**
 * 날씨 조건 + 사용자 약품 목록 + 카탈로그 → 맞춤 advice 문자열 리스트.
 * DB에서 로드한 규칙을 rule engine이 평가. 결과는 항상 1개 이상 보장.
 */
export function buildChemicalAdvice(
  rules: AdviceRule[],
  c: WeatherCondition,
  chemicals: ChemicalRow[],
  catalog: CatalogRow[],
  dwiScore: number,
): string[] {
  return evaluateAllRules(rules, c, chemicals, catalog, dwiScore);
}
