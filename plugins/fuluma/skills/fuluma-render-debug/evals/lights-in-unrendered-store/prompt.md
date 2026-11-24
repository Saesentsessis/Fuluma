---
tags: [fuluma]
max_turns: 40
timeout_seconds: 900
allowed_tools: [Read, Glob, Grep, Bash]
---

I'm on Unity 6000.3 with URP 17.3 and Fuluma 0.1.0. I placed a bunch of Fuluma fake spot lights in my Level01 scene (and made a light store asset for the level so I can swap them per level later), but none of them show up, not in the Scene view and not in the Game view. No errors or warnings in the console. I copied the relevant project files into the current folder (ProjectSettings and Assets subfolders). What's wrong and how do I fix it?

Input files: ProjectSettings/QualitySettings.asset, ProjectSettings/GraphicsSettings.asset, Assets/Settings/URP-HighFidelity.asset, Assets/Settings/URP-HighFidelity-Renderer.asset, Assets/Lighting/Level01 Lights.asset, Assets/Scenes/Level01.unity, plus .meta files
