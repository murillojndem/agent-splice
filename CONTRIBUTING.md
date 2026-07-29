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
