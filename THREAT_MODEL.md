# Technical notes and threat model

[日本語](技術資料.txt) · **English** · [简体中文](THREAT_MODEL_CN.md) · [한국어](기술자료.txt)

The detailed version, for people evaluating MeshProtect rather than using it. Everything needed to
use the tool is in [README.md](README.md); nothing here is required reading to protect an avatar.

The claim MeshProtect makes is narrow and worth stating exactly: **it makes ripping a model
expensive per model, rather than impossible.** Everything below is what that does and does not
buy you.

---

# Part 1 — Security scope

## What the design removes

**Scripted, offline, at-scale attacks.** This is the bulk of what actually happens to a model that
gets passed around: someone runs a tool over a downloaded file and gets a mesh. Against
MeshProtect there is no such tool, because there is no shared algorithm to write one against. The
hash program, its constants, the property names and the shader family are all generated per
avatar. Work spent on one avatar transfers to no other.

**The smoothness fit.** The previous generation of this idea stored per-vertex factors and let the
password contribute a few shared scalars. That made the restore *linear* in a handful of unknowns:
a least-squares fit against "meshes are smooth" recovered the model in milliseconds, without ever
touching the password. Generating the displacement from the key removes those unknowns.

That claim used to be stated as "nothing continuous is left to fit", which was the defender's view
of it and was wrong. The attacker is handed the displaced position, the original normal (the
decode needs it, so it ships), and the per-vertex amplitude in the clear in TEXCOORD6. The
original therefore sits in a known box around the shipped position, and the unknown is one or two
*bounded scalars per vertex* — continuous, and exactly what a smoothness objective fits. What
actually changed is the ratio: four unknowns against a whole mesh of constraints was trivially
over-determined; per-vertex unknowns are not.

**Measured**, on one avatar, with a Laplacian fit (`Tests/Diagnostics/MPSmoothnessAttack`):

| mode | displacement recovered | result |
|---|---|---|
| Normal | 56–89% | reached the floor on two of three meshes — the fit undid everything |
| **Tangent space** (default) | 28–45% | body mesh stayed 3.1 cm out against a floor of 0.25 cm |

The reason is structural rather than lucky: a smoothness prior only says where a surface should be
along its *normal*, so it constrains normal-direction displacement completely and says almost
nothing about the tangential half. This is why tangent space is the default and why the tool never
falls back to Normal quietly.

**Rip-and-reimport.** Vertex identity is the raw bit pattern of UV0. Any re-serialisation — FBX,
glTF, a trip through a DCC tool, a re-import — requantises those floats, changes every vertex
identity, and permanently breaks the restore *even for someone holding the correct password*. An
attacker has to work in place on the original binary rather than using the usual pipeline.

## What it does not cover

**Runtime GPU capture.** While the avatar is unlocked the decoded mesh exists on the GPU of every
client in the instance — it has to, or nobody could see it. A capture taken there needs no
password, no reversing, and none of the 18 bits; it needs someone to be in the same world as
somebody wearing it, and one is enough.

No uploaded file can prevent this, and it is the ordinary way models are taken. **If a model is
worn in public instances, assume a determined person can get the geometry.** What MeshProtect
raises is the cost of everything else. This applies equally to every mesh-protection tool that has
ever existed for this platform.

**Textures.** They come out intact, names and all. This is not an oversight: everything that
survives BC7/DXT block compression is either visible or useless. Watermark them instead.

**Quest / Android builds.** VRChat's Android platform does not allow custom shaders, so the decode
could never run. The Android upload therefore goes out exactly as the avatar is, unprotected,
while the PC upload is protected as usual. This is automatic and there is nothing to toggle — the
earlier design asked the author to untick for Quest and tick again for PC, and forgetting the
second half shipped an unprotected PC avatar, which is worse.

**The password is a lock, not a secret.** Six digits of 1–8 is 2^18, and the material carries a
verifier so a wrong password is recognisable — which means anyone who can evaluate the hash
offline tries all of them in under a second.

What stops that is that the hash program is generated per avatar and exists only as compiled
shader bytecode in the bundle, so recovering it means reversing DXBC for that specific avatar. The
C# ships as plain source inside the free package, so **the structure is not the secret — the
per-avatar constants are.**

A longer password does not fix this. 2^36 falls in minutes, and a radial menu cannot carry much
more. Neither does key stretching: the key is derived in a vertex shader, recomputed for every
vertex of every frame, with nowhere to put a slow function.

