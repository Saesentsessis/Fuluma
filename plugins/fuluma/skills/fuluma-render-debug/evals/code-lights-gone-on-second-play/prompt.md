---
tags: [fuluma]
max_turns: 40
timeout_seconds: 900
allowed_tools: [Read, Glob, Grep, Bash]
---

Our neon sign glows are Fuluma lights added from code (NeonSignSpawner.cs, in the current folder with our project settings). The first time I press Play after opening the project they all appear. I stop, press Play again: they're gone, and they stay gone until I restart Unity or change a script. No errors or warnings. The feature draws the store the spawner uses (it's the same asset). What's going on?

Input files: ProjectSettings/EditorSettings.asset, ProjectSettings/QualitySettings.asset, ProjectSettings/GraphicsSettings.asset, Assets/Settings/URP.asset, Assets/Settings/URP-Renderer.asset, Assets/Scripts/NeonSignSpawner.cs, plus .meta files
