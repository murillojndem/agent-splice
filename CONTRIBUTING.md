# Contributing

Read `CLAUDE.md`, `docs/SPECIFICATION.md`, and `docs/ARCHITECTURE.md` before submitting code.

## Before opening a large pull request

Open an issue describing:

- affected requirement IDs;
- client, runtime, model, and backend;
- expected and actual behavior;
- reproduction;
- proposed design;
- security and compatibility impact.

## Pull requests

- Keep scope focused.
- Add tests.
- Update documentation.
- Include benchmark results for performance claims.
- Never include private prompts, employer code, API keys, or model weights.

## Compatibility reports

A useful report includes exact versions, model file, quantization, runtime settings, sanitized request shape, relevant logs, and a deterministic fixture.

## Security reports

Report vulnerabilities privately, as `SECURITY.md` describes, never in a public issue.

## License

AgentSplice is licensed under the Apache License 2.0 (`LICENSE`). Under section 5 of that license, unless you explicitly state otherwise, a contribution you intentionally submit for inclusion is licensed under the Apache License 2.0 without additional terms. There is no further agreement to sign, and contributions offered under other terms are not accepted.

A new dependency must be under a license compatible with Apache-2.0, and is added to `THIRD-PARTY-NOTICES.md` in the same change; a contract test fails until it is. The dashboard's source imports only packages its `package.json` declares under `dependencies`, which another contract test checks.
