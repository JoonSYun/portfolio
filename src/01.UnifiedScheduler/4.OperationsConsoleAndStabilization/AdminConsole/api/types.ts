import type { PagedResult } from '../../../common/api/types';

export type Domain = {
  domainCode: string;
  domainGroups: string[];
  jobType: string;
  paramDtoType?: string | null;
  defaultQueue: string;
  useYn: 'Y' | 'N';
  // 테스트 도메인 마커. 'Y' 면 매핑된 모든 Job 이 ExecuteCoreAsync 스킵.
  testYn: 'Y' | 'N';
  // 도메인별 에러 메일 발송 플래그. 브랜드 mailSend 와 AND 결합되어 둘 다 'Y' 일 때만 발송. 기본 'N'.
  mailSend: 'Y' | 'N';
  remark?: string | null;
  // 도메인 자유 설명(긴 메모, NVARCHAR(MAX)). 짧은 비고는 remark. 상세 조회에서만 채워지고 목록에선 비어있다.
  domainMemo?: string | null;
  regDt: string;
  updDt: string;
  // L1 schedule cron, joined in by the list endpoint. null when no schedule row exists.
  cronExpr?: string | null;
  // SCH_DOMAIN_BRAND_OVERRIDE 매핑 개수 (USE_YN 무관 전체).
  brandCount: number;
  // L1 SCH_DOMAIN_SCHEDULE.USE_YN. 목록 화면의 USE 토글이 변경하는 컬럼.
  // 도메인 자체의 useYn (= SCH_DOMAIN.USE_YN) 은 도메인 편집 모달의 Enabled (Domain) 칸에서만 변경.
  scheduleUseYn: 'Y' | 'N';
};

export type DomainUpsertRequest = Omit<Domain, 'regDt' | 'updDt' | 'cronExpr' | 'brandCount' | 'scheduleUseYn'>;

export type Brand = {
  brandCode: string;
  centerCode?: string | null;
  description?: string | null;
  useYn: 'Y' | 'N';
  // 테스트 브랜드 마커. 'Y' 면 이 브랜드의 모든 Job 이 ExecuteCoreAsync 스킵.
  testYn: 'Y' | 'N';
  // 브랜드(마스터) 단위 에러 메일 발송 플래그. 도메인/오버라이드 mailSend 와 AND 결합. 기본 'N'.
  mailSend: 'Y' | 'N';
};

export type BrandUpsertRequest = Brand;

export type Queue = {
  queueName: string;
  description?: string | null;
  useYn: 'Y' | 'N';
  // 큐 전용 BackgroundJobServer 의 동시 실행 워커 수. 기본 1 = 큐 내부 순차 실행.
  workerCount: number;
  regDt: string;
  updDt: string;
};

export type QueueUpsertRequest = {
  queueName: string;
  description?: string | null;
  useYn: 'Y' | 'N';
  workerCount: number;
};

export type DomainGroup = {
  groupCode: string;
  description?: string | null;
  sortOrder: number;
  useYn: 'Y' | 'N';
  // 테스트 그룹 마커. 'Y' 면 이 그룹에 매핑된 모든 도메인 Job 이 ExecuteCoreAsync 스킵.
  testYn: 'Y' | 'N';
  regDt: string;
  updDt: string;
};

export type DomainGroupUpsertRequest = {
  groupCode: string;
  description?: string | null;
  sortOrder: number;
  useYn: 'Y' | 'N';
  testYn: 'Y' | 'N';
};

export type AddDomainsToGroupRequest = {
  domainCodes: string[];
};

export type AddDomainsToBrandRequest = {
  domainCodes: string[];
};

export type Schedule = {
  domainCode: string;
  cronExpr: string;
  timezoneId: string;
  validFrom?: string | null;
  validTo?: string | null;
  timeoutSec: number;
  // 큐에 들어간 뒤 시작되기까지 허용하는 대기 한도(초). 0 = 제한 없음.
  // timeoutSec 은 "시작된 뒤" 실행 한도라 별개 값이다.
  deprecateSec: number;
  maxConcurrency: number;
  payloadJson?: string | null;
  useYn: 'Y' | 'N';
  updDt: string;
  syncedUpdDt?: string | null;
};

export type ScheduleUpsertRequest = Omit<Schedule, 'domainCode' | 'updDt' | 'syncedUpdDt'>;

// Combined payload for the unified domain+schedule create/update endpoint.
// The UI always submits both together; the server writes them in one transaction.
export type DomainWithScheduleUpsertRequest = {
  domain:   DomainUpsertRequest;
  schedule: ScheduleUpsertRequest;
};

