import { useState } from 'react';
import { NavLink, Route, Routes } from 'react-router-dom';
import { useQueryClient } from '@tanstack/react-query';
import { hasToken, setToken } from './api/client';
import { Overview } from './pages/Overview';
import { Exchanges } from './pages/Exchanges';
import { ExchangeDetail } from './pages/ExchangeDetail';
import { Runtimes } from './pages/Runtimes';

/**
 * The four Stage 1C screens (FR-DASH-003).
 *
 * The dashboard is a client of the documented `/api/v1` endpoints and nothing else: it holds no
 * database connection, no configuration file, and no knowledge of the store's shape (FR-DASH-002).
 * Everything it can show is something the gateway already publishes.
 */
export function App() {
  return (
    <div className="app">
      <header>
        <nav>
          <NavLink to="/" end>Overview</NavLink>
          <NavLink to="/exchanges">Exchanges</NavLink>
          <NavLink to="/runtimes">Runtimes</NavLink>
        </nav>
        <TokenField />
      </header>

      <main>
        <Routes>
          <Route path="/" element={<Overview />} />
          <Route path="/exchanges" element={<Exchanges />} />
          <Route path="/exchanges/:exchangeId" element={<ExchangeDetail />} />
          <Route path="/runtimes" element={<Runtimes />} />
        </Routes>
      </main>

      <footer>
        <p>
          Metadata only. Prompts, model output, and tool arguments are never stored by a default
          deployment and are never displayed by this dashboard.
        </p>
      </footer>
    </div>
  );
}

/**
 * Where an operator supplies the administrative bearer token.
 *
 * In memory only, and the field says so. A dashboard that quietly persisted a credential which reads
 * someone's traces would be making a retention decision on the operator's behalf, which is the one
 * kind of decision this product does not make silently.
 */
function TokenField() {
  const client = useQueryClient();
  const [value, setValue] = useState('');
  const [applied, setApplied] = useState(hasToken());

  return (
    <form
      className="token"
      onSubmit={(event) => {
        event.preventDefault();
        setToken(value.length === 0 ? null : value);
        setApplied(value.length > 0);
        void client.invalidateQueries();
      }}
    >
      <label>
        Admin token
        <input
          type="password"
          value={value}
          placeholder={applied ? 'set for this tab' : 'not required from loopback'}
          onChange={(event) => setValue(event.target.value)}
          autoComplete="off"
        />
      </label>
      <button type="submit">Apply</button>
    </form>
  );
}
