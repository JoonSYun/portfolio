import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  deleteAdminJson, getAdminJson, getAdminJsonOrNull, patchAdminJson, postAdminJson, putAdminJson,
} from '../../../common/api/client';
import { useTabActive } from '../../../common/layout/tabs/ActiveTabContext';
import type { PagedResult } from '../../../common/api/types';
import { authApi } from '../../auth/api/client';
import type {
  AddDomainsToBrandRequest, AddDomainsToGroupRequest,
  Brand, BrandAddMail, BrandAddMailReorderRequest, BrandAddMailUpsertRequest,
  BrandOverride, BrandOverrideUpsertRequest, BrandUpsertRequest,
  CodeItem, DispatchPauseRequest, DispatchPauseState,
  Domain, DomainGroup, DomainGroupUpsertRequest,
  DomainWithScheduleUpsertRequest,
  JobLog, JobLogDeprecateResult, JobLogQuery, JobTypeItem, ManualRunResult, ParamDtoItem, ParamDtoSchema,
  NextRunBrand, NextRunDomain,
  OverrideCron, OverrideCronUpsertRequest,
  Queue, QueueUpsertRequest,
  RunAsMode,
  RunStatusRow,
  Schedule,
  UpcomingRun,
} from './types';

// 실행 enqueue·강제 종료는 로그 목록, 실행 결과 현황, 대시보드 요약(실행 중·최근 실패·금일 건수)을 바꾼다.
const invalidateRunViews = (qc: ReturnType<typeof useQueryClient>) => {
  qc.invalidateQueries({ queryKey: ['job-logs'] });
  qc.invalidateQueries({ queryKey: ['run-status'] });
  qc.invalidateQueries({ queryKey: ['dashboard-summary'] });
};

// ───────────── Domains
export const useDomains = () =>
  // 목록·선택 상자·큐/그룹 편집 창·브랜드 목록이 같이 쓴다 — 쓰는 곳 모두 실패를 그 자리에 보인다.
  useQuery({ queryKey: ['domains'], queryFn: () => getAdminJson<Domain[]>('/api/admin/domains'), meta: { inlineError: true } });

export const useDomain = (code?: string) =>
  useQuery({
    queryKey: ['domain', code],
    meta:     { inlineError: true },  // 실패는 화면이 표·카드 자리에 LoadError 로 보인다
    queryFn:  () => getAdminJson<Domain>(`/api/admin/domains/${code}`),
    enabled:  !!code,
  });

// Brand 팝업 "Add domain" 후보 — 아직 해당 brand 의 override 매핑이 없는 도메인만.
export const useAddableDomainsForBrand = (brandCode?: string) =>
  useQuery({
    queryKey: ['domains', 'addable-for-brand', brandCode],
    meta:     { inlineError: true },  // 실패는 화면이 표·카드 자리에 LoadError 로 보인다
    queryFn:  () => getAdminJson<Domain[]>(`/api/admin/domains/addable-for-brand/${brandCode}`),
    enabled:  !!brandCode,
  });

// Create/update always write Domain + L1 Schedule together against the unified
// /with-schedule endpoint (single DB transaction on the server).
export const useCreateDomainWithSchedule = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (req: DomainWithScheduleUpsertRequest) =>
      postAdminJson('/api/admin/domains/with-schedule', req),
    onSuccess: (_r, vars) => {
      qc.invalidateQueries({ queryKey: ['domains'] });
      qc.invalidateQueries({ queryKey: ['schedule', vars.domain.domainCode] });
    },
  });
};

export const useUpdateDomainWithSchedule = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ code, req }: { code: string; req: DomainWithScheduleUpsertRequest }) =>
      putAdminJson(`/api/admin/domains/${code}/with-schedule`, req),
    onSuccess: (_r, vars) => {
      qc.invalidateQueries({ queryKey: ['domains'] });
      qc.invalidateQueries({ queryKey: ['domain', vars.code] });
      qc.invalidateQueries({ queryKey: ['schedule', vars.code] });
    },
  });
};

export const useDeleteDomain = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (code: string) => deleteAdminJson(`/api/admin/domains/${code}`),
    onSuccess:  () => qc.invalidateQueries({ queryKey: ['domains'] }),
  });
};

export const useToggleDomainUseYn = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ code, useYn }: { code: string; useYn: 'Y' | 'N' }) =>
      patchAdminJson(`/api/admin/domains/${code}/use-yn`, { useYn }),
    onSuccess: (_r, vars) => {
      qc.invalidateQueries({ queryKey: ['domains'] });
      qc.invalidateQueries({ queryKey: ['domain', vars.code] });
    },
  });
};