**Someone willing to write an extractor once can break every avatar the tool has produced,
including already-uploaded ones.** That is a real and unpatched exposure. It costs that person
real reverse-engineering work up front, and it is the ceiling on what this design can offer.

**Wearing it with the password.** Anyone handed the password can wear the avatar, and the password
syncs to everyone in the instance because remote clients have to decode too.

**Object names the client drives.** The skeleton's humanoid bones, the avatar root, and the object
MMD dance worlds address the face through (`Body`) keep their real names, because renaming them
would break the avatar or the MMD world. Anything an animation this upload does not own also keeps
its name. Everything else is renamed.

## Why the design still makes sense

Rank the attacks by how often they actually happen to a model:

1. **Someone downloads a cache rip and wears it.** Blocked — no password, nothing renders.
2. **Someone runs an existing de-protection script.** Blocked — no script matches this avatar.
3. **Someone opens the mesh in Blender to edit and resell.** Blocked — needs the shader reversed.
4. **Someone captures it off the GPU in a public instance.** Not blocked.
5. **Someone reverse-engineers DXBC for this one avatar.** Not blocked, hours of work, per avatar.
6. **Someone writes a general extractor for the tool itself.** Not blocked, and it scales.

The first three are the overwhelming majority and they are gone. Four is a floor no file format
can raise. Five and six cost real expertise and real time — and five has to be paid again for
every single model.

**Not unbreakable, just not worth breaking at scale.** Sell what you build with it as protection
against ripping rather than as encryption, and the claim holds.

## What would change this assessment

- A published general extractor would collapse item 6 from "someone could" to "someone did", and
  every already-uploaded avatar with it. There is no mitigation from inside a shipped file; the
  response would be a new generation of the algorithm and a re-upload.
- A VRChat platform change permitting mesh access from Udon, or a client change to shader
  handling, could open paths that do not exist today.
- If a future Android platform allows custom shaders, Quest builds could be covered.

---

# Part 2 — Operational detail

## Passwords

**Saved per machine.** VRChat keeps the unlock state on local disk, not on its servers, so a new
PC or a reinstall means entering the password again. It is not recoverable from the avatar: a lost
password means generating a new one in Unity and re-uploading.

**A password may be one to six digits, and six is recommended.** The menu always shows six dials
whatever the length, so the dial count never reveals how long the password is. The positions a
shorter password does not use have to be left alone — the menu cannot send "not entered", so once
a dial is turned that position cannot go back from the menu. If that happens, VRChat's own **Reset
Avatar Data** clears it; because these parameters are saved, re-selecting the avatar brings the
correct ones back. There is deliberately no reset control in the menu, because a six-digit
password cannot over-type and the button mostly cleared correctly-entered passwords by accident.

A wrong digit within the password's length just needs that dial re-selected.

**Shorter is faster to guess by hand**: six digits is 262,144 combinations against 4,096 for four,
which is about five hours of somebody working through the menu.

## Expression parameter renaming

Optional, under Advanced, and the one setting with a cost outside your own project. The expression
parameter list is the avatar's public API: OSC applications — face tracking, heart rate, chatbox
and prop tools — address those names directly, and nothing inside the project can prove they do
not.

Names VRChat drives itself, namespaced names such as `v2/JawOpen`, and the names VRCFaceTracking
is documented to use are all kept automatically, and the report names everything that was renamed.
Two costs remain, and both reach people who already have the avatar:

- **saved parameter values reset** — a renamed parameter is a new parameter
- **an OSC setup keyed to your own names stops working** until it is repointed

Untick it if either matters.

## Parameters that keep their names

A parameter is avatar-global: rename `Action_Mode` in FX and the Base layer's `Action_Mode`
becomes a different parameter, and the two stop talking. So every parameter has exactly two
outcomes — every place that names it is rewritten, or none is.

The default is therefore inverted relative to every blacklist design: enumerate every reference in
the whole avatar, and rename only if **every** one of them lands somewhere this build rewrites. A
missing rule costs one unobfuscated parameter instead of an avatar that cannot stand up. The
component's report lists what was kept and why.

## Renderers that ship unchanged

Each of these means one renderer ships as it was, named in the Console with the reason, while the
rest of the avatar is protected. The upload is not stopped:

