import { session, type Session } from '../auth';
import type { AuditEntry, DeadLetter, Merchant, NewPayment, Notification, Page, Payment, Stats, TimelineEntry, User } from '../types';

/** Empty by default: requests go to the same origin and are proxied (nginx in Docker, Vite in dev). */
const BASE = (import.meta.env.VITE_API_URL as string | undefined)?.replace(/\/$/, '') ?? '';

export class ApiError extends Error {
  constructor(public status: number, message: string, public details?: Record<string, string[]>) {
    super(message);
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const token = session.get()?.token;
  const res = await fetch(`${BASE}${path}`, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...init?.headers,
    },
  });

  // Token expired or revoked: drop the session, which sends the user back to the login screen.
  if (res.status === 401 && token && !path.startsWith('/api/auth/login')) session.set(null);

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
    if (res.status === 403) message = 'You do not have permission to do that.';
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

  review: (id: string, decision: 'APPROVE' | 'REJECT', note?: string) =>
    request<Payment>(`/api/payments/${id}/review`, {
      method: 'POST',
      body: JSON.stringify({ decision, note }),
    }),

  notifications: (take = 15) => request<Notification[]>(`/api/notifications?take=${take}`),

  deadLetters: (includeReplayed = false) => request<DeadLetter[]>(`/api/dead-letters?includeReplayed=${includeReplayed}`),

  replayDeadLetter: (id: string) => request<{ replayed: string }>(`/api/dead-letters/${id}/replay`, { method: 'POST' }),

  login: (username: string, password: string) =>
    request<Session>('/api/auth/login', { method: 'POST', body: JSON.stringify({ username, password }) }),

  merchants: () => request<Merchant[]>('/api/merchants'),
  createMerchant: (id: string, name: string) =>
    request<{ id: string; apiKey: string; apiKeyPrefix: string }>('/api/merchants', { method: 'POST', body: JSON.stringify({ id, name }) }),
  rotateMerchantKey: (id: string) =>
    request<{ id: string; apiKey: string; apiKeyPrefix: string }>(`/api/merchants/${id}/rotate-key`, { method: 'POST' }),
  setMerchantDisabled: (id: string, disabled: boolean) =>
    request<void>(`/api/merchants/${id}/${disabled ? 'disable' : 'enable'}`, { method: 'POST' }),

  users: () => request<User[]>('/api/users'),
  createUser: (username: string, password: string, role: string) =>
    request<User>('/api/users', { method: 'POST', body: JSON.stringify({ username, password, role }) }),
  setUserDisabled: (id: string, disabled: boolean) =>
    request<void>(`/api/users/${id}/${disabled ? 'disable' : 'enable'}`, { method: 'POST' }),

  audit: (params: { actor?: string; action?: string; targetId?: string } = {}) => {
    const qs = new URLSearchParams({ take: '200' });
    Object.entries(params).forEach(([k, v]) => v && qs.set(k, v));
    return request<AuditEntry[]>(`/api/audit?${qs}`);
  },
};
