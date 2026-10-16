# Security policy

Do not open a public issue for vulnerabilities that could expose credentials, private prompts, source code, or allow unauthorized access.

Report a vulnerability privately through GitHub's private vulnerability reporting: open the repository's **Security** tab and choose **Report a vulnerability**, or go to <https://github.com/murillojndem/agent-splice/security/advisories/new>. Only the maintainer sees the report until an advisory is published.

Include:

- affected version or commit;
- deployment mode;
- reproduction;
- impact;
- suggested mitigation;
- whether sensitive data was accessed.

Never include real credentials or private repository content in the report.

AgentSplice is an experimental alpha. Fixes land on `main` and in the next release; earlier releases are not patched. The design-level security model is in `docs/SECURITY.md` and `docs/THREAT_MODEL.md`.
