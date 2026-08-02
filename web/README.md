# web/

`AgentSplice.Dashboard` is the optional diagnostic surface over the gateway's `/api/v1` endpoints.

## What it is allowed to be

It is a client of the documented HTTP API and nothing else (FR-DASH-002). It holds no database
connection, no configuration file, and no knowledge of the store's schema — everything it can show is
something the gateway already publishes, which is what keeps the API honest: a field the dashboard
needs has to be a field the contract declares.

It is also optional. The gateway is complete without it, and nothing in the request path knows it
exists.

## The rules it exists to keep

Three, and they are the reason the code has as many comments as it does:

- **An absent value is displayed as absent** (FR-DASH-006). A formatter that turns
  `number | undefined` into `"0 ms"` looks tidy and converts "this phase was not observed" into "this
  phase took no time". `src/format/evidence.ts` is where that is prevented, and
  `tests/evidence.test.ts` is where it stays prevented.
- **Every value carries where it came from** (FR-DASH-004, FR-OBS-010). A duration AgentSplice read
  from its own clock and a token count a runtime asserted are different kinds of claim, and the table
  says which is which.
- **No content is ever rendered** (FR-DASH-005). A default deployment stores none, and the retention
  notice says so rather than leaving a reader to notice the absence and suspect a bug.

Prompt processing and generation are never drawn as one bar. Nothing observable marks the end of
prompt processing, so the interval before the first output event contains the prompt, the queue, and
the network together (FR-OBS-005).

## Screens

Overview, Exchanges, Exchange Detail, and Runtimes (FR-DASH-003). Replay, benchmarks, the
compatibility matrix, and settings arrive with the stages that produce them.

## Working on it

```bash
pnpm install
pnpm dev        # http://127.0.0.1:5281, proxying /api and /health to the gateway on 5280
pnpm lint
pnpm test
pnpm build
```

`pnpm dev` proxies to `http://127.0.0.1:5280` so a developer does not have to disable CORS to look at
their own traces. Nothing about the proxy is required in production, where the two are configured by
URL.

## The administrative token

`/api/v1` needs a bearer token whenever the gateway has one configured — including from loopback, because
a local reverse proxy makes "arrived from 127.0.0.1" and "was made locally" the same observation. The
header field takes it and holds it **in memory only**: a token in `localStorage` survives the tab, is
readable by anything injected into this origin, and outlives the operator's intent. Closing the tab
ends the session, which is the right default for a credential that reads someone's traces.

A deployment wanting longer sessions should put the dashboard behind its own authentication rather
than have this cache a bearer.