// 도메인 TEST_YN 토글. effectiveIsTest 계산에 도메인이 OR 한 축이라 overrides 캐시도 무효화.
export const useToggleDomainTestYn = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ code, testYn }: { code: string; testYn: 'Y' | 'N' }) =>
      patchAdminJson(`/api/admin/domains/${code}/test-yn`, { testYn }),
    onSuccess: (_r, vars) => {
      qc.invalidateQueries({ queryKey: ['domains'] });
      qc.invalidateQueries({ queryKey: ['domain', vars.code] });
      qc.invalidateQueries({ queryKey: ['overrides'] });
    },
  });
};

// 도메인 MAIL_SEND 토글. 브랜드 mailSend 와 AND 결합이라 overrides 캐시도 무효화(목록 표시 갱신).
export const useToggleDomainMailSend = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ code, mailSend }: { code: string; mailSend: 'Y' | 'N' }) =>
      patchAdminJson(`/api/admin/domains/${code}/mail-send`, { mailSend }),
    onSuccess: (_r, vars) => {
      qc.invalidateQueries({ queryKey: ['domains'] });
      qc.invalidateQueries({ queryKey: ['domain', vars.code] });
      qc.invalidateQueries({ queryKey: ['overrides'] });
    },
  });
};

// "Run domain now" — 도메인의 모든 브랜드 오버라이드를 한 번에 fan-out enqueue.
// 응답은 enqueue 된 각 (도메인,브랜드) 의 ManualRunResult 배열. 매핑 0개면 빈 배열.
// 활성 잡 보유 브랜드는 SkipReason 채워진 row 로 결과에 포함됨 (부분 성공).
// 매 호출마다 unique LogId 발급 → 새 SCH_JOB_LOG row 생성, 자동 cron 과 충돌 없음.
// runAs: 'test' | 'operational' — 사용자가 RunNowModal 에서 선택. 필수 body.
//   DB 측 effectiveIsTest=true 인 브랜드는 fan-out 정책상 runAs 무시하고 자동 test 처리.
export const useRunDomainNow = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ code, runAs }: { code: string; runAs: RunAsMode }) =>
      postAdminJson<ManualRunResult[]>(`/api/admin/runs/domain/${code}`, { runAs }),
    onSuccess: () => invalidateRunViews(qc),
  });
};

// ───────────── Brands
export const useBrands = () =>
  useQuery({ queryKey: ['brands'], queryFn: () => getAdminJson<Brand[]>('/api/admin/brands'), meta: { inlineError: true } });  // 목록·선택 상자가 그 자리에 보인다

export const useCreateBrand = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (req: BrandUpsertRequest) => postAdminJson('/api/admin/brands', req),
    onSuccess:  () => qc.invalidateQueries({ queryKey: ['brands'] }),
  });
};

export const useUpdateBrand = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ code, req }: { code: string; req: BrandUpsertRequest }) =>
      putAdminJson(`/api/admin/brands/${code}`, req),
    onSuccess:  () => qc.invalidateQueries({ queryKey: ['brands'] }),
  });
};

export const useDeleteBrand = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (code: string) => deleteAdminJson(`/api/admin/brands/${code}`),
    onSuccess:  () => qc.invalidateQueries({ queryKey: ['brands'] }),
  });
};

export const useToggleBrandUseYn = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ code, useYn }: { code: string; useYn: 'Y' | 'N' }) =>
      patchAdminJson(`/api/admin/brands/${code}/use-yn`, { useYn }),
    onSuccess:  () => qc.invalidateQueries({ queryKey: ['brands'] }),
  });
};

// 브랜드 TEST_YN 토글. overrides 캐시도 invalidate — 같은 브랜드를 참조하는 override 들의 effectiveIsTest 가 바뀐다.
export const useToggleBrandTestYn = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ code, testYn }: { code: string; testYn: 'Y' | 'N' }) =>
      patchAdminJson(`/api/admin/brands/${code}/test-yn`, { testYn }),
    onSuccess:  () => {
      qc.invalidateQueries({ queryKey: ['brands'] });
      qc.invalidateQueries({ queryKey: ['overrides'] });
    },
  });
};

// 브랜드 MAIL_SEND 토글. 도메인/오버라이드와 AND 결합이라 overrides 캐시도 무효화(목록 표시 갱신).
export const useToggleBrandMailSend = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ code, mailSend }: { code: string; mailSend: 'Y' | 'N' }) =>
      patchAdminJson(`/api/admin/brands/${code}/mail-send`, { mailSend }),
    onSuccess:  () => {
      qc.invalidateQueries({ queryKey: ['brands'] });
      qc.invalidateQueries({ queryKey: ['overrides'] });
    },
  });
};

