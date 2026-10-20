# Dashboard screenshots

## A real session

`dashboard-real-session.gif`, at the top of the README, is not from `capture.js`. It was recorded on
2026-10-19 from the Windows package of 0.2.2-alpha, installed as the README's quickstart says and
started with its shipped settings, while OpenCode 1.18.7 ran two sessions through it to LM Studio 0.4.20
on one Windows machine:

- with `qwen3-coder-30b-apex`, whose embedded chat template LM Studio could not render: the agent
  request was answered `200` and then failed, as in the first trace report;
- with `qwen3.6-35b-a3b`, served one request at a time: the session's title request, then four agent
  steps, which OpenCode finished.

The task was the trace reports' `calc.py` task. A headless Chrome showed the dashboard while the second
session ran and took a frame whenever the list changed; the frames between changes are dropped, so the
session's 46 seconds play in about 7. The caption strip was added afterwards. The dashboard shows what the
gateway keeps: counts, shapes, and timings, never what was said. This is a demonstration, not a trace
report: the dated reports under `docs/reports/` are the evidence.

## From synthetic data

Taken by `capture.js` in this folder, from the gateway's own Release build serving its dashboard, against
a synthetic LM Studio the script runs on `127.0.0.1:1234`. The prompts, the answer, the token counts, and
the timings are made up by that fake runtime: the images show what the dashboard renders, and nothing
anyone said. They are not evidence about LM Studio, a model, or a client — the dated trace reports under
`docs/reports/` are.

| File | Screen |
| --- | --- |
| `dashboard-overview.png` | The overview: what the deployment retains and exports, runtime health, recent exchanges. |
| `dashboard-exchanges.png` | The exchange list, with what each runtime said beside the transport status (ADR 0026). |
| `dashboard-exchange-stream.png` | A streamed exchange: where the time went, tokens and derived throughput, each measurement's provenance. |
| `dashboard-exchange-runtime-error.png` | A stream in which the runtime reported an error inside a `200`, recorded as the runtime's failure. |

To take them again, with a Release build of the gateway, a dashboard build, and Chrome:

```text
dotnet build -c Release
pnpm --dir web/AgentSplice.Dashboard build
node docs/images/capture.js
```

`CHROME` names another Chrome executable. Port `1234` must be free, so stop LM Studio's server first.
