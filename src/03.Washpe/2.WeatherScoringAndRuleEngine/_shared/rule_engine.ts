// [담당업무 2] DB 기반 규칙 엔진 — 하드코딩 분기 27개를 advice_rules 로 이관, JSONB 조건(variable__operator) 평가 + 4계층 우선순위.

/**
 * DB-driven Rule Engine for chemical advice.
 *
 * advice_rules 테이블에서 로드한 규칙을 평가하여 맞춤 추천 문자열 배열을 생성한다.
 * 기존 chemical_advice.ts의 하드코딩 로직을 대체.
 */

import type { WeatherCondition } from "./weather_scoring.ts";
import type { ChemicalRow, CatalogRow } from "./chemical_advice.ts";
import type { SupabaseClient } from "https://esm.sh/@supabase/supabase-js@2";

// ─── Types ───────────────────────────────────────────────────────────────────

export interface AdviceRule {
  id: string;
  layer: "warning" | "positive" | "catalog" | "fallback";
  sort_order: number;
  condition: Record<string, unknown>;
  target_categories: string[] | null;
  ownership_mode: "owned" | "unowned" | "any";
  message_template: string;
  emoji: string;
  is_layer_fallback: boolean;
}

/** 조건 평가에 사용되는 플랫 컨텍스트. */
interface EvalContext {
  humidity: number;
  temperature: number;
  dust: string;
  wind_speed: number;
  visibility: number;
  dwi_score: number;
  available_count: number;
}

// ─── Cache ───────────────────────────────────────────────────────────────────

let rulesCache: { data: AdviceRule[]; fetchedAt: number } | null = null;
const RULES_TTL_MS = 60 * 60 * 1000; // 1 hour

export async function getRulesCached(
  supabase: SupabaseClient,
): Promise<AdviceRule[]> {
  const now = Date.now();
  if (rulesCache && now - rulesCache.fetchedAt < RULES_TTL_MS) {
    return rulesCache.data;
  }

  const { data, error } = await supabase
    .from("advice_rules")
    .select(
      "id, layer, sort_order, condition, target_categories, ownership_mode, message_template, emoji, is_layer_fallback",
    )
    .eq("is_active", true)
    .order("sort_order");

  if (error) {
    // DB 실패 시 stale 캐시 반환, 없으면 빈 배열
    console.error("[rule_engine] Failed to fetch rules:", error.message);
    return rulesCache?.data ?? [];
  }

  const rows = (data ?? []) as AdviceRule[];
  rulesCache = { data: rows, fetchedAt: now };
  return rows;
}

// ─── Condition Evaluator ─────────────────────────────────────────────────────

function parseKey(key: string): [string, string] {
  const operators = ["__gte", "__gt", "__lte", "__lt", "__eq", "__neq", "__in"];
  for (const op of operators) {
    if (key.endsWith(op)) {
      return [key.slice(0, -op.length), op.slice(2)];
    }
  }
  return [key, "eq"];
}

function matchesCondition(
  condition: Record<string, unknown>,
  context: EvalContext,
): boolean {
  for (const [key, expected] of Object.entries(condition)) {
    const [variable, operator] = parseKey(key);
    const actual = (context as Record<string, unknown>)[variable];
    if (actual === undefined) return false;

    switch (operator) {
      case "gt":
        if (!((actual as number) > (expected as number))) return false;
        break;
      case "gte":
        if (!((actual as number) >= (expected as number))) return false;
        break;
      case "lt":
        if (!((actual as number) < (expected as number))) return false;
        break;
      case "lte":
        if (!((actual as number) <= (expected as number))) return false;
        break;
      case "eq":
        if (actual !== expected) return false;
        break;
      case "neq":
        if (actual === expected) return false;
        break;
      case "in":
        if (!Array.isArray(expected) || !expected.includes(actual)) return false;
        break;
      default:
        return false;
    }
  }
  return true;
}

// ─── Template Resolver ───────────────────────────────────────────────────────

function visibilityLabel(meters: number): string {
  if (meters >= 1000) return `${(meters / 1000).toFixed(1)}km`;
  return `${meters}m`;
}

function resolveTemplate(
  template: string,
  context: EvalContext,
  productInfo: { name?: string; brand?: string },
): string {
  return template
    .replace(/\{product_name\}/g, productInfo.name ?? "")
    .replace(/\{product_brand\}/g, productInfo.brand ?? "")
    .replace(/\{humidity\}/g, String(context.humidity))
    .replace(/\{temperature\}/g, String(context.temperature))
    .replace(/\{wind_speed\}/g, context.wind_speed.toFixed(1))
    .replace(/\{visibility\}/g, visibilityLabel(context.visibility))
    .replace(/\{dwi_score\}/g, String(context.dwi_score));
}

// ─── Helpers (chemical availability) ─────────────────────────────────────────

function isExpired(ch: ChemicalRow): boolean {
  if (ch.expiry_months == null) return false;
  const baseDate = ch.open_date ?? ch.purchase_date;
  const expiry = new Date(baseDate);
  expiry.setMonth(expiry.getMonth() + ch.expiry_months);
  return new Date() > expiry;
}

function isEmpty(ch: ChemicalRow): boolean {
  return ch.current_amount <= 0;
}

