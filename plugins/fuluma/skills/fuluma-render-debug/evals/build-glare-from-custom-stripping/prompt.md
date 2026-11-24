---
tags: [fuluma]
max_turns: 40
timeout_seconds: 900
allowed_tools: [Read, Glob, Grep, Bash]
---

Our Windows player build looks off: every Fuluma spot light has a blinding white hotspot right at the lamp, like a little sun, and the beam looks harsher overall. In the Editor (Game view, same Warehouse scene, same quality level) they look soft and normal. Nobody touched the renderer between the two. A colleague was tidying up build settings last week. I copied ProjectSettings, Assets/Settings, the scene and the Fuluma lines of the build log into the current folder. What's going on?

Input files: ProjectSettings/QualitySettings.asset, ProjectSettings/GraphicsSettings.asset, ProjectSettings/FulumaSettings.asset, Assets/Settings/URP.asset, Assets/Settings/URP-Renderer.asset, Assets/Scenes/Warehouse.unity, build-log-fuluma.txt, plus .meta files
