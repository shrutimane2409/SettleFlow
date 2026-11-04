import { useState } from 'react';
import { Send, Zap } from 'lucide-react';
import { api } from '../api/client';
import type { NewPayment } from '../types';

const SCENARIOS: { label: string; hint: string; payment: Partial<NewPayment> }[] = [
  { label: 'Low risk', hint: 'Approved → settled', payment: { amount: 1249.5, country: 'IE', merchantId: 'merchant_demo' } },
  { label: 'High amount', hint: 'Held for manual review', payment: { amount: 6000, country: 'DE', merchantId: 'merchant_demo' } },
  { label: 'Risky', hint: 'High amount + country → declined', payment: { amount: 7500, country: 'US', merchantId: 'merchant_demo' } },
  { label: 'Blocked merchant', hint: 'Acquirer declines settlement', payment: { amount: 88.4, country: 'FR', merchantId: 'blocked_shop' } },
  { label: 'Acquirer outage', hint: 'Retries, circuit breaker opens, then dead-letter queue', payment: { amount: 42.1, country: 'NL', merchantId: 'merchant_outage' } },
  { label: 'Slow acquirer', hint: 'Times out after 2s; circuit breaker trips', payment: { amount: 19.99, country: 'IE', merchantId: 'merchant_slow' } },
  { label: 'Manual capture', hint: 'Authorized only: capture or void it from the payment drawer', payment: { amount: 320.75, country: 'IE', merchantId: 'merchant_demo', captureMode: 'manual' } },
];

const FIELDS: { key: Exclude<keyof NewPayment, 'captureMode'>; label: string }[] = [
  { key: 'amount', label: 'Amount' },
  { key: 'currency', label: 'Currency' },
  { key: 'country', label: 'Country' },
  { key: 'customerId', label: 'Customer' },
  { key: 'merchantId', label: 'Merchant' },
];

export function PaymentForm({ onCreated }: { onCreated: () => void }) {
  const [form, setForm] = useState({ amount: '1249.50', currency: 'EUR', customerId: 'cust_demo', merchantId: 'merchant_demo', country: 'IE' });
  const [manual, setManual] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ tone: 'ok' | 'error'; text: string } | null>(null);

  const toPayment = (): NewPayment => ({ ...form, amount: Number(form.amount), captureMode: manual ? 'manual' : 'automatic' });

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setMessage(null);
    try {
      const p = await api.createPayment(toPayment());
      setMessage({ tone: 'ok', text: `Accepted ${p.id.slice(0, 8)}…` });
      onCreated();
    } catch (err) {
      setMessage({ tone: 'error', text: err instanceof Error ? err.message : String(err) });
    } finally {
      setBusy(false);
    }
  };

  /** Fires 10 payments from distinct customers to show the pipeline under load. */
  const burst = async () => {
    setBusy(true);
    setMessage(null);
    const results = await Promise.allSettled(
      Array.from({ length: 10 }, (_, i) =>
        api.createPayment({
          ...toPayment(),
          customerId: `cust_burst_${Date.now().toString(36)}_${i}`,
          amount: Math.round((50 + Math.random() * 9000) * 100) / 100,
          country: ['IE', 'DE', 'FR', 'US', 'BR', 'NL'][i % 6],
        })),
    );
    const ok = results.filter(r => r.status === 'fulfilled').length;
    setMessage({ tone: ok === 10 ? 'ok' : 'error', text: `Burst: ${ok}/10 accepted` });
    setBusy(false);
    onCreated();
  };

  return (
    <form onSubmit={submit}>
      <div className="scenarios">
        {SCENARIOS.map(s => (
          <button
            type="button"
            key={s.label}
            title={s.hint}
            onClick={() => {
              const { captureMode, ...rest } = s.payment;
              setManual(captureMode === 'manual');
              setForm(f => ({ ...f, ...rest, amount: String(rest.amount ?? f.amount) }));
            }}
          >
            {s.label}
          </button>
        ))}
      </div>
      <div className="fields">
        {FIELDS.map(({ key, label }) => (
          <label key={key} className={key === 'customerId' || key === 'merchantId' ? 'span2' : ''}>
            {label}
            <input value={form[key]} onChange={e => setForm({ ...form, [key]: e.target.value })} />
          </label>
        ))}
      </div>
      <label className="toggle"><input type="checkbox" checked={manual} onChange={e => setManual(e.target.checked)} /> Manual capture (authorize only)</label>
      <div className="form-actions">
        <button className="primary" disabled={busy}><Send size={15} /> Submit payment</button>
        <button type="button" onClick={burst} disabled={busy} title="Send 10 random payments"><Zap size={15} /> Burst ×10</button>
      </div>
      {message && <p className={`form-msg ${message.tone}`}>{message.text}</p>}
    </form>
  );
}
