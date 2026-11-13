import { useEffect, useState } from 'react';
import { Alert, Radio, Typography } from 'antd';
import { CaretRightOutlined } from '@ant-design/icons';
import type { RunAsMode } from '../api/types';
import { CrudModal, MonoText, Caption, StatusTag } from '../../../common/components';
import { Inline, Stack } from '../../../common/layout';
import { COLORS } from '../../../common/constants/colors';

const { Text } = Typography;

// 테스트 강제 출처 — 경고 색으로 강조.
const SOURCE_COLOR = { source: COLORS.statusWarning };

type Props = {
  open: boolean;
  /** 표시용 — 모달 헤더와 본문에 어떤 (Domain, Brand) 인지 보여준다. */
  domainCode: string;
  /** null 이면 도메인 fan-out 모드 (DomainListPage 의 Run domain now). */
  brandCode?: string | null;
  /**
   * DB 측 effectiveIsTest. true 면 라디오는 잠기고 "강제 테스트" 안내를 보여준다.
   * 도메인 fan-out 케이스는 보통 false 로 보내되, 일부 브랜드만 test 일 수 있다는 안내를 별도로 표기.
   */
  effectiveIsTest: boolean;
  /** effectiveIsTest=true 일 때 표시할 출처 ("domain", "brand", "group:ERP" 등). */
  testSource: string[];
  /** 처리 중 상태 — Modal 의 OK 로딩 표시. */
  pending: boolean;
  /** "[Run]" 클릭 시 호출. 부모가 mutateAsync 를 감싸 호출하고 try/finally 로 모달을 닫음. */
  onSubmit: (runAs: RunAsMode) => void;
  onCancel: () => void;
};

/**
 * 직접실행 모달 — Popconfirm 자리에 들어가는 1-step 모달.
 *
 * 동작:
 *   - DB IsTest=N : Radio 활성. 사용자가 '운영'/'테스트' 중 선택 후 Run.
 *   - DB IsTest=Y : Radio 비활성, '테스트' 강제 + 출처 표시. Run 버튼 라벨도 'Run as Test'.
 *   - 라디오 기본값 : DB IsTest=Y 면 'test', N 이면 'operational' (안전 default — 의도치 않은 test 실행 방지).
 */
export default function RunNowModal({
  open, domainCode, brandCode,
  effectiveIsTest, testSource, pending,
  onSubmit, onCancel,
}: Props) {
  const [runAs, setRunAs] = useState<RunAsMode>(effectiveIsTest ? 'test' : 'operational');

  // open 토글 시 default 를 다시 계산 — 이전 선택이 다음 (Domain,Brand) 로 새지 않도록.
  useEffect(() => {
    if (open) setRunAs(effectiveIsTest ? 'test' : 'operational');
  }, [open, effectiveIsTest]);

  const target = brandCode ? `${domainCode} / ${brandCode}` : domainCode;
  const isFanOut = !brandCode;

  const sourcesLabel = formatTestSource(testSource);

  // 스케줄러는 환경을 고르지 않는 관리자 설정이라 운영 표시를 붙이지 않는다. 테스트 실행도 빨강이 아니다.
  return (
    <CrudModal
      title={
        <Inline>
          <CaretRightOutlined />
          <span>직접실행 — {target}</span>
        </Inline>
      }
      open={open}
      okText={runAs === 'test' ? '테스트 실행' : '실행'}
      confirmLoading={pending}
      onOk={() => onSubmit(runAs)}
      onCancel={onCancel}
    >
      <Stack gap="xl">
        <Stack gap="xs" align="start">
          <Caption size="base">대상</Caption>
          <MonoText>{target}</MonoText>
        </Stack>

        {effectiveIsTest ? (
          <Alert
            type="warning"
            showIcon
            title="테스트 모드 강제"
            description={
              <Stack gap="sm" align="start">
                <Inline gap="xs">
                  출처: <StatusTag mode="custom" value="source" colorMap={SOURCE_COLOR} label={sourcesLabel} />
                </Inline>
                <span>
                  ExecuteCoreAsync 가 실행되지 않습니다. 스케줄링/디스패치/페이로드까지의 파이프라인만 검증됩니다.
                </span>
              </Stack>
            }
          />
        ) : (
          <Radio.Group value={runAs} onChange={(e) => setRunAs(e.target.value as RunAsMode)}>
            <Stack gap="md" align="start">
              <Radio value="operational">
                <Inline gap="md">
                  <Text strong>운영으로 실행</Text>
                  <Caption size="base">비즈니스 로직 정상 수행</Caption>
                </Inline>
              </Radio>
              <Radio value="test">
                <Inline gap="md">
                  <Text strong>테스트로 실행</Text>
                  <Caption size="base">ExecuteCoreAsync 스킵 (dry-run)</Caption>
                </Inline>
              </Radio>
            </Stack>
          </Radio.Group>
        )}

        {isFanOut && (
          <Alert
            type="info"
            showIcon
            title="도메인 fan-out"
            description="이 도메인에 매핑된 모든 브랜드에 동일 모드가 적용됩니다. DB 측 TEST_YN='Y' 인 브랜드는 mode 와 무관하게 강제 테스트로 실행됩니다."
          />
        )}
      </Stack>
    </CrudModal>
  );
}

/** testSource 배열을 사용자에게 보여줄 한국어 라벨로 변환. */
export function formatTestSource(sources: string[]): string {
  if (sources.length === 0) return '';
  const parts: string[] = [];
  const groups: string[] = [];
  for (const s of sources) {
    if (s === 'domain') parts.push('도메인');
    else if (s === 'brand') parts.push('브랜드');
    else if (s.startsWith('group:')) groups.push(s.slice(6));
  }
  if (groups.length > 0) parts.push(`그룹: ${groups.join(', ')}`);
  return parts.join(' · ');
}
