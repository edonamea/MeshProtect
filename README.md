<div align="center">

<img src="https://raw.githubusercontent.com/edonamea/MeshProtect/main/.github/images/banner-en.png" alt="MeshProtect — ripped, but unusable." width="100%">

**English** · [日本語](README.ja.md) · [简体中文](README.zh-CN.md) · [한국어](README.ko.md)

[![Unity 2022.3](https://img.shields.io/badge/Unity-2022.3-222222?style=flat-square&logo=unity&logoColor=white)](https://unity.com/)
[![VRChat Avatar SDK3](https://img.shields.io/badge/VRChat-Avatar%20SDK3-00acc1?style=flat-square)](https://vrchat.com/)
[![lilToon and 8 more](https://img.shields.io/badge/lilToon-and%208%20more-e91e63?style=flat-square)](#shader-support)
[![BOOTH](https://img.shields.io/badge/BOOTH-free-fc4d50?style=flat-square)](https://humuhumuhumu.booth.pm/items/8731588)
[![License](https://img.shields.io/badge/license-MIT-607d8b?style=flat-square)](LICENSE)

</div>

---

Somebody pulls your avatar out of the game files, opens it in Blender, and finds a cloud of
unrelated points. That is the whole idea.

MeshProtect scrambles the mesh on its way out and ships a shader that puts it back — but only for
the person wearing it, after they enter a six-digit password from the expression menu. **Your
project is never touched:** protection is applied to the temporary copy the SDK builds from, so
taking the component off gives you a plain upload again, and nothing in your scene ever knows
this happened.

<div align="center">
<img src="https://raw.githubusercontent.com/edonamea/MeshProtect/main/.github/images/locked-en.png" alt="Without the password the avatar does not render at all; with it, everything looks normal." width="92%">
<br><sub>Locked, the avatar does not render at all — not a scrambled mess in front of other people.</sub>
</div>

## Install

Take the `.unitypackage` from [BOOTH](https://humuhumuhumu.booth.pm/items/8731588) — it is free —
or add this repository as a UPM/VPM package:

```
https://github.com/edonamea/MeshProtect.git
```

Unity 2022.3 · VRChat Avatar SDK3 · lilToon 2.x · PC avatars.
The inspector speaks **日本語 / English / 简体中文 / 한국어**, following your editor's language.

## Four steps

1. Avatar root → `Add Component` → `MeshProtect / Mesh Protect Root`
2. Press **Generate Password** — six digits, each 1–8, or type your own (one to six digits).
   **Write it down:** VRChat stores it per machine, so a new PC means entering it again
3. Build & Publish, exactly as you always do
4. In VRChat: Expressions → **Unlock** → pick one digit per position

> [!WARNING]
> The SDK will show a red error saying this component gets removed by the client. That is true,
> harmless and expected — the build hook takes it out of the copy the SDK builds from.
> **Do not press Auto Fix.** It deletes the component from your scene, and your password goes
> with it.

<div align="center">
<img src="https://raw.githubusercontent.com/edonamea/MeshProtect/main/.github/images/inspector.png" alt="The Mesh Protect Root inspector" width="60%">
<br><sub>The whole interface. No bake button, no second avatar in the scene, nothing to re-run after you edit the model.</sub>
</div>

Outfits, PhysBones, Modular Avatar, VRCFury, mesh optimisers — all fine, in any order, because
protection runs after all of them. Quest uploads pass straight through untouched, so there is
nothing to toggle between the two builds.

## Shader support

| | |
|---|---|
| **lilToon 2.x** | Native, through lilToon's official extension point. Nothing is forked or patched, so a lilToon update cannot break it. |
| **Poiyomi Toon** · **Xiexe's Toon Shader** · **UnityChanToonShader** · **Sunao Shader** · **GTAvaToon** · **blackbody** | Automatic graft: the decode is copied onto their shader, pass by pass, located by semantic rather than by patching against text. |
| **lilSSAO** · **lilSSRT** | Merged family: they are lilToon custom shader families, so their folder is copied into the avatar's generated output and the decode is merged in. A copy of the host's shader folder therefore lives in your project alongside the generated family. |

A material the graft cannot cover in full ships unprotected and named in the Console, rather than
broken. Anything on a shader outside that list is left exactly as it was — never damaged.

## What it stops

| | |
|---|---|
| Open the ripped mesh in Blender | **blocked** — the shader has to be reversed first |
| Take it apart, retexture, resell | **blocked** — same |
| Run an existing de-protection script on it | **blocked** — no two avatars share an algorithm |
| Re-export the mesh through any DCC tool | **blocked** — vertex identity is the raw bit pattern of UV0 |
| Read what the avatar does from its FX controller | **blocked** — layers, states, blend trees and clips are renamed |
| Read what its meshes, materials and objects are called | **blocked** — generated from the avatar's own algorithm |
| Reverse this avatar's compiled shader by hand | hours of work — **and it buys nothing on the next avatar** |

The goal was never "unbreakable". It is that **breaking it does not scale**: the cost is paid
again, in full, for every single model, and that is what removes the scripted, offline, at-scale
attacks which are most of what actually happens to a model once it starts getting passed around.

Textures are not protected, and someone in the same instance can still capture the decoded mesh
off the GPU while it is being worn. Both limits, and the reasoning behind them, are written out
in [THREAT_MODEL.md](THREAT_MODEL.md) — worth reading before you sell anything with this.

## It checks its own work

Most tools in this space fail quietly, and you find out in game. This one verifies before the
upload finishes: the generated shader is compared against the C# cipher **on the GPU**, every
baked mesh is decoded back and checked per vertex across blend shape frames, the unlock menu and
its transport bits are confirmed complete, and every serialised field of every component is
walked to prove no original mesh is still reachable anywhere. Whatever it cannot cover in full is
left alone and named in the Console, so a build never quietly ships a broken avatar.

## How it works

- **The displacement is generated, not stored.** Each vertex is pushed along its tangent and
  normal by an amount derived from a hash of the password and the vertex's own identity. There
  are no coefficients in the mesh to read back.
- **Every avatar gets its own algorithm.** Setting a password assembles a fresh hash program and
  emits a complete shader family with the constants compiled in. Property, parameter and asset
  names are generated too, so nothing in an uploaded avatar even names this tool.
- **A locked avatar is invisible, not exploded.** Every vertex collapses to a point, so nothing
  rasterises in any pass. A wrong password collapses too: the avatar is either correct or absent,
  never scrambled in front of other people.
- **Integer arithmetic throughout**, so C# and HLSL agree bit for bit on every GPU.

**Overhead:** one UV channel (TEXCOORD6) · 24 bits of expression parameter budget · a handful of
vertex shader instructions, with **no change to the avatar's performance rank** · about 3.5 s
once, when the algorithm is generated. Changing the password and re-uploading cost nothing.

## More

📄 **[THREAT_MODEL.md](THREAT_MODEL.md)** — technical notes, security analysis, panel-by-panel
behaviour, troubleshooting
📋 **[CHANGELOG.md](CHANGELOG.md)** — what changed
💬 Questions and bug reports — the contact form on the
[BOOTH page](https://humuhumuhumu.booth.pm/items/8731588)

Tests live in `Tests/Editor` behind the `LILMP_TESTS` define. Every guarantee is additionally
checked by mutation audit: remove it from the source, and the named check has to go red.

## Security Maintainer

Humu humu (`edonamea`) is the creator and primary security maintainer of MeshProtect,
responsible for its threat model, anti-extraction architecture, vulnerability handling, and
security-related maintenance.

## Licence

[MIT](LICENSE). Use, modify, redistribute and sell it, the tool itself included — keep the
copyright and licence notice with every copy. What you build with it is yours to sell.

The lilToon shader templates in `Shaders/Templates` are derived from
[lilxyzw/lilToon](https://github.com/lilxyzw/lilToon) (MIT). The displacement approach follows
prior art in [rygo6/GTAvaCrypt](https://github.com/rygo6/GTAvaCrypt) and
[lilxyzw/AvaterEncryption](https://github.com/lilxyzw/AvaterEncryption), both MIT. Full
attributions in [NOTICE.md](NOTICE.md).
