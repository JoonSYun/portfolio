import { useMemo } from 'react';
import { DatePicker, Form, Segmented, Tooltip } from 'antd';
import { ReloadOutlined } from '@ant-design/icons';
import dayjs, { Dayjs } from 'dayjs';
import { useJobLog, useJobLogs, useUserNames } from '../api/hooks';
import type { JobLog, JobLogQuery, JobStatus } from '../api/types';
import { BrandCodeSelect, DomainCodeSelect, JobStatusSelect } from '../components/selects';
import JobLogDetailModal from '../components/JobLogDetailModal';
import JobStatusTag from '../components/JobStatusTag';
import TestModeTag from '../components/TestModeTag';
import { HeaderWithTip, LabelTag, LogIdCell } from '../../../common/components';
import { ListScreen, PageShell, definePage } from '../../../common/layout';
import { useUrlListState } from '../../../common/utils/useUrlListState';
import { useSyncedFilterForm } from '../../../common/utils/useSyncedFilterForm';
import { useUrlModal } from '../../../common/utils/useUrlModal';
import { FIELD_DOCS } from '../constants/fieldDocs';
import { CONTAINER } from '../../../common/constants/container';
import { formatDateTime, toBackendDateTime } from '../../../common/utils/datetime';
import { PHONE_MEDIA } from '../../../common/constants/breakpoints';
import { useMediaQuery } from '../../../common/hooks';

// 'ALL' = 필터 없음, 'Y' = 테스트만, 'N' = 운영만 (default).
type TestModeFilter = 'N' | 'Y' | 'ALL';

type Filters = {
  range?: [Dayjs | null, Dayjs | null];
  status?: JobStatus;
  domain?: string;
  brand?: string;
  testMode?: TestModeFilter;
};

const FILTER_KEYS = ['from', 'to', 'status', 'domain', 'brand', 'testMode'] as const;

