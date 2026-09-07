// 관리 콘솔 API 클라이언트 — JWT 를 Authorization 헤더로 전달

export interface HistoryFilter { domainCode?: string; brandCode?: string; hours: number; }
export interface ExecutionRow {
  executionId: string; domainCode: string; brandCode: string; state: string;
  firedAt: string; finishedAt?: string; elapsedMs: number; processedCount: number; message?: string;
}

const token = () => localStorage.getItem("scheduler.jwt") ?? "";
const headers = () => ({ Authorization: `Bearer ${token()}`, "Content-Type": "application/json" });

export const consoleApi = {
  async history(f: HistoryFilter): Promise<ExecutionRow[]> {
    const to = new Date(); const from = new Date(to.getTime() - f.hours * 3600_000);
    const qs = new URLSearchParams({ fromUtc: from.toISOString(), toUtc: to.toISOString() });
    if (f.domainCode) qs.set("domainCode", f.domainCode);
    if (f.brandCode) qs.set("brandCode", f.brandCode);
    const res = await fetch(`/api/console/history?${qs}`, { headers: headers() });
    return res.json();
  },
  upcoming: (hours = 24) => fetch(`/api/console/upcoming?hours=${hours}`, { headers: headers() }).then(r => r.json()),
  run: (domain: string, brand?: string) =>
    fetch(`/api/console/run/${domain}${brand ? `?brandCode=${brand}` : ""}`, { method: "POST", headers: headers() }),
  dbHealth: () => fetch(`/api/console/db-health`, { headers: headers() }).then(r => r.json()),
  control: {
    pause: (reason: string) => fetch(`/api/control/pause`, { method: "POST", headers: headers(), body: JSON.stringify({ reason }) }),
    block: (level: string, key: string, reason: string) =>
      fetch(`/api/control/block/${level}/${key}`, { method: "POST", headers: headers(), body: JSON.stringify({ reason }) }),
    validation: (level: string, key: string, on: boolean) =>
      fetch(`/api/control/validation/${level}/${key}?on=${on}`, { method: "PUT", headers: headers() }),
  },
};