export type BrandOverride = {
  domainCode: string;
  brandCode: string;
  centerCode?: string | null;
  cronExpr?: string | null;
  timezoneId?: string | null;
  validFrom?: string | null;
  validTo?: string | null;
  timeoutSec?: number | null;
  // 브랜드별 대기 한도 덮어쓰기. null = 도메인 스케줄 값 상속, 0 = 제한 없음.
  deprecateSec?: number | null;
  payloadJson?: string | null;
  // 브랜드별 큐 오버라이드. null = 도메인의 defaultQueue 사용.
  queue?: string | null;
  useYn: 'Y' | 'N';
  // 도메인×브랜드 에러 메일 발송 플래그. 도메인 mailSend 와 AND 결합. 기본 'N'.
  mailSend: 'Y' | 'N';
  regDt: string;
  updDt: string;
  // 실효 IsTest. Domain.TEST_YN='Y' OR Brand.TEST_YN='Y' OR (매핑된 그룹 중 TEST_YN='Y' 1개 이상).
  // RunNowModal 이 강제 test 분기 / 출처 표시에 사용.
  effectiveIsTest: boolean;
  // 실효 IsTest=true 일 때 그 원인. "domain" / "brand" / "group:{code}" 의 조합. false 면 빈 배열.
  testSource: string[];
  // 시간대별 cron 목록 (SCH_DOMAIN_BRAND_CRON). 0개면 도메인 L1 스케줄 상속.
  // 디스패처는 활성 cron 중 이번 tick 에 매치되는 게 하나라도 있으면(OR) 발사 → 겹쳐도 무해.
  crons: OverrideCron[];
};

// SCH_DOMAIN_BRAND_CRON 1행 — 브랜드 override 의 시간대별 cron.
export type OverrideCron = {
  id: number;
  domainCode: string;
  brandCode: string;
  cronExpr: string;
  timezoneId?: string | null;
  validFrom?: string | null;
  validTo?: string | null;
  useYn: 'Y' | 'N';
  regDt: string;
  updDt: string;
  regUser?: string | null;
  updUser?: string | null;
};

// cron 생성/수정 요청. 도메인/브랜드는 라우트로 받으므로 body 에는 cron 속성만.
export type OverrideCronUpsertRequest = {
  cronExpr: string;
  timezoneId?: string | null;
  validFrom?: string | null;
  validTo?: string | null;
  useYn: 'Y' | 'N';
};

// 서버 계산/관리 필드(regDt/updDt/effectiveIsTest/testSource/crons)는 override upsert 에 포함하지 않음.
// crons 는 별도 서브리소스(/crons) 로 관리.
export type BrandOverrideUpsertRequest =
  Omit<BrandOverride, 'regDt' | 'updDt' | 'effectiveIsTest' | 'testSource' | 'crons'>;

// SCH_BRAND_ADD_MAIL 1행 — 브랜드별 추가 에러 메일 수신자.
// BusinessException 발생 시 개발팀 외에 이 목록의 활성(useYn='Y') 이메일에도 발송.
export type BrandAddMail = {
  id: number;
  brandCode: string;
  email: string;
  useYn: 'Y' | 'N';
  regDt: string;
  updDt: string;
  regUser?: string | null;
  updUser?: string | null;
};

// 추가 메일 생성/수정 요청. 브랜드 코드는 라우트로 받으므로 body 에는 email/useYn 만.
export type BrandAddMailUpsertRequest = {
  email: string;
  useYn: 'Y' | 'N';
};

// 추가 메일 순서 변경 요청. 순서 = ID 오름차순이라 서버가 ids 를 지우고 이 순서대로 다시 등록해
// 새 ID 로 목록 맨 뒤에 붙인다. 옮긴 메일과 그 아래 메일 전부를 새 순서대로 보낸다.
export type BrandAddMailReorderRequest = {
  ids: number[];
};

export type RunAsMode = 'test' | 'operational';

// "Run now" 응답.
//   - 정상 enqueue: logId/hangfireJobId/queue 채워지고 skipReason 은 null.
//   - skip 된 경우 (도메인 fan-out 에서 일부 브랜드가 활성 잡 보유): logId/hangfireJobId/queue 는 null,
//     skipReason 에 사람이 읽는 사유 ("이미 실행 중") 가 들어감.
// 단일 브랜드 RunNow 는 활성 잡 존재 시 409 로 throw 되므로 항상 success 형태만 반환된다.
export type ManualRunResult = {
  domainCode: string;
  brandCode: string;
  logId: string | null;
  hangfireJobId: string | null;
  queue: string | null;
  skipReason: string | null;
  // 실효 IsTest — Dispatcher 가 (Domain/Group/Brand TEST_YN) OR (runAs=test) 의 OR 결과로 결정.
  // 프론트가 토스트에 "테스트로 실행됨" 분기에 사용.
  isTest: boolean;
};

export type JobStatus = 'ENQUEUED' | 'RUNNING' | 'SUCCESS' | 'FAILED' | 'DEPRECATED' | 'SKIPPED_DUP';

export type JobLog = {
  logId: string;
  domainCode: string;
  brandCode: string;
  centerCode?: string | null;
  scheduledDt: string;
  enqueuedDt: string;
  startedDt?: string | null;
  finishedDt?: string | null;
  status: JobStatus;
  hangfireJobId?: string | null;
  timeoutSec: number;
  // 큐 투입 시점의 대기 한도 스냅샷(초). 0 = 제한 없음. 이 기능 도입 이전 아카이브 row 는 0.
  deprecateSec: number;
  errorMessage?: string | null;
  // 본 실행이 테스트 path 였는지. 'Y' 인 row 는 STATUS=SUCCESS + ERROR_MESSAGE="Skipped: test mode".
  testYn: 'Y' | 'N';
  // 실행 요청자. cron 자동은 "Scheduler", Admin Run now 는 로그인 아이디. V020 이전 row 는 null.
  requester?: string | null;
};

