---
title: Guides
description: Practical scenarios for implementing resilience policies in your application.
order: 2
---

# Guides

These guides provide complete, step-by-step scenarios for common resilience tasks. Each guide takes you from the initial problem to a working implementation.

If you are new to NResilience, see [Key concepts](../getting-started/key-concepts.md) to learn about policies, verdicts, and the library's core vocabulary.

| Guide | Goal |
| :--- | :--- |
| [Retry an HTTP call](retry-an-http-call.md) | Ensure an API call succeeds despite occasional transient failures. |
| [Protect a dependency](protect-a-dependency.md) | Use a circuit breaker to prevent a flaky dependency from affecting the rest of your application. |
| [Configure from appsettings](configure-from-configuration.md) | Update policy settings, such as deadlines, without redeploying the application. |
| [Test a policy](test-a-policy.md) | Write fast, reliable tests to verify that your policies behave as expected. |
