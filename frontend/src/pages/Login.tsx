import { useState } from 'react';
import { LogIn } from 'lucide-react';
import { api } from '../api/client';
import { session } from '../auth';

export function Login() {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      session.set(await api.login(username.trim(), password));
    } catch (err) {
      setError(err instanceof Error && err.message.includes('429')
        ? 'Too many attempts. Wait a minute and try again.'
        : 'Invalid username or password.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="login-page">
      <form className="login card" onSubmit={submit}>
        <div className="brand">
          <div className="logo">S</div>
          <div><b>SettleFlow</b><span>Payment Operations</span></div>
        </div>
        <h1>Sign in</h1>
        <label>Username<input autoFocus autoComplete="username" value={username} onChange={e => setUsername(e.target.value)} /></label>
        <label>Password<input type="password" autoComplete="current-password" value={password} onChange={e => setPassword(e.target.value)} /></label>
        <button className="primary" disabled={busy || !username || !password}><LogIn size={15} /> Sign in</button>
        {error && <p className="form-msg error">{error}</p>}
        {import.meta.env.VITE_HIDE_DEMO_LOGINS !== 'true' && <p className="muted small">
          Local demo accounts: <span className="mono">admin</span> / <span className="mono">admin-change-me</span>,{' '}
          <span className="mono">analyst</span> or <span className="mono">viewer</span> / <span className="mono">settleflow-demo</span>
        </p>}
      </form>
    </div>
  );
}