// 브랜드 팝업 "Add domain" — 선택된 N개 도메인을 스케줄러 기본값(L1 상속)으로 일괄 INSERT.
// 매핑이 추가되면 overrides/domains/addable-for-brand 캐시 모두 무효화.
export const useAddDomainsToBrand = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ code, req }: { code: string; req: AddDomainsToBrandRequest }) =>
      postAdminJson(`/api/admin/brands/${code}/domains`, req),
    onSuccess:  () => {
      qc.invalidateQueries({ queryKey: ['overrides'] });
      qc.invalidateQueries({ queryKey: ['domains'] });
    },
  });
};

// ───────────── Brand 추가 메일 수신자 (SCH_BRAND_ADD_MAIL)
export const useBrandAddMails = (brandCode?: string) =>
  useQuery({
    queryKey: ['brand-add-mails', brandCode],
    meta:     { inlineError: true },  // 실패는 화면이 표·카드 자리에 LoadError 로 보인다
    queryFn:  () => getAdminJson<BrandAddMail[]>(`/api/admin/brands/${brandCode}/add-mails`),
    enabled:  !!brandCode,
  });

export const useCreateBrandAddMail = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ code, req }: { code: string; req: BrandAddMailUpsertRequest }) =>
      postAdminJson(`/api/admin/brands/${code}/add-mails`, req),
    onSuccess: (_r, vars) => qc.invalidateQueries({ queryKey: ['brand-add-mails', vars.code] }),
  });
};

export const useUpdateBrandAddMail = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, req }: { id: number; code: string; req: BrandAddMailUpsertRequest }) =>
      putAdminJson(`/api/admin/brands/add-mails/${id}`, req),
    onSuccess: (_r, vars) => qc.invalidateQueries({ queryKey: ['brand-add-mails', vars.code] }),
  });
};

export const useToggleBrandAddMailUseYn = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, useYn }: { id: number; code: string; useYn: 'Y' | 'N' }) =>
      patchAdminJson(`/api/admin/brands/add-mails/${id}/use-yn`, { useYn }),
    onSuccess: (_r, vars) => qc.invalidateQueries({ queryKey: ['brand-add-mails', vars.code] }),
  });
};

export const useDeleteBrandAddMail = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id }: { id: number; code: string }) =>
      deleteAdminJson(`/api/admin/brands/add-mails/${id}`),
    onSuccess: (_r, vars) => qc.invalidateQueries({ queryKey: ['brand-add-mails', vars.code] }),
  });
};

// 실패도 다시 조회한다 — 실패는 대개 다른 창에서 메일이 지워져 화면이 낡은 경우라 현재 순서를 보여줘야 한다.
export const useReorderBrandAddMails = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ code, req }: { code: string; req: BrandAddMailReorderRequest }) =>
      postAdminJson(`/api/admin/brands/${code}/add-mails/reorder`, req),
    onSettled: (_r, _e, vars) => qc.invalidateQueries({ queryKey: ['brand-add-mails', vars.code] }),
  });
};

// ───────────── Queues
export const useQueues = () =>
  useQuery({ queryKey: ['queues'], queryFn: () => getAdminJson<Queue[]>('/api/admin/queues'), meta: { inlineError: true } });  // 목록·선택 상자가 그 자리에 보인다

export const useCreateQueue = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (req: QueueUpsertRequest) => postAdminJson('/api/admin/queues', req),
    onSuccess:  () => qc.invalidateQueries({ queryKey: ['queues'] }),
  });
};

export const useUpdateQueue = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ name, req }: { name: string; req: QueueUpsertRequest }) =>
      putAdminJson(`/api/admin/queues/${name}`, req),
    onSuccess:  () => qc.invalidateQueries({ queryKey: ['queues'] }),
  });
};

export const useDeleteQueue = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (name: string) => deleteAdminJson(`/api/admin/queues/${name}`),
    onSuccess:  () => qc.invalidateQueries({ queryKey: ['queues'] }),
  });
};

export const useToggleQueueUseYn = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ name, useYn }: { name: string; useYn: 'Y' | 'N' }) =>
      patchAdminJson(`/api/admin/queues/${name}/use-yn`, { useYn }),
    onSuccess:  () => qc.invalidateQueries({ queryKey: ['queues'] }),
  });
};

