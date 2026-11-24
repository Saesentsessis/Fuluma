---
tags: [fuluma]
max_turns: 40
timeout_seconds: 900
allowed_tools: [Read, Glob, Grep, Bash]
---

Since yesterday our Android build dropped from a steady 60 fps to about 35 on a Mali-G57 phone in the hangar level, which has around 300 Fuluma lights with the default 0.5 light buffer. The only change merged was our art lead's 'enable 4x MSAA everywhere for nicer edges' (diff in the current folder, with our URP settings). Can MSAA really cost that much, and is it Fuluma? What would you do?

Input files: msaa-change.diff, ProjectSettings/QualitySettings.asset, ProjectSettings/GraphicsSettings.asset, Assets/Settings/URP-Mobile.asset, Assets/Settings/URP-Mobile-Renderer.asset, plus .meta files
