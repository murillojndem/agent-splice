# Dashboard screenshots

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