// ───────────── Domain Groups
//   Mutations also invalidate the catalog dropdown (`['catalog','domain-groups']`)
//   so the Domain 생성/편집 폼의 DomainGroupSelect 가 즉시 새 목록을 보게 한다.
export const useDomainGroupsAdmin = () =>
  useQuery({
    queryKey: ['domain-groups'],
    meta:     { inlineError: true },  // 실패는 화면이 표·카드 자리에 LoadError 로 보인다
    queryFn:  () => getAdminJson<DomainGroup[]>('/api/admin/domain-groups'),
  });

export const useCreateDomainGroup = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (req: DomainGroupUpsertRequest) => postAdminJson('/api/admin/domain-groups', req),
    onSuccess:  () => {
      qc.invalidateQueries({ queryKey: ['domain-groups'] });
      qc.invalidateQueries({ queryKey: ['catalog', 'domain-groups'] });
    },
  });
};

export const useUpdateDomainGroup = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ code, req }: { code: string; req: DomainGroupUpsertRequest }) =>
      putAdminJson(`/api/admin/domain-groups/${code}`, req),
    onSuccess:  () => {
      qc.invalidateQueries({ queryKey: ['domain-groups'] });
      qc.invalidateQueries({ queryKey: ['catalog', 'domain-groups'] });
    },
  });
};

export const useDeleteDomainGroup = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (code: string) => deleteAdminJson(`/api/admin/domain-groups/${code}`),
    onSuccess:  () => {
      qc.invalidateQueries({ queryKey: ['domain-groups'] });
      qc.invalidateQueries({ queryKey: ['catalog', 'domain-groups'] });
    },
  });
};

export const useToggleDomainGroupUseYn = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ code, useYn }: { code: string; useYn: 'Y' | 'N' }) =>
      patchAdminJson(`/api/admin/domain-groups/${code}/use-yn`, { useYn }),
    onSuccess:  () => {
      qc.invalidateQueries({ queryKey: ['domain-groups'] });
      qc.invalidateQueries({ queryKey: ['catalog', 'domain-groups'] });
    },
  });
};

// 그룹 TEST_YN 토글. 매핑된 도메인의 effectiveIsTest 가 바뀌므로 overrides 캐시도 invalidate.
export const useToggleDomainGroupTestYn = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ code, testYn }: { code: string; testYn: 'Y' | 'N' }) =>
      patchAdminJson(`/api/admin/domain-groups/${code}/test-yn`, { testYn }),
    onSuccess:  () => {
      qc.invalidateQueries({ queryKey: ['domain-groups'] });
      qc.invalidateQueries({ queryKey: ['overrides'] });
    },
  });
};

// 그룹 측에서 N개의 도메인을 한 번에 매핑 (idempotent — 이미 매핑된 건 skip).
// 도메인의 domainGroups 가 갱신되므로 ['domains'] 도 무효화.
export const useAddDomainsToGroup = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ groupCode, req }: { groupCode: string; req: AddDomainsToGroupRequest }) =>
      postAdminJson(`/api/admin/domain-groups/${groupCode}/domains`, req),
    onSuccess:  () => {
      qc.invalidateQueries({ queryKey: ['domains'] });
      qc.invalidateQueries({ queryKey: ['domain-groups'] });
    },
  });
};

export const useRemoveDomainFromGroup = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ groupCode, domainCode }: { groupCode: string; domainCode: string }) =>
      deleteAdminJson(`/api/admin/domain-groups/${groupCode}/domains/${domainCode}`),
    onSuccess:  () => {
      qc.invalidateQueries({ queryKey: ['domains'] });
      qc.invalidateQueries({ queryKey: ['domain-groups'] });
    },
  });
};

// ───────────── Schedule (L1) — read only
//   Domain + Schedule writes now go through the unified /with-schedule endpoint
//   on DomainsController, so no direct upsert lives here.
//   Legacy domains from before the unified endpoint may still lack a schedule
//   row; swallow 404 into null so the detail page can seed defaults.
export const useSchedule = (domainCode?: string) =>
  useQuery({
    queryKey: ['schedule', domainCode],
    meta:     { inlineError: true },  // 실패는 화면이 표·카드 자리에 LoadError 로 보인다
    queryFn:  () => getAdminJsonOrNull<Schedule>(`/api/admin/schedules/${domainCode}`),
    enabled: !!domainCode,
  });

// ───────────── Brand overrides (L2)
export const useOverrides = (domain?: string) =>
  useQuery({
    queryKey: ['overrides', 'by-domain', domain],
    meta:     { inlineError: true },  // 실패는 화면이 표·카드 자리에 LoadError 로 보인다
    queryFn:  () => getAdminJson<BrandOverride[]>('/api/admin/brand-overrides', { domain }),
    enabled:  !!domain,
  });

