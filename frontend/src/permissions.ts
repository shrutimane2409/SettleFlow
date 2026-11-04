import type { Session } from './auth';

/** What each role may do in the dashboard. Mirrors the API's policies (the API is the real gate). */
export const can = {
  review: (s: Session | null) => s?.user.role === 'admin' || s?.user.role === 'analyst',
  admin: (s: Session | null) => s?.user.role === 'admin',
  createPayments: (s: Session | null) => s?.user.role === 'admin' || s?.user.role === 'analyst',
};
