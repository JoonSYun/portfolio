// [담당업무 2] 감사 로그 — 실패해도 본 요청으로 전파하지 않는다.

import { supabase } from './supabase.ts';

export interface AuditEntry {
  user_id?: string;
  operation: 'INSERT' | 'UPDATE' | 'DELETE' | 'R2_PRESIGN' | 'R2_DELETE' | 'R2_CLEANUP' | 'SUBSCRIPTION_VERIFY' | 'SUBSCRIPTION_WEBHOOK';
  source: string;
  record_id: string;
  parent_id?: string;
  metadata?: Record<string, unknown>;
}

/** 단건 audit log INSERT. 실패 시 console.error만 출력 (호출자 에러 전파 없음). */
export async function writeAuditLog(entry: AuditEntry): Promise<void> {
  const { error } = await supabase.from('audit_log').insert(entry);
  if (error) {
    console.error('audit_log insert failed:', error.message);
  }
}

/** 다건 audit log batch INSERT. 실패 시 console.error만 출력. */
export async function writeAuditLogs(entries: AuditEntry[]): Promise<void> {
  if (entries.length === 0) return;
  const { error } = await supabase.from('audit_log').insert(entries);
  if (error) {
    console.error('audit_log batch insert failed:', error.message);
  }
}