// "Referenced by" — list of overrides that target a specific brand, used by the
// Brand edit modal to show which domains reference the brand.
export const useOverridesByBrand = (brand?: string) =>
  useQuery({
    queryKey: ['overrides', 'by-brand', brand],
    meta:     { inlineError: true },  // 실패는 화면이 표·카드 자리에 LoadError 로 보인다
    queryFn:  () => getAdminJson<BrandOverride[]>('/api/admin/brand-overrides', { brand }),
    enabled:  !!brand,
  });

// Invalidate BOTH domain-keyed and brand-keyed override caches on any write,
// so the Brand edit modal's "used by" table stays fresh.
const invalidateOverrides = (qc: ReturnType<typeof useQueryClient>) => {
  qc.invalidateQueries({ queryKey: ['overrides'] });
};

// 매핑 생성·삭제는 도메인 목록의 brandCount 도 바꾸므로 ['domains'] 도 무효화(useAddDomainsToBrand 와 같음).
export const useCreateOverride = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (req: BrandOverrideUpsertRequest) => postAdminJson('/api/admin/brand-overrides', req),
    onSuccess:  () => {
      invalidateOverrides(qc);
      qc.invalidateQueries({ queryKey: ['domains'] });
    },
  });
};

export const useUpdateOverride = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (args: { domainCode: string; brandCode: string; req: BrandOverrideUpsertRequest }) =>
      putAdminJson(`/api/admin/brand-overrides/${args.domainCode}/${args.brandCode}`, args.req),
    onSuccess:  () => invalidateOverrides(qc),
  });
};

export const useDeleteOverride = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (args: { domainCode: string; brandCode: string }) =>
      deleteAdminJson(`/api/admin/brand-overrides/${args.domainCode}/${args.brandCode}`),
    onSuccess: () => {
      invalidateOverrides(qc);
      qc.invalidateQueries({ queryKey: ['domains'] });
    },
  });
};

export const useToggleOverrideUseYn = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (args: { domainCode: string; brandCode: string; useYn: 'Y' | 'N' }) =>
      patchAdminJson(`/api/admin/brand-overrides/${args.domainCode}/${args.brandCode}/use-yn`, { useYn: args.useYn }),
    onSuccess: () => invalidateOverrides(qc),
  });
};

// override MAIL_SEND 토글. 도메인 mailSend 와 AND 결합 — 둘 다 'Y' 일 때만 실제 발송.
export const useToggleOverrideMailSend = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (args: { domainCode: string; brandCode: string; mailSend: 'Y' | 'N' }) =>
      patchAdminJson(`/api/admin/brand-overrides/${args.domainCode}/${args.brandCode}/mail-send`, { mailSend: args.mailSend }),
    onSuccess: () => invalidateOverrides(qc),
  });
};

// ───────────── Override 시간대별 cron (SCH_DOMAIN_BRAND_CRON)
// 모달 안에서 항상 최신 목록을 보도록 전용 GET. 키를 ['overrides',...] 하위에 둬서
// invalidateOverrides 한 번에 같이 갱신된다.
export const useOverrideCrons = (domainCode?: string, brandCode?: string) =>
  useQuery({
    queryKey: ['overrides', 'crons', domainCode, brandCode],
    meta:     { inlineError: true },  // 실패는 화면이 표·카드 자리에 LoadError 로 보인다
    queryFn:  () => getAdminJson<OverrideCron[]>(`/api/admin/brand-overrides/${domainCode}/${brandCode}/crons`),
    enabled:  !!domainCode && !!brandCode,
  });

export const useCreateOverrideCron = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (args: { domainCode: string; brandCode: string; req: OverrideCronUpsertRequest }) =>
      postAdminJson(`/api/admin/brand-overrides/${args.domainCode}/${args.brandCode}/crons`, args.req),
    onSuccess: () => invalidateOverrides(qc),
  });
};

export const useUpdateOverrideCron = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (args: { id: number; req: OverrideCronUpsertRequest }) =>
      putAdminJson(`/api/admin/brand-overrides/crons/${args.id}`, args.req),
    onSuccess: () => invalidateOverrides(qc),
  });
};

export const useDeleteOverrideCron = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (args: { id: number }) =>
      deleteAdminJson(`/api/admin/brand-overrides/crons/${args.id}`),
    onSuccess: () => invalidateOverrides(qc),
  });
};

