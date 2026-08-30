# Changelog

## 1.1.0

- **lilSSAO and lilSSRT are supported.** Both are lilToon custom shader families, which is the
  same extension mechanism this tool builds its own family on - so rather than grafting, their
  folder is copied into the avatar's generated output and the decode is merged into it. Materials
  keep every host property and the host's own inspector. Tessellating variants are refused and
  ship unprotected rather than shimmering, because the domain shader interpolates the vertex
  identity before the decode can read it.
- **No more flicker noise in motion-blur worlds.** Unity draws per-object motion vectors with
  its own internal shader, which never runs the decode - so in a world using post-process motion
  blur or TAA, the scrambled mesh was rasterised into the motion-vector buffer and smeared into
  visible noise around the avatar. Protected renderers now use camera-only motion vectors;
  measured, that makes the buffer pixel-identical to an unprotected avatar's. Present in every
  earlier version; it only shows in worlds that consume motion vectors.
- **A leftover install of the old lilToonMeshProtect is now reported.** The two use different
  folders, so importing MeshProtect never removed the old one, and both then added a step to every
  build while sharing every asset GUID. Nothing said so before. Read your password off the
  component before deleting the old folder - it may not survive.
- A material that only ever appears in a wardrobe animation is now surveyed like any other, so a
  non-lilToon one gets its graft prepared. It used to ship unprotected, and the warning naming it
  pointed at a button that looked in the same wrong place.
- 'Rebuild Shader' surveys the whole avatar even when the component sits on a child object. It
  used to survey only that object and report success.
- Three messages that named a button or a workflow which no longer exists were rewritten.

Upgrading from 1.0.0 keeps your password, your generated shader family and your prepared grafts:
nothing was removed and no GUID moved.

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
