// [담당업무 4] React 관리 콘솔 — 실행 이력 화면 (발췌)
// 실행 이력 · 실행 예정 · 수동 실행 · DB 헬스를 한 화면의 탭으로 묶어 현업 상시 도구로 정착시켰다.

import { useEffect, useState } from "react";
import { consoleApi, ExecutionRow, HistoryFilter } from "./api";

const STATE_COLOR: Record<string, string> = {
  Succeeded: "green", Validated: "teal", Skipped: "gray",
  Failed: "red", TimedOut: "orange", Running: "blue",
};

export function ExecutionHistoryPage() {
  const [filter, setFilter] = useState<HistoryFilter>({ hours: 24 });
  const [rows, setRows] = useState<ExecutionRow[]>([]);
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    setLoading(true);
    consoleApi.history(filter).then(setRows).finally(() => setLoading(false));
    const timer = setInterval(() => consoleApi.history(filter).then(setRows), 10_000); // 10초 자동 갱신
    return () => clearInterval(timer);
  }, [filter]);

  const rerun = async (r: ExecutionRow) => {
    if (!confirm(`${r.domainCode}/${r.brandCode} 수동 실행할까요?`)) return;
    await consoleApi.run(r.domainCode, r.brandCode);
  };

  return (
    <section>
      <header className="toolbar">
        <input placeholder="도메인" onChange={e => setFilter({ ...filter, domainCode: e.target.value || undefined })} />
        <input placeholder="브랜드" onChange={e => setFilter({ ...filter, brandCode: e.target.value || undefined })} />
        <select value={filter.hours} onChange={e => setFilter({ ...filter, hours: Number(e.target.value) })}>
          <option value={1}>1시간</option><option value={24}>24시간</option><option value={168}>7일</option>
        </select>
        {loading && <span className="spinner" />}
      </header>

      <table className="grid">
        <thead>
          <tr><th>발사</th><th>도메인</th><th>브랜드</th><th>상태</th><th>소요</th><th>처리</th><th>메시지</th><th /></tr>
        </thead>
        <tbody>
          {rows.map(r => (
            <tr key={r.executionId}>
              <td>{new Date(r.firedAt).toLocaleString("ko-KR")}</td>
              <td>{r.domainCode}</td>
              <td>{r.brandCode}</td>
              <td><span className={`badge ${STATE_COLOR[r.state]}`}>{r.state}</span></td>
              <td>{(r.elapsedMs / 1000).toFixed(1)}s</td>
              <td>{r.processedCount}</td>
              <td className="msg" title={r.message ?? ""}>{r.message}</td>
              <td><button onClick={() => rerun(r)}>재실행</button></td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
