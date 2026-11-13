import { useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { ReloadOutlined } from '@ant-design/icons';
import { Tooltip, Typography } from 'antd';
import dayjs from 'dayjs';
import { MasterDetailScreen, PageShell, Stack, definePage } from '../../../common/layout';
import { Caption, DataTable, LoadError, PageHelp, ScreenCard, ScreenTabs, SimpleEmpty } from '../../../common/components';
import type { DataColumn } from '../../../common/components';
import { BrandPicker, DomainPicker } from '../components/RunBrowserPickers';
import {
  useNextRunBrands, useNextRunBrandsForDomain,
  useNextRunDomains, useNextRunDomainsForBrand, useNextRunUpcoming,
} from '../api/hooks';
import type { UpcomingRun } from '../api/types';
import { describeCron } from '../../../common/utils/cron';
import { DATETIME_FMT } from '../../../common/utils/datetime';
import { FONT_SIZE } from '../../../common/constants/typography';

// 도메인×브랜드 "다음 실행 예정" 브라우저. 두 진입점을 탭으로 제공:
//   - 브랜드 기준: 브랜드 → (그 브랜드의) 도메인 → 다음 실행 예정 Top 20
//   - 도메인 기준: 도메인 → (그 도메인의) 브랜드 → 다음 실행 예정 Top 20
// 발사 시각은 대시보드 "다음 실행 예정" 과 동일한 백엔드 Cronos 로직(KST, 활성 후보만).
export default function NextRunsPage() {
  const [tab, setTab] = useState('brand');
  const qc = useQueryClient();
  return (
    <PageShell title="다음 실행 예정">
      <PageHelp title="다음 실행 예정 화면 안내">
        <Typography.Paragraph>
          <b>브랜드 기준</b> — 브랜드를 고르면 그 브랜드에 설정된 도메인이 나오고, 도메인을 고르면 해당 브랜드·도메인의 다음 실행
          예정(Top 20)을 보여줍니다. 도메인을 고르기 전에는 그 브랜드 전체의 가장 가까운 20건을 보여줍니다.
        </Typography.Paragraph>
        <Typography.Paragraph>
          <b>도메인 기준</b> — 도메인을 고르면 그 도메인에 override 된 브랜드가 나오고, 브랜드를 고르면 해당 도메인·브랜드의 다음 실행
          예정(Top 20)을 보여줍니다. 브랜드를 고르기 전에는 그 도메인 전체의 가장 가까운 20건을 보여줍니다.
        </Typography.Paragraph>
      </PageHelp>
      <ScreenCard
        fill
        actions={{
          items: [
            { key: 'refresh', label: '새로고침', icon: <ReloadOutlined />, onClick: () => qc.invalidateQueries({ queryKey: ['next-runs'] }) },
          ],
        }}
      >
        <ScreenTabs
          activeKey={tab}
          onChange={setTab}
          items={[
            { key: 'brand',  label: '브랜드 기준', children: <BrandFirstView /> },
            { key: 'domain', label: '도메인 기준', children: <DomainFirstView /> },
          ]}
        />
      </ScreenCard>
    </PageShell>
  );
}

// 브랜드 → 도메인 → 시각. 브랜드를 바꾸면 도메인 선택은 해제(우측은 그 브랜드 전체 도메인 합산).
function BrandFirstView() {
  const [brand, setBrand]   = useState<string>();
  const [domain, setDomain] = useState<string>();
  const brands  = useNextRunBrands();
  const domains = useNextRunDomainsForBrand(brand);

  return (
    <Stack fill>
      <MasterDetailScreen
        panes={[
          {
            key: 'brand',
            title: '브랜드',
            weight: 'narrow',
            selected: !!brand,
            onClear: () => { setBrand(undefined); setDomain(undefined); },
            content: (
              <BrandPicker
                title="브랜드"
                data={brands.data}
                error={brands.error}
                onRetry={() => void brands.refetch()}
                loading={brands.isLoading}
                showCount
                selected={brand}
                onSelect={(c) => { setBrand(c); setDomain(undefined); }}
                emptyText="발사 후보 브랜드가 없습니다"
              />
            ),
          },
          {
            key: 'domain',
            title: '도메인',
            weight: 'narrow',
            selected: !!domain,
            onClear: () => setDomain(undefined),
            // 도메인을 고르지 않아도 그 브랜드 전체의 예정이 뜻을 가진다.
            skipLabel: '전체 도메인 보기',
            content: (
              <DomainPicker
                title="도메인"
                data={domains.data}
                error={domains.error}
                onRetry={() => void domains.refetch()}
                loading={!!brand && domains.isLoading}
                selected={domain}
                onSelect={(c) => setDomain(c === domain ? undefined : c)}
                emptyText={brand ? 'override 도메인이 없습니다' : '왼쪽에서 브랜드를 선택하세요'}
              />
            ),
          },
          {
            key: 'runs',
            title: '다음 실행 예정',
            weight: 'wide',
            content: <RunsPanel brand={brand} domain={domain} placeholder="브랜드를 선택하면 다음 실행 예정이 표시됩니다" />,
          },
        ]}
      />
    </Stack>
  );
}

// 도메인 → 브랜드 → 시각. 도메인을 바꾸면 브랜드 선택은 해제(우측은 그 도메인 전체 브랜드 합산).
function DomainFirstView() {
  const [domain, setDomain] = useState<string>();
  const [brand, setBrand]   = useState<string>();
  const domains = useNextRunDomains();
  const brands  = useNextRunBrandsForDomain(domain);

  return (
    <Stack fill>
      <MasterDetailScreen
        panes={[
          {
            key: 'domain',
            title: '도메인',
            weight: 'narrow',
            selected: !!domain,
            onClear: () => { setDomain(undefined); setBrand(undefined); },
            content: (
              <DomainPicker
                title="도메인"
                data={domains.data}
                error={domains.error}
                onRetry={() => void domains.refetch()}
                loading={domains.isLoading}
                showCount
                selected={domain}
                onSelect={(c) => { setDomain(c); setBrand(undefined); }}
                emptyText="발사 후보 도메인이 없습니다"
              />
            ),
          },
          {
            key: 'brand',
            title: '브랜드',
            weight: 'narrow',
            selected: !!brand,
            onClear: () => setBrand(undefined),
            // 브랜드를 고르지 않아도 그 도메인 전체의 예정이 뜻을 가진다.
            skipLabel: '전체 브랜드 보기',
            content: (
              <BrandPicker
                title="브랜드"
                data={brands.data}
                error={brands.error}
                onRetry={() => void brands.refetch()}
                loading={!!domain && brands.isLoading}
                selected={brand}
                onSelect={(c) => setBrand(c === brand ? undefined : c)}
                emptyText={domain ? 'override 브랜드가 없습니다' : '왼쪽에서 도메인을 선택하세요'}
              />
            ),
          },
          {
            key: 'runs',
            title: '다음 실행 예정',
            weight: 'wide',
            content: <RunsPanel brand={brand} domain={domain} placeholder="도메인을 선택하면 다음 실행 예정이 표시됩니다" />,
          },
        ]}
      />
    </Stack>
  );
}

// 다음 실행 예정 Top 20. 고정되지 않은 축(brand/domain)을 컬럼으로 보여준다 —
// 브랜드 기준 탭은 brand 가 고정되므로 Domain 컬럼만, 도메인 기준 탭은 그 반대.
function RunsPanel({ brand, domain, placeholder }: {
  brand?: string;
  domain?: string;
  placeholder: string;
}) {
  const runs = useNextRunUpcoming(brand, domain);
  const active = brand || domain;

  const columns: DataColumn<UpcomingRun>[] = [
    {
      title: '예정 시각', dataIndex: 'nextDt', width: 150,
      render: (v: string) => {
        const next = dayjs(v);
        return (
          <Tooltip title={next.format(DATETIME_FMT)}>
            {next.format('HH:mm:ss')}{' '}
            <Caption size="xxs">({formatRelative(next)})</Caption>
          </Tooltip>
        );
      },
    },
    ...(brand ? [] : [{
      title: 'Brand', dataIndex: 'brandCode', width: 100, priority: 1,
      render: (v: string) => <span style={{ overflowWrap: 'anywhere' }}>{v}</span>,
    } as DataColumn<UpcomingRun>]),
    ...(domain ? [] : [{
      title: 'Domain', dataIndex: 'domainCode', width: 150,
      render: (v: string, r: UpcomingRun) =>
        r.remark
          ? <Tooltip title={r.remark}><span style={{ overflowWrap: 'anywhere' }}>{v}</span></Tooltip>
          : <span style={{ overflowWrap: 'anywhere' }}>{v}</span>,
    } as DataColumn<UpcomingRun>]),
    {
      title: 'Cron', dataIndex: 'cronExpr', width: 180,
      render: (v: string) => (
        <Tooltip title={v}><span style={{ fontSize: FONT_SIZE.xs }}>{describeCron(v)}</span></Tooltip>
      ),
    },
    {
      title: 'Queue', dataIndex: 'queue', width: 120, priority: 3,
      render: (v: string) => <span style={{ fontSize: FONT_SIZE.xs }}>{v}</span>,
    },
  ];

  const title = !active
    ? '다음 실행 예정'
    : brand && domain
      ? `다음 실행 예정 — ${brand} / ${domain}`
      : brand
        ? `다음 실행 예정 — ${brand} (전체 도메인)`
        : `다음 실행 예정 — ${domain} (전체 브랜드)`;

  return (
    <ScreenCard
      fill
      title={title}
      extra={<Caption size="xs">Top 20 · KST</Caption>}
    >
      {!active ? (
        // fill body(flex 컬럼) 안의 유일 자식이라 기본 flex-start 로 상단에 붙는다 →
        // 채워진 표 상태와 맞춰 남은 높이를 채우고 가운데 정렬.
        <div style={{ flex: 1, minHeight: 0, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
          <SimpleEmpty description={placeholder} />
        </div>
      ) : runs.error ? (
        <LoadError what="다음 실행 예정" error={runs.error} onRetry={() => void runs.refetch()} />
      ) : (
        <DataTable<UpcomingRun>
          rowKey={(r) => `${r.domainCode}/${r.brandCode}/${r.nextDt}`}
          fill
          viewKey="scheduler.next-runs.runs"
          loading={runs.isLoading || runs.isFetching}
          dataSource={runs.data}
          pagination={false}
          columns={columns}
          tableProps={{ locale: { emptyText: <SimpleEmpty description="예정된 자동 실행이 없습니다" /> } }}
        />
      )}
    </ScreenCard>
  );
}

// 다음 실행까지 남은 시간 짧은 표기 (대시보드 UpcomingRunsCard 와 동일 규칙).
function formatRelative(next: dayjs.Dayjs): string {
  const sec = next.diff(dayjs(), 'second');
  if (sec <= 0)     return 'now';
  if (sec < 60)     return `+${sec}s`;
  if (sec < 3600)   return `+${Math.floor(sec / 60)}m`;
  if (sec < 86400)  return `+${Math.floor(sec / 3600)}h`;
  return `+${Math.floor(sec / 86400)}d`;
}

export const meta = definePage({
  key:         'scheduler.next-runs',
  pathPattern: '/scheduler/next-runs',
});