export default function JobLogPage() {
  const isPhone = useMediaQuery(PHONE_MEDIA);
  const [form] = Form.useForm<Filters>();
  const url = useUrlListState({ defaultSize: 50 });

  const fromIso = url.get('from');
  const toIso   = url.get('to');

  const filters: Filters = useMemo(() => {
    const from = fromIso ? dayjs(fromIso) : null;
    const to   = toIso   ? dayjs(toIso)   : null;
    // testMode 가 URL 에 없으면 기본 'ALL' (전체) — 운영/테스트 한 화면에서 같이 보고 싶다는 요구.
    const testMode = (url.get('testMode') as TestModeFilter | undefined) ?? 'ALL';
    return {
      range:  from || to ? [from, to] : undefined,
      status: url.get('status') as JobStatus | undefined,
      domain: url.get('domain'),
      brand:  url.get('brand'),
      testMode,
    };
  }, [fromIso, toIso, url]);

  // keep-alive: URL 이 필터의 단일 진실. 딥링크/뒤로가기로 URL 이 바뀌면 필터 폼(draft)을 재동기화한다.
  // (테이블은 URL 을 매 렌더 읽어 스스로 갱신되지만, 폼 입력칸은 initialValues 마운트 1회라 stale 됨)
  useSyncedFilterForm(form, filters, url.get, FILTER_KEYS);

  const query: JobLogQuery = {
    from:   fromIso,
    to:     toIso,
    status: filters.status,
    domain: filters.domain,
    brand:  filters.brand,
    // 'ALL' 은 서버 필터 미적용, 'Y'/'N' 은 그대로 전달.
    testYn: filters.testMode === 'ALL' ? undefined : filters.testMode,
    page:   url.page,
    size:   url.size,
  };

  const { data, isFetching, error, refetch } = useJobLogs(query);

  // keep-alive: 로그 상세 모달을 URL(?logId=)이 소유 → 링크 공유·뒤로가기 닫기. 현재 페이지에 있는 로그는 그 항목
  // (목록 새로고침을 따라감)을, 다른 페이지 로그는 logId 단건 조회(useJobLog)를 보여 줘 공유 링크도 열린다.
  const detail = useUrlModal<JobLog>({ param: 'logId', items: data?.items, keyOf: (l) => l.logId });
  const { data: detailLog } = useJobLog(detail.value);

  // 현재 페이지에 등장한 unique requester ID 만 추려 한 번에 lookup.
  // 'Scheduler' / 'unknown' / null 은 백엔드 사용자가 아니므로 제외.
  const requesterIds = useMemo(
    () => Array.from(new Set(
      (data?.items ?? [])
        .map(r => r.requester)
        .filter((v): v is string => !!v && v !== 'Scheduler' && v !== 'unknown'),
    )),
    [data?.items],
  );
  const { data: requesterNames } = useUserNames(requesterIds);

  const applyFilters = () => {
    const v = form.getFieldsValue();
    const patch = {
      // 백엔드는 KST wall-clock(타임존 없는 값)으로 SCHEDULED_DT 를 비교하므로
      // toISOString()(UTC Z)이 아니라 같은 wall-clock 문자열로 보낸다 (datetime.ts BACKEND_DT_FMT 참고).
      from:     toBackendDateTime(v.range?.[0]),
      to:       toBackendDateTime(v.range?.[1]),
      status:   v.status,
      domain:   v.domain,
      brand:    v.brand,
      testMode: v.testMode,
    };
    // 조건이 그대로면 URL 이 바뀌지 않아 쿼리가 다시 나가지 않으므로 직접 다시 부른다. testMode 는 없으면 'ALL'.
    const same = (k: string, val?: string) => (k === 'testMode' ? (val || 'ALL') : (val || undefined));
    const unchanged = url.page === 1
      && Object.entries(patch).every(([k, val]) => same(k, val) === same(k, url.get(k)));
    url.setFilters(patch);
    if (unchanged) void refetch();
  };

  const resetAll = () => {
    form.resetFields();
    form.setFieldValue('testMode', 'ALL');
    url.clearFilters(FILTER_KEYS);
  };

  return (
    <PageShell title="Job Logs">
      <ListScreen<JobLog, Filters>
        actions={{
          items: [
            { key: 'refresh', label: '새로고침', icon: <ReloadOutlined />, onClick: () => refetch() },
          ],
        }}
        loadError={error ? { what: 'Job Logs', error, onRetry: () => void refetch() } : undefined}
        filter={{
          form,
          initialValues: filters,
          onFinish: applyFilters,
          onReset: resetAll,
          fields: [
            {
              name: 'range', label: 'Scheduled', size: 'xwide',
              node: <DatePicker.RangePicker inputReadOnly={isPhone} showTime />,
            },
            { name: 'status', label: 'Status', node: <JobStatusSelect /> },
            { name: 'domain', label: 'Domain', node: <DomainCodeSelect allowClear /> },
            { name: 'brand', label: 'Brand', node: <BrandCodeSelect allowClear /> },
            {
              name: 'testMode', label: 'Mode',
              node: (
                <Segmented<TestModeFilter>
                  options={[
                    { label: '운영',   value: 'N' },
                    { label: '전체',   value: 'ALL' },
                    { label: '테스트', value: 'Y' },
                  ]}
                />
              ),
              // 'ALL' 은 조건 없음이라 칩이 없다. 비우면 고른 것 없는 Segmented 가 되므로 칩 × 도 없다.
              clearable: false,
              format: (v) => (v === 'N' ? '운영' : v === 'Y' ? '테스트' : null),
            },
          ],
        }}
        table={{
          rowKey: 'logId',
          viewKey: 'scheduler.logs',
          loading: isFetching,
          dataSource: data?.items,
          pagination: {
            current: url.page,
            pageSize: url.size,
            total: data?.pagination.total ?? 0,
            showSizeChanger: true,
            pageSizeOptions: [20, 50, 100, 200],
            onChange: url.setPaging,
          },
          columns: [
            {
              title: <HeaderWithTip label="Log ID" tip={FIELD_DOCS.jobLog.logId} />,
              dataIndex: 'logId',
              fixed: 'left',
              width: CONTAINER.fixedColMd,
              render: (v: string, row) => (
                <LogIdCell id={v} onClick={() => detail.openItem(row)} />
              ),
            },
            {
              title: <HeaderWithTip label="Scheduled" tip={FIELD_DOCS.jobLog.scheduled} />,
              dataIndex: 'scheduledDt',
              render: (v) => formatDateTime(v),
            },
            {
              title: <HeaderWithTip label="Domain" tip={FIELD_DOCS.jobLog.domain} />,
              dataIndex: 'domainCode',
            },
            {
              title: <HeaderWithTip label="Brand" tip={FIELD_DOCS.jobLog.brand} />,
              dataIndex: 'brandCode',
            },
            {
              title: <HeaderWithTip label="Center" tip={FIELD_DOCS.jobLog.center} />,
              dataIndex: 'centerCode', priority: 3,
            },
            {
              title: <HeaderWithTip label="Status" tip={FIELD_DOCS.jobLog.status} />,
              dataIndex: 'status', priority: 1,
              render: (v: JobStatus) => <JobStatusTag value={v} />,
            },
            {
              title: <HeaderWithTip label="Mode" tip={FIELD_DOCS.jobLog.mode} />,
              dataIndex: 'testYn', align: 'center',
              render: (v: 'Y' | 'N') => <TestModeTag value={v} size="sm" />,
            },
            {
              title: <HeaderWithTip label="Requester" tip={FIELD_DOCS.jobLog.requester} />,
              dataIndex: 'requester', priority: 3,
              render: (v?: string | null) => {
                if (!v) return '-';
                if (v === 'Scheduler' || v === 'unknown') return <LabelTag>{v}</LabelTag>;
                const name = requesterNames?.get(v);
                // 매핑 성공 → 이름만 표시 (id 는 tooltip). 실패 (계정 삭제 등) → ID fallback.
                return name
                  ? <Tooltip title={v}><span>{name}</span></Tooltip>
                  : v;
              },
            },
            {
              title: <HeaderWithTip label="Started" tip={FIELD_DOCS.jobLog.started} />,
              dataIndex: 'startedDt', priority: 3,
              render: (v) => formatDateTime(v),
            },
            {
              title: <HeaderWithTip label="Finished" tip={FIELD_DOCS.jobLog.finished} />,
              dataIndex: 'finishedDt', priority: 3,
              render: (v) => formatDateTime(v),
            },
            {
              title: <HeaderWithTip label="Hangfire" tip={FIELD_DOCS.jobLog.hangfire} />,
              dataIndex: 'hangfireJobId', priority: 3,
            },
            {
              title: <HeaderWithTip label="Message" tip={FIELD_DOCS.jobLog.message} />,
              dataIndex: 'errorMessage', ellipsis: true, priority: 3,
            },
          ],
        }}
      />
      <JobLogDetailModal log={detail.selected ?? detailLog ?? null} onClose={detail.close} />
    </PageShell>
  );
}

export const meta = definePage({
  key:         'scheduler.logs',
  pathPattern: '/scheduler/logs',
});
