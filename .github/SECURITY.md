# Security Policy

## Supported versions

NResilience is in initial development (0.x). Only the latest release receives
security fixes.

| Version | Supported |
|---------|-----------|
| 0.x     | Yes       |

Once 1.0 ships, this section will list the actively supported versions.

## Reporting a vulnerability

**Do not open a public GitHub issue for a suspected security vulnerability.**

Report it privately instead:

1. Open a private security advisory through GitHub:
   **Report a vulnerability** at
   [https://github.com/nresilience/NResilience/security/advisories/new](https://github.com/nresilience/NResilience/security/advisories/new),
   or
2. Email **security@nresilience.net** with a description and, if possible, a
   minimal reproduction.

You should receive an acknowledgement within 72 hours. If the vulnerability is
confirmed, we will publish a patched release and credit you in the advisory
unless you prefer to remain anonymous.

## Scope

- Authentication bypass, privilege escalation, or remote code execution in any
  NResilience package.
- A resilience policy silently failing to retry, time out, or open the breaker
  when it should - that is the class of bug this library exists to prevent.
- Denial of service through the library's own allocation or retry behavior.

## Out of scope

- Vulnerabilities in dependencies of `NResilience.Extensions` (report them
  upstream to the respective Microsoft.Extensions package owners).
- Bugs that do not affect correctness or safety - open a regular issue instead.