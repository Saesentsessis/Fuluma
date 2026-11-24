---
tags: [fuluma]
max_turns: 40
timeout_seconds: 900
allowed_tools: [Read, Glob, Grep, Bash]
---

Yesterday the Redmi 15C ran the stress test (Benchmark scene, 1024 lights, 0.5 light buffer) at about 17 M GPU active cycles per frame. Today I made a fresh Android profiling build and the same run shows about 32 M. I didn't touch the package code. Figure out why, without running Unity or touching the phone: read what's in the repo.
