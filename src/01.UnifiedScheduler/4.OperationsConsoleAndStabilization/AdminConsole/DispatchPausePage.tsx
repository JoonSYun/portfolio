import { useState } from 'react';
import { Alert, Input, Typography } from 'antd';
import { PauseCircleOutlined, PlayCircleOutlined } from '@ant-design/icons';
import dayjs from 'dayjs';
import { useDispatchPause, useSetDispatchPause } from '../api/hooks';
import { notify } from '../../../common/utils/messages';
import { PageShell, definePage, Inline, Stack } from '../../../common/layout';
import { Caption, LoadError, Loading, PageHelp, ScreenCard, StatusTag } from '../../../common/components';
import { COLORS } from '../../../common/constants/colors';

const STATE_COLORS = { PAUSED: COLORS.statusFailed, RUNNING: COLORS.statusSuccess };

/**
 * Dispatch 전역 일시정지 페이지.
 * - 일시정지 토글 시 Hangfire RecurringJob 의 cron 자동 발사가 Dispatcher.Fire 진입부에서 차단되어
 *   sch_job_log row 가 발생하지 않는다.
 * - 수동 실행("Run now") 은 본 가드의 영향을 받지 않으므로 점검 중에도 관리자가 직접 실행 가능.
 * - 상태는 솔루션 루트 dispatch-pause.json 에 영속되어 프로세스 재기동 후에도 유지된다.
 * - 일시정지·재개는 전체 자동 실행에 걸리므로 확인 창을 거친다.
 */
export default function DispatchPausePage() {
  const { data, isLoading, error, refetch } = useDispatchPause();
  const setMut               = useSetDispatchPause();
  const [reason, setReason]  = useState<string>('');

  const onPause = async () => {
    await setMut.mutateAsync({ isPaused: true, reason: reason.trim() || null });
    notify.success('Dispatch 를 일시정지했습니다.');
    setReason('');
  };

  const onResume = async () => {
    await setMut.mutateAsync({ isPaused: false });
    notify.success('Dispatch 를 재개했습니다.');
  };

  const state = data?.isPaused ? 'PAUSED' : 'RUNNING';

  return (
    <PageShell title="Dispatch 일시정지">
      <PageHelp title="Dispatch 일시정지 화면 안내">
        <Typography.Paragraph>
          스케줄러 실행을 전역으로 차단합니다.
          배포 전에 활성화 하여 전체 스케줄러 동작을 정지시킨 후 배포를 진행하세요.
          스케줄러 수동 실행은 영향을 받지 않습니다.
        </Typography.Paragraph>
      </PageHelp>

      <ScreenCard
        actions={data && {
          items: [
            data.isPaused
              ? {
                  key: 'resume', label: 'Dispatch 재개', icon: <PlayCircleOutlined />, primary: true,
                  loading: setMut.isPending, onClick: onResume,
                  confirm: { title: 'Dispatch 를 재개하시겠습니까?', content: '차단된 스케줄러 자동 실행이 다시 시작됩니다.' },
                }
              : {
                  key: 'pause', label: 'Dispatch 일시정지', icon: <PauseCircleOutlined />, primary: true, danger: true,
                  loading: setMut.isPending, onClick: onPause,
                  confirm: {
                    title:   'Dispatch 를 일시정지하시겠습니까?',
                    content: '스케줄러 자동 실행이 전역으로 차단됩니다. 스케줄러 수동 실행은 영향을 받지 않습니다.',
                  },
                },
          ],
        }}
      >
        {error ? (
          <LoadError what="Dispatch 상태" error={error} onRetry={() => void refetch()} />
        ) : isLoading || !data ? (
          <Loading />
        ) : (
          <Stack gap="xl">
            <Inline>
              <Typography.Text strong>현재 상태</Typography.Text>
              <StatusTag
                mode="custom"
                value={state}
                colorMap={STATE_COLORS}
                icon={data.isPaused ? <PauseCircleOutlined /> : <PlayCircleOutlined />}
              />
            </Inline>

            {data.isPaused ? (
              <Alert
                type="warning" showIcon
                title="Dispatch 자동 발사가 차단된 상태입니다."
                description={
                  <Stack gap="xxs" align="start">
                    <span>일시정지자: <b>{data.pausedBy ?? '-'}</b></span>
                    <span>일시정지 시각: <b>{data.pausedDt ? dayjs(data.pausedDt).format('YYYY-MM-DD HH:mm') : '-'}</b></span>
                    <span>사유: {data.reason ?? <Caption size="base">(없음)</Caption>}</span>
                  </Stack>
                }
              />
            ) : (
              // 폼 칸 하나라 Form 없이 — Form.Item 아래 여백이 카드 끝에 남지 않게.
              <Stack gap="xs">
                <Typography.Text>사유 (선택)</Typography.Text>
                <Input.TextArea
                  rows={3}
                  value={reason}
                  onChange={(e) => setReason(e.target.value)}
                  placeholder="예: 외부 시스템 점검으로 09:00~10:00 자동 발사 중단"
                  maxLength={500}
                  showCount
                />
                <Caption>다른 운영자에게 맥락 전달 / 감사 로그용</Caption>
              </Stack>
            )}
          </Stack>
        )}
      </ScreenCard>
    </PageShell>
  );
}

export const meta = definePage({
  key:         'scheduler.dispatchPause',
  pathPattern: '/scheduler/dispatch-pause',
});
