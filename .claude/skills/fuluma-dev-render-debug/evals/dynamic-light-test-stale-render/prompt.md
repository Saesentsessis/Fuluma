---
tags: [fuluma]
max_turns: 40
timeout_seconds: 900
allowed_tools: [Read, Glob, Grep, Bash]
---

I added a new EditMode test (DynamicLightMoveTests.cs, in the current folder): it renders a dynamic fake spot light, moves its Transform, renders again and expects the glow to move. It fails: the second image is identical to the first. In the Editor and in players the dynamic light moves fine. Why does the test see a stale render, and how should the test be written? Don't run Unity; read the repo.

Input files: DynamicLightMoveTests.cs
