# Changelog

## 1.0.0

First stable release, and a rename: **lilToonMeshProtect is now MeshProtect**. The component
GUID did not move, so an existing install upgrades in place and keeps its password.

- Shaders other than lilToon are protected too. The generated decode is grafted onto a copy of
  the shader, pass by pass, for Poiyomi Toon, Xiexe's Toon Shader, UnityChanToonShader,
  Sunao Shader, GTAvaToon and blackbody. A material the graft cannot cover in full ships
  unprotected and named in the Console, rather than broken.
- The inspector, its tooltips and its dialogs are localized: 日本語 / English / 简体中文 / 한국어,
  following the OS language unless you pick one. Build warnings and the Console stay English so
  they can be pasted into a help thread.
- An upload that protects nothing now stops and says so, instead of shipping an avatar that
  looks protected and is not. The dialog has a button to upload it anyway.
- Everything the tool generates lives inside the plugin folder, so removing the folder removes
  it all.

Betas 0.2.0 through 1.0.0-beta.37 are not listed here; they were the development of the above.
