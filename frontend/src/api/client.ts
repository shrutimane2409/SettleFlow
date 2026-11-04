import type { DeadLetter, NewPayment, Notification, Page, Payment, Stats, TimelineEntry } from '../types';

/** Empty by default: requests go to the same origin and are proxied (nginx in Docker, Vite in dev). */
const BASE = (import.meta.env.VITE_API_URL as string | undefined)?.replace(/\/$/, '') ?? '';

export class ApiError extends Error {
  constructor(public status: number, message: string, public details?: Record<string, string[]>) {
    super(message);
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...init?.headers },
  });

  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    let details: Record<string, string[]> | undefined;
    try {
      const problem = await res.json();
      message = problem.detail ?? problem.title ?? message;
      details = problem.errors;
      if (details) message = Object.values(details).flat().join(' ');
    } catch { /* not JSON */ }
    if (res.status === 429) message = 'Rate limit reached: slow down a little.';
    throw new ApiError(res.status, message, details);
  }

  return res.status === 204 ? (undefined as T) : res.json();
}

export const api = {
  stats: () => request<Stats>('/api/stats'),

  payments: (params: { status?: string; q?: string; page?: number; pageSize?: number } = {}) => {
    const qs = new URLSearchParams();
    Object.entries(params).forEach(([k, v]) => v !== undefined && v !== '' && qs.set(k, String(v)));
    return request<Page<Payment>>(`/api/payments?${qs}`);
  },

  payment: (id: string) => request<Payment>(`/api/payments/${id}`),

  timeline: (id: string) => request<TimelineEntry[]>(`/api/payments/${id}/timeline`),

  createPayment: (body: NewPayment, idempotencyKey: string = crypto.randomUUID()) =>
    request<Payment>('/api/payments', {
      method: 'POST',
      headers: { 'Idempotency-Key': idempotencyKey },
      body: JSON.stringify(body),
    }),

  review: (id: string, decision: 'APPROVE' | 'REJECT', reviewer: string, note?: string) =>
    request<Payment>(`/api/payments/${id}/review`, {
      method: 'POST',
      body: JSON.stringify({ decision, reviewer, note }),
    }),

  notifications: (take = 15) => request<Notification[]>(`/api/notifications?take=${take}`),

  deadLetters: (includeReplayed = false) => request<DeadLetter[]>(`/api/dead-letters?includeReplayed=${includeReplayed}`),

  replayDeadLetter: (id: string) => request<{ replayed: string }>(`/api/dead-letters/${id}/replay`, { method: 'POST' }),
};