- a mesh with no UV0
- a mesh with data already in UV6
- a mesh without Read/Write enabled
- a material reading its lilToon ID Mask from UV6 (set the mask's UV to 7)
- a mesh a collider or a particle system also points at
- a material on a shader family outside the supported list
- a material on somebody else's lilToon custom family — if that family is another protection tool,
  displacing an already-displaced mesh would destroy the model

Because hiding is a shader effect, anything left unchanged this way still renders while the avatar
is locked. The Console names every renderer this applies to.

## When the build stops

A single unprotectable renderer never stops an upload. Two things do:

- **the build would produce a broken avatar** — the generated shader disagreeing with the C#
  cipher, a mesh that does not decode back, a digit reaching a material, a missing key verifier,
  two shader families mixed on one avatar
- **the build would protect nothing at all** — no password, no material that can carry the decode,
  a shader family missing from the project, or no room in the expression parameter budget

The second stops with a dialog naming the reason and a copy in `Generated/last-upload.txt`, and
its other button uploads anyway. Carrying on silently used to be the behaviour, and it made
"protected" and "not protected" indistinguishable from outside — same button, same success, same
upload — so it was found out in game, after the upload. Quest builds are exempt: there, protecting
nothing is the right answer.

## Invisible Materials

Under Advanced. This is the author's word on something the tool cannot judge: a material on an
unsupported shader that truly draws nothing on screen would otherwise block the displacement of
every sub-mesh it shares or re-draws, because the build cannot prove it renders nothing.

Do not list anything that writes stencil or depth — such a material decides what *other* materials
draw, and listing it lands the damage on the body or clothes, visible only while the avatar is
unlocked. Do not list anything visible: a visible material listed there shows the scrambled mesh
as permanent noise.

Anti-clip shells on blackbody no longer belong here — that family is grafted automatically.

## Other notes

- **Viewers with custom shaders disabled see nothing** rather than a scrambled avatar
  (`VRCFallback: Hidden`).
- **Base, Gesture and Action layers are copied in the editor**, not during the build. Copying them
  during the upload once produced an avatar that held a pose it was not in and could not stand up,
  with no structural difference between the two builds that anything could find. They are remade
  whenever you edit one of those controllers.
- **When prepared controller copies go stale** — tools that replace controllers during the build,
  VRCFury above all, invalidate the stored maps — the build falls back to a fresh survey and
  renames whatever its own FX copy fully accounts for, keeping the rest by name.
- **A full root menu costs one control one level, not the whole menu.** When there is no slot left,
  the last control moves into a `More` page and the unlock entry joins it there. Only the upload is
  affected; the menu in your project is untouched.
- **Blend trees saved as their own `.asset`** are copied into the protected controller rather than
  written to. On the avatar this was measured against, 320 trees were stored that way and blocked
  43 parameters on their own.

---

# Part 3 — Development

Tests live in `Tests/Editor` behind the `LILMP_TESTS` define so they do not compile in user
projects. Add `LILMP_TESTS` to Scripting Define Symbols in a project with lilToon and the Avatar
SDK, then:

    Unity.exe -batchmode -quit -projectPath <project> -executeMethod MPTest.MeshProtectBatchTest.RunAll -logFile run.log
    Unity.exe -batchmode -quit -projectPath <project> -executeMethod MPTest.EndToEndTest.Run -logFile e2e.log
    Unity.exe -batchmode -quit -projectPath <project> -executeMethod MPTest.UnlockChainTest.Run -logFile chain.log
    Unity.exe -batchmode -quit -projectPath <project> -executeMethod MPTest.BundleRoundTripTest.Run -logFile bundle.log

`MeshProtectBatchTest` covers the cipher, the avalanche gate, shader generation, the generated
HLSL against the C# interpreter on the GPU, bake round-trips and cross-variant isolation.
`EndToEndTest` drives the real preprocess hook on a synthetic avatar and checks what comes out.
`UnlockChainTest` simulates the generated animator digit by digit — bit-order and polarity
mistakes produce a well-formed animator that decodes to the wrong password, and only this catches
them. `BundleRoundTripTest` builds a real AssetBundle and checks UV0 comes back bit-identical,
because Unity rewriting it would lock every uploaded avatar permanently while every other suite
stayed green.

A green suite proves nothing on its own: an assertion that cannot fail is indistinguishable from
one that passes. Every guarantee here is checked by **mutation audit** — removing it from the
source and requiring the named check to go red. That audit has found real holes: a filter that
quantified over an empty set, a NaN comparison that made "the value is wrong" evaluate to false,
an assertion comparing two compile-time constants, a check satisfied equally by "the names are
hidden" and "the controller is empty". It has also caught its own harness twice, scoring a crashed
suite as green and applying a mutation to the wrong one of two identical call sites.
