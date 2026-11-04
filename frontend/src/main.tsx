import { useState } from 'react';
import { createRoot } from 'react-dom/client';
import { Activity, CreditCard, Inbox, RefreshCw, ShieldAlert } from 'lucide-react';
import { api } from './api/client';
import { PaymentDrawer } from './components/PaymentDrawer';
import { useHashRoute, usePolling } from './hooks';
import { DeadLetters } from './pages/DeadLetters';
import { Overview } from './pages/Overview';
import { Payments } from './pages/Payments';
import './styles.css';

const NAV = [
  { route: 'overview', label: 'Overview', icon: Activity, title: 'Payment Operations', subtitle: 'Real-time event-driven settlement pipeline' },
  { route: 'payments', label: 'Payments', icon: CreditCard, title: 'Payments', subtitle: 'Search and inspect every payment' },
  { route: 'review', label: 'Review queue', icon: ShieldAlert, title: 'Review queue', subtitle: 'Payments the risk engine wants a human to decide' },
  { route: 'dlq', label: 'Dead letters', icon: Inbox, title: 'Dead letters', subtitle: 'Failed messages, ready to replay' },
];

function App() {
  const [route] = useHashRoute();
  const [selected, setSelected] = useState<string | null>(null);
  const [tick, setTick] = useState(0);
  const bump = () => setTick(t => t + 1);

  const { data: stats } = usePolling(api.stats, 4000, [tick]);
  const { data: health, error: healthError } = usePolling(
    () => fetch(`${import.meta.env.VITE_API_URL ?? ''}/health/ready`).then(r => r.json() as Promise<{ status: string }>), 5000);

  const current = NAV.find(n => n.route === route) ?? NAV[0];
  const healthy = !healthError && health?.status === 'Healthy';

  return (
    <div className="app">
      <aside>
        <div className="brand">
          <div className="logo">S</div>
          <div><b>SettleFlow</b><span>Payment Operations</span></div>
        </div>
        <nav>
          {NAV.map(({ route: r, label, icon: Icon }) => (
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
          <button onClick={bump}><RefreshCw size={16} /> Refresh</button>
        </header>

        {current.route === 'overview' && <Overview onSelect={setSelected} tick={tick} onCreated={bump} />}
        {current.route === 'payments' && <Payments onSelect={setSelected} tick={tick} />}
        {current.route === 'review' && <Payments onSelect={setSelected} tick={tick} fixedStatus="FraudReview" title="Awaiting manual review" />}
        {current.route === 'dlq' && <DeadLetters onSelect={setSelected} />}
      </main>

      {selected && <PaymentDrawer id={selected} onClose={() => setSelected(null)} onChanged={bump} />}
    </div>
  );
}

createRoot(document.getElementById('root')!).render(<App />);
