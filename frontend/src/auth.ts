import { useSyncExternalStore } from 'react';

export type Role = 'admin' | 'analyst' | 'viewer';

export interface Session {
  token: string;
  expiresAt: string;
  user: { id: string; username: string; role: Role };
}

const KEY = 'settleflow.session';
const listeners = new Set<() => void>();

/** sessionStorage: the token disappears when the tab is closed and is never shared across tabs. */
function read(): Session | null {
  try {
    const s = JSON.parse(sessionStorage.getItem(KEY) ?? 'null') as Session | null;
    return s && new Date(s.expiresAt).getTime() > Date.now() ? s : null;
  } catch {
    return null;
  }
}

let current = read();

export const session = {
  get: () => current,
  set(s: Session | null) {
    current = s;
    if (s) sessionStorage.setItem(KEY, JSON.stringify(s));
    else sessionStorage.removeItem(KEY);
    listeners.forEach(l => l());
  },
  subscribe(l: () => void) {
    listeners.add(l);
    return () => listeners.delete(l);
  },
};

export function useSession() {
  return useSyncExternalStore(session.subscribe, session.get);
}

// Role checks live in permissions.ts (pure, unit-tested).
export { can } from './permissions';
