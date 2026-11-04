import { useState } from 'react';
import { createRoot } from 'react-dom/client';
import { Activity, BarChart3, BookOpen, ShieldBan, CreditCard, FileClock, Inbox, Landmark, LogOut, RefreshCw, Scale, ShieldAlert, Store, Users as UsersIcon, Webhook } from 'lucide-react';
import { api } from './api/client';
import { can, session, useSession } from './auth';
import { PaymentDrawer } from './components/PaymentDrawer';
import { useHashRoute, useLiveStream, usePolling } from './hooks';
import { AuditLog, Merchants, Users } from './pages/Admin';
import { Analytics } from './pages/Analytics';
import { DeadLetters } from './pages/DeadLetters';
import { Fraud } from './pages/Fraud';
import { Ledger, Payouts, Reconciliation } from './pages/Finance';
import { Login } from './pages/Login';
import { Overview } from './pages/Overview';
import { Payments } from './pages/Payments';
import { Webhooks } from './pages/Webhooks';
import './styles.css';

const NAV = [
  { route: 'overview', label: 'Overview', icon: Activity, title: 'Payment Operations', subtitle: 'Real-time event-driven settlement pipeline' },
  { route: 'analytics', label: 'Analytics', icon: BarChart3, title: 'Analytics', subtitle: 'Volume, approval and fraud trends' },
  { route: 'payments', label: 'Payments', icon: CreditCard, title: 'Payments', subtitle: 'Search and inspect every payment' },
  { route: 'review', label: 'Review queue', icon: ShieldAlert, title: 'Review queue', subtitle: 'Payments the risk engine wants a human to decide' },
  { route: 'fraud', label: 'Fraud rules', icon: ShieldBan, title: 'Fraud rules', subtitle: 'Versioned rules, simulation, blocklist and chargeback feedback' },
  { route: 'dlq', label: 'Dead letters', icon: Inbox, title: 'Dead letters', subtitle: 'Failed messages, ready to replay' },
  { route: 'ledger', label: 'Ledger', icon: BookOpen, title: 'Ledger', subtitle: 'Double-entry books: every movement of money' },
  { route: 'payouts', label: 'Payouts', icon: Landmark, title: 'Payouts', subtitle: 'Settlement batches paid to merchants' },
  { route: 'reconciliation', label: 'Reconciliation', icon: Scale, title: 'Reconciliation', subtitle: 'SettleFlow vs acquirer vs ledger' },
  { route: 'webhooks', label: 'Webhooks', icon: Webhook, title: 'Webhooks', subtitle: 'Signed event deliveries to merchants' },
  { route: 'merchants', label: 'Merchants', icon: Store, title: 'Merchants', subtitle: 'Accounts and API keys', admin: true },
  { route: 'users', label: 'Users', icon: UsersIcon, title: 'Users', subtitle: 'Who can sign in, and what they can do', admin: true },
  { route: 'audit', label: 'Audit log', icon: FileClock, title: 'Audit log', subtitle: 'Every human and merchant action, append-only', admin: true },
];

function Root() {
  const s = useSession();
  return s ? <App /> : <Login />;
}

function App() {
  const me = useSession();
  const [route] = useHashRoute();
  const [selected, setSelected] = useState<string | null>(null);
  const [tick, setTick] = useState(0);
  const bump = () => setTick(t => t + 1);
  // Pushed updates: any payment change refreshes the current page immediately.
  const live = useLiveStream(bump);

  const { data: stats } = usePolling(api.stats, 4000, [tick]);
  const { data: health, error: healthError } = usePolling(
    () => fetch(`${import.meta.env.VITE_API_URL ?? ''}/health/ready`).then(r => r.json() as Promise<{ status: string }>), 5000);

  const nav = NAV.filter(n => !n.admin || can.admin(me));
  const current = nav.find(n => n.route === route) ?? nav[0];
  const healthy = !healthError && health?.status === 'Healthy';

  return (
    <div className="app">
      <aside>
        <div className="brand">
          <div className="logo">S</div>
          <div><b>SettleFlow</b><span>Payment Operations</span></div>
        </div>
        <nav>
          {nav.map(({ route: r, label, icon: Icon }) => (
            <a key={r} href={`#/${r}`} className={current.route === r ? 'active' : ''}>
              <Icon size={17} /> {label}
              {r === 'review' && !!stats?.reviewQueue && <em className="count warn">{stats.reviewQueue}</em>}
              {r === 'dlq' && !!stats?.pipeline.deadLetters && <em className="count bad">{stats.pipeline.deadLetters}</em>}
            </a>
          ))}
        </nav>
        <div className="side-links">
          <a href="http://localhost:8083/swagger" target="_blank" rel="noreferrer">API docs ↗</a>
          <a href="http://localhost:8082" target="_blank" rel="noreferrer">Kafka console ↗</a>
          <a href="http://localhost:16686" target="_blank" rel="noreferrer">Traces (Jaeger) ↗</a>
          <a href="http://localhost:3001" target="_blank" rel="noreferrer">Grafana ↗</a>
        </div>
        <div className="user-box">
          <div><b>{me?.user.username}</b><span>{me?.user.role}</span></div>
          <button title="Sign out" onClick={() => session.set(null)}><LogOut size={14} /></button>
        </div>
        <div className={`side-status ${healthy ? '' : 'down'}`}>
          <i /> {healthy ? 'All systems operational' : health ? `Degraded (${health.status})` : 'API unreachable'}
        </div>
      </aside>

      <main>
        <header>
          <div>
            <p className="eyebrow">CONTROL PLANE</p>
            <h1>{current.title}</h1>
            <p className="muted">{current.subtitle}</p>
          </div>
          <div className="header-actions">
            <span className={`live-badge ${live}`} title="Updates are pushed from the server over Server-Sent Events">
              <i /> {live === 'live' ? 'Live' : live === 'connecting' ? 'Connecting…' : 'Offline, retrying'}
            </span>
            <button onClick={bump}><RefreshCw size={16} /> Refresh</button>
          </div>
        </header>

        {current.route === 'overview' && <Overview onSelect={setSelected} tick={tick} onCreated={bump} />}
        {current.route === 'payments' && <Payments onSelect={setSelected} tick={tick} />}
        {current.route === 'analytics' && <Analytics />}
        {current.route === 'review' && <Payments onSelect={setSelected} tick={tick} fixedStatus="FraudReview" title="Awaiting manual review" />}
        {current.route === 'dlq' && <DeadLetters onSelect={setSelected} />}
        {current.route === 'fraud' && <Fraud onSelect={setSelected} />}
        {current.route === 'webhooks' && <Webhooks onSelect={setSelected} />}
        {current.route === 'ledger' && <Ledger onSelect={setSelected} />}
        {current.route === 'payouts' && <Payouts onSelect={setSelected} />}
        {current.route === 'reconciliation' && <Reconciliation onSelect={setSelected} />}
        {current.route === 'merchants' && <Merchants />}
        {current.route === 'users' && <Users />}
        {current.route === 'audit' && <AuditLog onSelect={setSelected} />}
      </main>

      {selected && <PaymentDrawer id={selected} onClose={() => setSelected(null)} onChanged={bump} />}
    </div>
  );
}

createRoot(document.getElementById('root')!).render(<Root />);
