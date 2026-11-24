---
tags: [fuluma]
max_turns: 40
timeout_seconds: 900
allowed_tools: [Read, Glob, Grep, Bash]
---

fuluma fake lights look great in the editor (Game view, my hangar scene) but in the android build on my phone they are just gone, everything else renders fine. it's a vulkan build, no errors from fuluma in logcat (attached). project files are in the current folder. any idea?

Input files: ProjectSettings/QualitySettings.asset, ProjectSettings/GraphicsSettings.asset, Assets/Settings/URP-PC.asset, Assets/Settings/URP-Mobile.asset, Assets/Settings/URP-PC-Renderer.asset, Assets/Settings/URP-Mobile-Renderer.asset, Assets/Scenes/Hangar.unity, logcat.txt, plus .meta files
