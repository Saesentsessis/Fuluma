---
tags: [fuluma]
max_turns: 40
timeout_seconds: 900
allowed_tools: [Read, Glob, Grep, Bash]
---

Our street lamps are Unity spot Lights with Fake Light (Linked). In the Editor, when I tick a lamp's Light on and off outside play mode, the fake beam follows it. But in play mode and in the Windows build, when NightCycle switches the lamps on at dusk, the Unity lights come on (I can see them lighting the road) and no fake beams ever appear. Scene, script and settings are in the current folder.

Input files: ProjectSettings/QualitySettings.asset, ProjectSettings/GraphicsSettings.asset, Assets/Settings/URP.asset, Assets/Settings/URP-Renderer.asset, Assets/Scenes/Street.unity, Assets/Scripts/NightCycle.cs, plus .meta files