export const useToggleOverrideCronUseYn = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (args: { id: number; useYn: 'Y' | 'N' }) =>
      patchAdminJson(`/api/admin/brand-overrides/crons/${args.id}/use-yn`, { useYn: args.useYn }),
    onSuccess: () => invalidateOverrides(qc),
  });
};

// "Run now" — 단일 (도메인,브랜드) 즉시 enqueue.
// 매 호출마다 unique LogId 발급 → 새 SCH_JOB_LOG row 생성, 자동 cron 과 충돌 없음.
// 활성 잡(ENQUEUED/RUNNING) 있으면 200 + skipReason 채워진 응답 (호출자가 분기).
// 성공 시 로그 목록·실행 결과 현황·대시보드 캐시를 무효화해서 바로 보이도록.
// runAs: 'test' | 'operational' — 사용자가 RunNowModal 에서 선택. 필수 body.
//   서버 측 DB IsTest=Y && runAs='operational' 조합은 400 으로 반려된다 (UI 가 잘못된 선택지를 보낸 방어).
export const useRunOverrideNow = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (args: { domainCode: string; brandCode: string; runAs: RunAsMode }) =>
      postAdminJson<ManualRunResult>(
        `/api/admin/runs/brand/${args.domainCode}/${args.brandCode}`,
        { runAs: args.runAs },
      ),
    onSuccess: () => invalidateRunViews(qc),
  });
};

// ───────────── Job logs
// SCH_JOB_LOG.LOG_ID 는 고정폭 CHAR 컬럼이라 백엔드가 트레일링 공백을 패딩해 내려준다
// (예: '49090…788e2c' 뒤에 공백 8칸). 그대로 두면 URL 쿼리(?logId=…++++)·경로(/…/deprecate)에
// 공백이 인코딩돼 새고, 딥링크/복사도 지저분해진다. → **페치 진입점에서 1회 trim** 해서
// 앱 전체가 깔끔한 logId 를 쓰게 한다(진짜 해결은 백엔드 DTO trim/varchar).
export const cleanJobLogId = (l: JobLog): JobLog => ({ ...l, logId: l.logId.trim() });
const cleanPagedJobLogs = (r: PagedResult<JobLog>): PagedResult<JobLog> => ({
  ...r,
  items: r.items.map(cleanJobLogId),
});

// Query 는 Admin 백엔드(DB 만). Deprecate 액션은 Hangfire 의존이라 Scheduler 백엔드 유지.
export const useJobLogs = (q: JobLogQuery) =>
  useQuery({
    queryKey: ['job-logs', q],
    meta:     { inlineError: true },  // 실패는 화면이 표·카드 자리에 LoadError 로 보인다
    queryFn:  async () => cleanPagedJobLogs(await getAdminJson<PagedResult<JobLog>>('/api/admin/job-logs', q)),
  });

// LOG_ID 단건 조회 — "실행 결과 현황" 등에서 logId 딥링크(?logId=)로 상세 모달을 여는 용도.
// 전용 엔드포인트 GET /api/admin/job-logs/{logId}(핫 테이블만 — 아카이브 로그는 없음). 없으면 404 → null 로 흡수.
export const useJobLog = (logId?: string) =>
  useQuery({
    queryKey: ['job-log', logId],
    queryFn:  async () => {
      const log = await getAdminJsonOrNull<JobLog>(`/api/admin/job-logs/${logId}`);
      return log && cleanJobLogId(log);
    },
    enabled: !!logId,
  });

// 운영자가 ENQUEUED/RUNNING 잡을 즉시 종료 처리. DeprecationSweeper (매분 1회) 를 못 기다리는 운영
// 상황을 위한 escape hatch. 백엔드가 (1) row 검증 — 없으면 404 / terminal 이면 409 — 한 뒤
// (2) HangfireJobId 가 있으면 Hangfire 측 종료 상태(Succeeded/Failed/Deleted) 를 검사해 stale row
// 면 Hangfire 실제 결과로 sync, 아니면 (3) DB 를 DEPRECATED 로 묻는다. 결과 outcome 에 따라
// 호출자는 다른 토스트를 띄울 수 있다 (Deprecated / SyncedFromHangfire / AlreadyTransitioned).
// 성공 시 그 로그 단건·목록·현황·대시보드 캐시를 무효화해 즉시 갱신. body 가 필요 없는 POST 라 wrapper 생략(undefined).
export const useDeprecateJobLog = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (logId: string) =>
      postAdminJson<JobLogDeprecateResult>(`/api/admin/job-logs/${logId}/deprecate`),
    onSuccess:  (_r, logId) => {
      qc.invalidateQueries({ queryKey: ['job-log', logId] });
      invalidateRunViews(qc);
    },
  });
};

