---
title: Guides
description: Worked scenarios, each one complete and copy-pasteable.
order: 2
---

# Guides

Each guide is one scenario, start to finish. A **policy** is the object that says what to do when
a call fails - whether to retry, how long to wait, and when to stop calling the dependency. If the
word is new, [key concepts](../getting-started/key-concepts.md) defines it and the rest of the
vocabulary these guides use.

| Guide | The goal you arrived with |
| --- | --- |
| [Retry an HTTP call](retry-an-http-call.md) | "This API fails sometimes and I want the call to survive it." |
| [Protect a dependency](protect-a-dependency.md) | "Payments is flaky and I do not want it taking the rest of the app down." |
| [Configure from appsettings](configure-from-configuration.md) | "Operations wants to change the deadline without a deploy." |
| [Test a policy](test-a-policy.md) | "I want a test that proves the retry happens, and that runs in milliseconds." |

