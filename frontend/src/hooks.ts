import { useCallback, useEffect, useRef, useState } from 'react';
import { api } from './api/client';

/**
 * Fetches data immediately and then every `intervalMs` while the tab is visible.
 * Keeps showing the last good data if a refresh fails.
 */
export function usePolling<T>(fetcher: () => Promise<T>, intervalMs: number, deps: unknown[] = []) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const fetcherRef = useRef(fetcher);
  fetcherRef.current = fetcher;

  const refresh = useCallback(async () => {
    try {
      setData(await fetcherRef.current());
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    setLoading(true);
    refresh();
    const id = setInterval(() => {
      if (document.visibilityState === 'visible') refresh();
    }, intervalMs);
    return () => clearInterval(id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [intervalMs, refresh, ...deps]);

  return { data, error, loading, refresh };
}

export function useHashRoute(): [string, (route: string) => void] {
  const read = () => window.location.hash.replace(/^#\/?/, '') || 'overview';
  const [route, setRoute] = useState(read);
  useEffect(() => {
    const onChange = () => setRoute(read());
    window.addEventListener('hashchange', onChange);
    return () => window.removeEventListener('hashchange', onChange);
  }, []);
  return [route, (r: string) => { window.location.hash = `/${r}`; }];
}

export const formatMoney = (amount: number, currency: string) => {
  try {
    return new Intl.NumberFormat(undefined, { style: 'currency', currency }).format(amount);
  } catch {
    return `${currency} ${amount.toFixed(2)}`;
  }
};

export const timeAgo = (iso: string) => {
  const s = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
  if (s < 60) return `${s}s ago`;
  if (s < 3600) return `${Math.floor(s / 60)}m ago`;
  if (s < 86400) return `${Math.floor(s / 3600)}h ago`;
  return new Date(iso).toLocaleDateString();
};

export type LiveState = 'connecting' | 'live' | 'offline';

/**
 * Subscribes to the server's payment change stream (Server-Sent Events) and calls onChange,
 * debounced, whenever payments change. Stream tokens live 60s, so every (re)connect fetches a fresh one.
 */
export function useLiveStream(onChange: () => void, debounceMs = 300): LiveState {
  const [state, setState] = useState<LiveState>('connecting');
  const onChangeRef = useRef(onChange);
  onChangeRef.current = onChange;

  useEffect(() => {
    let source: EventSource | null = null;
    let retry: ReturnType<typeof setTimeout> | undefined;
    let debounce: ReturnType<typeof setTimeout> | undefined;
    let stopped = false;

    const connect = async () => {
      setState('connecting');
      try {
        const { token } = await api.streamToken();
        if (stopped) return;
        source = new EventSource(api.streamUrl(token));
        source.addEventListener('hello', () => setState('live'));
        source.addEventListener('payment', () => {
          clearTimeout(debounce);
          debounce = setTimeout(() => onChangeRef.current(), debounceMs);
        });
        source.onerror = () => {
          // Don't let EventSource retry with the (soon expired) token: reconnect ourselves.
          source?.close();
          setState('offline');
          if (!stopped) retry = setTimeout(connect, 3000);
        };
      } catch {
        setState('offline');
        if (!stopped) retry = setTimeout(connect, 5000);
      }
    };

    connect();
    return () => {
      stopped = true;
      source?.close();
      clearTimeout(retry);
      clearTimeout(debounce);
    };
  }, [debounceMs]);

  return state;
}