// UserId → 표시명 매핑. JobLog.Requester 컬럼처럼 audit 화면이 한 페이지 안에서
// 등장한 unique UserId 만 한 번에 lookup 하는 용도. 빈 입력은 호출 자체를 skip
// (enabled), 결과는 Map<userId, name> 으로 변환해 렌더 시 O(1) 매핑.
// staleTime 5분 — 사용자명은 자주 안 바뀌므로 같은 페이지 안에서 refetch 안 함.
export const useUserNames = (userIds: readonly string[]) => {
  // useQuery 의 queryKey 자동 직렬화에 의존 — 입력 순서가 달라도 같은 집합이면
  // 같은 키가 되도록 정렬 + 중복 제거.
  const sorted = Array.from(new Set(userIds)).sort();
  return useQuery({
    queryKey: ['user-names', sorted],
    queryFn:  async () => {
      const items = await authApi.lookup({ userIds: sorted });
      return new Map(items.map(i => [i.userId, i.name || i.userName] as const));
    },
    enabled:   sorted.length > 0,
    staleTime: 5 * 60 * 1000,
  });
};

// SCH_JOB_LOG_BK 조회. 백엔드는 매일 03:00 KST 에 retention 경과 + 터미널 status row 를
// 핫에서 BK 로 옮기므로, 30일 이전 로그는 이쪽에서 검색.
export const useJobLogsArchive = (q: JobLogQuery) =>
  useQuery({
    queryKey: ['job-logs-archive', q],
    meta:     { inlineError: true },  // 실패는 화면이 표·카드 자리에 LoadError 로 보인다
    queryFn:  async () => cleanPagedJobLogs(await getAdminJson<PagedResult<JobLog>>('/api/admin/job-logs-archive', q)),
  });

// ───────────── Catalog (dropdowns)
// job-types / param-dtos / schema 는 Scheduler 어셈블리 reflection 이라 Scheduler 백엔드 유지.
// domain-groups / timezones / job-statuses 는 DB/런타임 enum 이므로 Admin 백엔드.
// 실패는 선택 상자(selects.tsx)가 그 자리에 보인다(selectLoadProps).
export const useJobTypes     = () =>
  useQuery({ queryKey: ['catalog', 'job-types'],      queryFn: () => getAdminJson<JobTypeItem[]>('/api/admin/catalog/job-types'),         staleTime: Infinity, meta: { inlineError: true } });
export const useParamDtos    = () =>
  useQuery({ queryKey: ['catalog', 'param-dtos'],     queryFn: () => getAdminJson<ParamDtoItem[]>('/api/admin/catalog/param-dtos'),       staleTime: Infinity, meta: { inlineError: true } });
export const useDomainGroups = () =>
  useQuery({ queryKey: ['catalog', 'domain-groups'],  queryFn: () => getAdminJson<CodeItem[]>('/api/admin/catalog/domain-groups'), meta: { inlineError: true } });
export const useTimezones    = () =>
  useQuery({ queryKey: ['catalog', 'timezones'],      queryFn: () => getAdminJson<CodeItem[]>('/api/admin/catalog/timezones'),      staleTime: Infinity, meta: { inlineError: true } });
export const useJobStatuses  = () =>
  useQuery({ queryKey: ['catalog', 'job-statuses'],   queryFn: () => getAdminJson<CodeItem[]>('/api/admin/catalog/job-statuses'),   staleTime: Infinity, meta: { inlineError: true } });

export const useParamDtoSchema = (fqdn?: string | null) =>
  useQuery({
    queryKey: ['catalog', 'param-schema', fqdn],
    queryFn:  () => getAdminJson<ParamDtoSchema>('/api/admin/catalog/param-dtos/schema', { fqdn }),
    enabled:  !!fqdn,
    retry:    false,        // unknown type → silently fall back to free-form editor
    staleTime: Infinity,
  });

// ───────────── Dispatch pause
// Dispatch 전역 일시정지 상태 조회 / 토글. cron 자동 발사만 가드되며 manual run 은 영향 없음.
export const useDispatchPause = () =>
  useQuery({
    queryKey:  ['dispatch-pause'],
    meta:      { inlineError: true },  // 실패는 화면이 표·카드 자리에 LoadError 로 보인다
    queryFn:   () => getAdminJson<DispatchPauseState>('/api/admin/dispatch-pause'),
    staleTime: 0,
  });