// 수동 DEPRECATED 엔드포인트 응답.
//   - Deprecated           : 일반 흐름, DB 를 DEPRECATED 로 묻고 Hangfire 큐도 정리됨.
//   - SyncedFromHangfire   : Hangfire 가 이미 종료(Succeeded/Failed/Deleted) 한 stale row 를 그 결과로 sync.
//                           hangfireStateName 에 원본 Hangfire state ("Succeeded"|"Failed"|"Deleted").
//   - AlreadyTransitioned  : SELECT~UPDATE 사이 race-loss (sweeper/워커가 먼저 전이) — 운영자 의도는 이미 달성.
export type JobLogDeprecateResult = {
  outcome:           'Deprecated' | 'SyncedFromHangfire' | 'AlreadyTransitioned';
  finalStatus:       JobStatus;
  hangfireStateName?: string | null;
};

export type JobLogQuery = {
  from?: string;
  to?: string;
  status?: JobStatus;
  domain?: string;
  brand?: string;
  // 운영/테스트 필터. 'Y' | 'N' | undefined. undefined = 전체.
  // 기본은 페이지에서 'N' 으로 강제 (운영자 default view).
  testYn?: 'Y' | 'N';
  page?: number;
  size?: number;
};

export type CodeItem = { value: string; label: string; group?: string | null };

export type JobTypeItem = {
  fqdn: string;
  name: string;
  namespace?: string | null;
  assemblyQualifiedName: string;
  /** Short name of TParam derived from JobBase<TSelf, TParam>. null if not derivable. */
  paramName?: string | null;
};

export type ParamDtoItem = {
  fqdn: string;
  name: string;
  namespace?: string | null;
  assemblyQualifiedName: string;
};

export type ParamProperty = {
  name: string;
  type: string;
  nullable: boolean;
  required: boolean;
  description?: string | null;
};

export type ParamDtoSchema = {
  fqdn: string;
  name: string;
  properties: ParamProperty[];
  skeletonJson: string;
};

// Dispatch 전역 일시정지 상태. cron 자동 발사만 차단하고 수동 Run now 는 영향 없음.
export type DispatchPauseState = {
  isPaused: boolean;
  pausedDt?: string | null;
  pausedBy?: string | null;
  reason?:   string | null;
};

export type DispatchPauseRequest = {
  isPaused: boolean;
  reason?:  string | null;
};

// ───────────── 다음 실행 예정 브라우저 (next-runs)
// 활성 발사 후보(override)를 1건 이상 가진 브랜드 1행. domainCount = 발사 후보 도메인 수.
export type NextRunBrand = {
  brandCode: string;
  description?: string | null;
  domainCount: number;
};

// 도메인 1행. brandCount 는 도메인 기준 탭 좌측 목록에서만 채워진다(브랜드 기준 탭 중앙 목록에선 0).
export type NextRunDomain = {
  domainCode: string;
  remark?: string | null;
  brandCount?: number;
};

// 다음 발사 예정 1줄 — 대시보드 UpcomingRunsCard 와 동일 shape (백엔드 DashboardUpcomingRunDto).
// 시간대 cron(SCH_DOMAIN_BRAND_CRON) 매치 시 그 cron, 없으면 도메인 L1 cron 이 cronExpr 로 내려온다.
export type UpcomingRun = {
  domainCode: string;
  remark: string | null;
  brandCode: string;
  cronExpr: string;
  // KST wall-clock 문자열 (toBackendDateTime 과 동일 contract — UTC Z 아님).
  nextDt: string;
  queue: string;
  jobType: string;
  paramDtoType: string | null;
};

// ───────────── 실행 결과 현황 (run-status)
// JobStatus(로그 실제 상태) + 합성 상태 2종.
//   MISSED    : 예상 발사인데 로그가 없고 예상시각이 이미 지남(미실행).
//   SCHEDULED : 예상 발사인데 로그가 없고 예상시각이 아직 미래(예정).
export type RunStatus = JobStatus | 'MISSED' | 'SCHEDULED';

// 실행 결과 현황 1행 — 예상 발사(Domain × Brand × 시각) × 실제 로그 합성 (백엔드 RunStatusRowDto).
export type RunStatusRow = {
  domainCode: string;
  remark: string | null;
  brandCode: string;
  brandDescription: string | null;
  // 예상 발사 시각 (KST wall-clock 문자열, UTC Z 아님). 로그가 있으면 그 SCHEDULED_DT 와 분 단위 일치.
  scheduledDt: string;
  cronExpr: string;
  status: RunStatus;
  startedDt: string | null;
  finishedDt: string | null;
  // 실행 소요 초 = finishedDt − startedDt (둘 다 있을 때만).
  durationSec: number | null;
  errorMessage: string | null;
  logId: string | null;
};