function isAvailable(ch: ChemicalRow): boolean {
  return !isEmpty(ch) && !isExpired(ch);
}

function findOwned(
  available: ChemicalRow[],
  categories: string[],
): ChemicalRow | null {
  for (const ch of available) {
    if (categories.includes(ch.category)) return ch;
  }
  return null;
}

function findCatalogProduct(
  catalog: CatalogRow[],
  categories: string[],
): CatalogRow | null {
  for (const cat of categories) {
    const product = catalog.find((p) => p.category === cat);
    if (product) return product;
  }
  return null;
}

// ─── Layer Evaluator ─────────────────────────────────────────────────────────

function evaluateLayer(
  rules: AdviceRule[],
  context: EvalContext,
  available: ChemicalRow[],
  ownedCategories: Set<string>,
  catalog: CatalogRow[],
): string[] {
  const results: string[] = [];
  const usedCategories = new Set<string>();

  for (const rule of rules) {
    // is_layer_fallback: 같은 레이어에서 다른 규칙이 이미 발동했으면 스킵
    if (rule.is_layer_fallback && results.length > 0) continue;

    if (!matchesCondition(rule.condition, context)) continue;

    let message: string | null = null;

    if (rule.ownership_mode === "owned") {
      if (!rule.target_categories) continue;
      const matched = findOwned(available, rule.target_categories);
      if (!matched) continue;
      message = resolveTemplate(rule.message_template, context, {
        name: matched.name,
      });
    } else if (rule.ownership_mode === "unowned") {
      if (!rule.target_categories) continue;

      if (rule.layer === "catalog") {
        // catalog 레이어: 미보유 + 중복 카테고리 방지
        const unownedCats = rule.target_categories.filter(
          (c) => !ownedCategories.has(c) && !usedCategories.has(c),
        );
        if (unownedCats.length === 0) continue;
        const product = findCatalogProduct(catalog, unownedCats);
        if (!product) continue;
        message = resolveTemplate(rule.message_template, context, {
          name: product.name,
          brand: product.brand,
        });
        usedCategories.add(product.category);
      } else {
        // warning 레이어의 unowned: 해당 카테고리를 보유하지 않았을 때 일반 안내
        const hasAny = rule.target_categories.some((c) =>
          ownedCategories.has(c),
        );
        if (hasAny) continue;
        message = resolveTemplate(rule.message_template, context, {});
      }
    } else {
      // ownership_mode === 'any'
      message = resolveTemplate(rule.message_template, context, {});
    }

    if (message) {
      const prefix = rule.emoji ? `${rule.emoji} ` : "";
      results.push(`${prefix}${message}`);
    }
  }

  return results;
}

// ─── Main Entry ──────────────────────────────────────────────────────────────

function buildEvalContext(
  c: WeatherCondition,
  dwiScore: number,
  availableCount: number,
): EvalContext {
  return {
    humidity: c.humidityPercent,
    temperature: c.temperatureCelsius,
    dust: c.dustLevel,
    wind_speed: c.windSpeed,
    visibility: c.visibility,
    dwi_score: dwiScore,
    available_count: availableCount,
  };
}

/**
 * DB에서 로드한 규칙을 평가하여 맞춤 advice 문자열 배열을 반환.
 * 결과는 항상 1개 이상 보장.
 */
export function evaluateAllRules(
  rules: AdviceRule[],
  c: WeatherCondition,
  chemicals: ChemicalRow[],
  catalog: CatalogRow[],
  dwiScore: number,
): string[] {
  const available = chemicals.filter(isAvailable);
  const ownedCategories = new Set(available.map((ch) => ch.category));
  const context = buildEvalContext(c, dwiScore, available.length);

  // 레이어별 규칙 그룹핑 (sort_order 정렬은 DB에서 완료)
  const layerOrder: AdviceRule["layer"][] = [
    "warning",
    "positive",
    "catalog",
    "fallback",
  ];
  const grouped = new Map<string, AdviceRule[]>();
  for (const layer of layerOrder) {
    grouped.set(layer, []);
  }
  for (const rule of rules) {
    grouped.get(rule.layer)?.push(rule);
  }

  const warningResults = evaluateLayer(
    grouped.get("warning")!,
    context,
    available,
    ownedCategories,
    catalog,
  );
  const positiveResults = evaluateLayer(
    grouped.get("positive")!,
    context,
    available,
    ownedCategories,
    catalog,
  );
  const catalogResults = evaluateLayer(
    grouped.get("catalog")!,
    context,
    available,
    ownedCategories,
    catalog,
  ).slice(0, 2);

  const result = [...warningResults, ...positiveResults, ...catalogResults];

  // Fallback — 다른 레이어 결과가 없을 때
  if (result.length === 0) {
    const fallbackResults = evaluateLayer(
      grouped.get("fallback")!,
      context,
      available,
      ownedCategories,
      catalog,
    );
    if (fallbackResults.length > 0) {
      result.push(fallbackResults[0]);
    }
  }

  // 하드코딩 안전망 — DB가 비어있어도 최소 1개 보장
  if (result.length === 0) {
    result.push(
      dwiScore >= 60
        ? "✨ 오늘은 세차하기 좋은 날씨입니다!"
        : "ℹ️ 날씨 상황을 확인하고 세차 계획을 세우세요.",
    );
  }

  return result;
}