export const useSetDispatchPause = () => {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (req: DispatchPauseRequest) =>
      postAdminJson<DispatchPauseState>('/api/admin/dispatch-pause', req),
    onSuccess:  () => qc.invalidateQueries({ queryKey: ['dispatch-pause'] }),
  });
};

// ───────────── 다음 실행 예정 브라우저 (next-runs)
// 브랜드 기준 탭 좌측 — 활성 발사 후보(override)를 1건 이상 가진 브랜드 목록.
export const useNextRunBrands = () =>
  useQuery({
    queryKey: ['next-runs', 'brands'],
    meta:     { inlineError: true },  // 실패는 화면이 표·카드 자리에 LoadError 로 보인다
    queryFn:  () => getAdminJson<NextRunBrand[]>('/api/admin/next-runs/brands'),
  });

// 브랜드 기준 탭 중앙 — 선택 브랜드가 활성 override 를 가진 도메인 목록.
export const useNextRunDomainsForBrand = (brand?: string) =>
  useQuery({
    queryKey: ['next-runs', 'domains-for-brand', brand],
    meta:     { inlineError: true },  // 실패는 화면이 표·카드 자리에 LoadError 로 보인다
    queryFn:  () => getAdminJson<NextRunDomain[]>(`/api/admin/next-runs/brands/${brand}/domains`),
    enabled:  !!brand,
  });

// 도메인 기준 탭 좌측 — 활성 발사 후보(override)를 1건 이상 가진 도메인 목록.
export const useNextRunDomains = () =>
  useQuery({
    queryKey: ['next-runs', 'domains'],
    meta:     { inlineError: true },  // 실패는 화면이 표·카드 자리에 LoadError 로 보인다
    queryFn:  () => getAdminJson<NextRunDomain[]>('/api/admin/next-runs/domains'),
  });

// 도메인 기준 탭 중앙 — 선택 도메인이 활성 override 를 가진 브랜드 목록.
export const useNextRunBrandsForDomain = (domain?: string) =>
  useQuery({
    queryKey: ['next-runs', 'brands-for-domain', domain],
    meta:     { inlineError: true },  // 실패는 화면이 표·카드 자리에 LoadError 로 보인다
    queryFn:  () => getAdminJson<NextRunBrand[]>(`/api/admin/next-runs/domains/${domain}/brands`),
    enabled:  !!domain,
  });

// 우측 col — 다음 발사 예정 Top 20. brand/domain 중 최소 1개 필요(한쪽만 주면 그 축 전체 합산).
// 자동 cron 시각이라 30초마다 refetch (대시보드 summary 와 동일 간격).
export const useNextRunUpcoming = (brand?: string, domain?: string) => {
  // keep-alive: 이 탭이 숨겨져 있으면(선택이 유지된 채 다른 탭을 봐도) 30초 배경 폴링을 멈춘다.
  // 탭을 다시 열어도 즉시 refetch 하지 않는다 — 다음 30초 폴링으로 갱신된다(탭 전환 시 자동 새로고침 제거).
  const active = useTabActive();
  const q = useQuery({
    queryKey: ['next-runs', 'upcoming', brand ?? null, domain ?? null],
    meta:     { inlineError: true },  // 실패는 화면이 표·카드 자리에 LoadError 로 보인다
    queryFn:  () => getAdminJson<UpcomingRun[]>('/api/admin/next-runs/upcoming', { brand, domain, top: 20 }),
    enabled:  !!brand || !!domain,
    refetchInterval: active ? 30_000 : false,
  });
  return q;
};

// ───────────── 실행 결과 현황 (run-status)
// 선택 (일자 + 시) 1시간 윈도우의 예상 발사 × 실제 로그 대조. date 는 'YYYY-MM-DD'(KST),
// hour 는 0~23. brand/domain 은 선택 협소화. 좌측 피커는 next-runs 훅을 그대로 재사용한다.
// 오늘·현재 시를 볼 때만 배경 폴링(30초) — 과거 윈도우는 결과가 굳어지므로 폴링 안 함.
export const useRunStatus = (
  date: string,
  hour: number,
  brand?: string,
  domain?: string,
  live = false,
) => {
  const active = useTabActive();
  return useQuery({
    queryKey: ['run-status', date, hour, brand ?? null, domain ?? null],
    meta:     { inlineError: true },  // 실패는 화면이 표·카드 자리에 LoadError 로 보인다
    queryFn:  () => getAdminJson<RunStatusRow[]>('/api/admin/run-status', { date, hour, brand, domain }),
    refetchInterval: live && active ? 30_000 : false,
  });
};

