---
tags: [fuluma]
max_turns: 40
timeout_seconds: 900
allowed_tools: [Read, Glob, Grep, Bash]
---

I added a soft-edges option to the spot light shader behind a new render feature setting (shader.diff in the current folder) and now FulumaShaderStrippingTests.Schema_MatchesTheShadersKeywords fails (test-output.txt). What else do I need to change so stripping and the tests handle the new keyword? Don't run Unity; read the repo.

Input files: shader.diff, test-output.txt
