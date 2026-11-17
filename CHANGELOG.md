# Changelog

## 1.3.0

- **Every shader that carries the decode is now measured before it is used, instead of being
  trusted.** Until now a shader was protected because this tool knew its name, and whether the
  decode still fitted it was settled by checks written against the version of that shader the
  tool was built with. lilSSRT 1.4 showed what that costs: the merge succeeded, the desktop
  variant compiled, and the avatar was broken for everybody in a headset. Each material's
  decode shader is now compiled, pass by pass, for the desktop and for the stereo-instanced
  variant VR uses, and then drawn - the original over a plain mesh, the decode shader over a
  displaced one, unlocked and locked - and the pictures are compared. A shader that does not
  compile where its original does, or that does not draw what its original draws once
  unlocked, is refused: that material ships unprotected and intact, the lock gate keeps it
  hidden, and the report carries the compiler's own error.
- What this changes about updates: a new version of lilToon, lilSSRT, Poiyomi or any other
  supported shader can still cost a material its protection until this tool catches up, but
  it can no longer produce an avatar that is protected and broken. If the report says a
  shader did not pass the check, send last-upload.txt - it has everything needed.
- The check runs in the build and when 'Rebuild Shader' or 'Generate Password' is pressed,
  where anything it refuses is named in the Console before any upload. Compiling takes
  several seconds per shader the first time and is not repeated until that shader's source
  changes - read off the preprocessed source itself, so an updated include, lilToon
  regenerating its settings and a new version of a host shader all count. The pictures are
  taken on every build, which costs a fraction of a second per shader. A refusal is never
  remembered.
- A pass that still draws something while the avatar is locked is reported and not refused.
  The mesh is still protected, and leaving it readable to hide a few pixels would be the
  worse trade.
- Only passes VRChat can run are checked. lilToon puts a pass that never runs into every
  shader, and its own build-time optimisation leaves that pass uncompilable in every custom
  family - harmless, and not a reason to refuse anything. Materials with GPU instancing
  ticked have their instanced variants compiled as well.
- What the check does not cover: the light, shadow and fog keyword combinations beyond one
  per pass, and anything a later build step changes after this tool has run. It was measured
  once more on lilToon and lilSSRT shaders as lilToon's optimisation leaves them for the
  bundle, and they pass.
- last-upload.txt says how many shaders were checked, how many were measured in that upload
  and how many were refused, so a report from a version that checks can be told apart from
  one that did not.

## 1.2.2

- **lilSSRT 1.4 no longer breaks for VR viewers** (tested on 1.4.3). lilSSRT 1.4 changed how its
  AO tessellation hands vertices to the hull stage, and the decode wired in front of it still
  assumed the earlier shape. That mismatch compiles for desktop viewers and in the editor and
  fails in the stereo-instanced variant VR uses - so a protected lilSSRT avatar looked right to
  its author and its AO materials, which lilSSRT puts on these shaders BY ITSELF with default
  settings, did not compile for anybody in a headset. Nothing at build time said so. The wiring
  now reads the type off the installed lilSSRT instead of assuming it, and every tessellating
  pass built on a host's own tessellation is test-compiled in both variants when the merged
  family is made: one that fails goes on the deny list and ships unprotected and intact, rather
  than protected and broken.
- **Avatars using lilSSRT 1.4: press 'Rebuild Shader' once** (under 'Advanced'). Until you do,
  their AO-tessellating materials are refused with a warning that names them and ship
  unprotected and intact. Merged families of lilSSAO and of lilSSRT before 1.4 were wired
  correctly and keep working without a rebuild.
- **A locked avatar now shows nothing at all.** The lock is a shader effect, so on its own it
  only hides what it protects - and whatever was not protected kept drawing: a body on an
  unsupported shader, a renderer left alone over a material swap, accessories, particles.
  Reported as the head and the clothes vanishing and the body left standing there. 'Hide
  unprotected parts while locked' (Advanced, on by default) adds one FX layer that keeps those
  renderers switched off until the password is in. It applies to existing avatars on their next
  upload with nothing to press. It hides and does not protect - those meshes ship as readable
  as before, and the upload report says so every time.
- That layer cannot check the password - an animator that could would have it written into a
  controller that ships - so it opens when every digit the password uses has been entered. A
  complete WRONG password therefore shows the unprotected parts on their own until it is
  corrected, and the layer's conditions tell anyone who opens the controller how many digits
  the password has (not which). A renderer whose enabled flag an animation already drives, or
  that shares its name and path with an earlier sibling, is left visible and named.
- **The inspector spells out how to enter the password**, with your own digits: which entry to
  open and which number to press. In game the unlock menu shows entries 1-6 holding buttons
  1-8, and nothing there says the first number is a position and the second a digit.
- The unlock entry's name, parent path and position are folded away as optional. Left alone
  they add 'Unlock' at the end of the root menu, which is what almost everybody wants; the
  line that stays visible says where the entry will be.
- **A password changed after uploading is pointed out.** The password field is live, and the
  avatar already in VRChat only opens with the password it was uploaded with. The inspector
  now warns when the one on screen is not the one the last build used.
- A renderer that shares its name and path with an earlier sibling is no longer displaced. The
  digits reach a renderer through an animation path, and two siblings with one name have one
  path, so the second could never be decoded - it would have stayed collapsed with the right
  password. It now ships unprotected and is named in the report; renaming one of the two
  protects it.
- The over-budget expression parameter notice goes into last-upload.txt as well as the
  Console, so an avatar that will not unlock for that reason can be told apart from the rest.

## 1.2.1

- The unlock entry's display name, parent submenu path and position can be set on Mesh Protect
  Root. Menu paths resolve after other installers have run, and only build copies are edited.
  Existing controls named Unlock are preserved. Explicit positions stay on the selected page
  when it is full; the last two original controls move into More.
  Paths support escaped slashes in names and visible labels with rich-text formatting.
- Password, protection and preparation changes are recorded as Prefab instance overrides.
- Shader preparation also discovers material swaps referenced by VRCFury and Modular Avatar
  components, including controllers and clips outside the Avatar Descriptor.
- Material-swap animations are checked before meshes are displaced. A renderer with an ignored,
  unsupported or otherwise unrewritable material swap stays original and is named in the build
  warnings, so switching outfits cannot put an undecoded material on its scrambled mesh.
  This includes child Animators and legacy Animation components, using each component's animation root.
  Unrewritten synchronized-layer material overrides also keep the affected renderer original.
  Material swaps copy affected external blend trees even when name obfuscation or optional tree
  copying is disabled, preserving shared references without modifying the original assets.
- Object renaming preserves paths used by independent Animators and legacy Animation components.
  Animation references inside external trees that are not copied retain their names. Shared state
  behaviours stored outside their controller are also left untouched, with their referenced names preserved.
  Playable-layer override clips and synchronized-layer overrides also preserve references that
  the build cannot rewrite. Preparation only treats controllers with an actual copy as rewritable.
  Older snapshots must be prepared again before adoption; builds safely fall back in the meantime.
- Prepared controllers now track their animation dependencies and exact sub-asset identities.
  Dependency fingerprints include referenced assets individually, so editing an external clip
  invalidates its snapshot even when Unity leaves the controller's own import hash unchanged.
  Builds reject stale or mismatched copies, including records restored by Undo, and recalculate
  safe name maps against the current avatar. Distinct same-name FX clips retain their own overrides.
  When other avatar tools replace every prepared controller, name maps are recalculated from the
  final build controllers. Each preparation writes an independent snapshot; clearing the record
  preserves old assets for other avatars and Undo, at the cost of additional disk space.
  First-time preparation creates the snapshot's parent folders before allocating its path.
- Builds with multiple Mesh Protect Root components stop and list their locations, including
  disabled components, so the password and protection settings are never selected arbitrarily.
- Baked mesh bounds include the original bounds as well as the displaced vertices, so unlocked
  static accessories keep their culling bounds and artist-supplied bounds are preserved.
- Skinning residual measurements match renamed meshes by hierarchy position, including duplicate
  object names, and report when no vertices could be measured. A Root component on a child object
  measures the whole Avatar.
- Locked lilToon materials use the native invisible gate before outline, AudioLink and fur effects;
  foreign shader grafts discard locked fragments. Existing avatars need **Advanced → Rebuild Shader**
  once to update their generated shaders without changing the password. Older families are refused
  with an actionable warning rather than used as though they contained the fix.
- Generated GPU self-check probes declare the visibility input used by the shared shader code,
  preventing a probe compilation failure from incorrectly blocking a valid avatar build.
- Parameter-budget handling remains unchanged.

## 1.2.0

- **lilToon's Tessellation is protected properly now.** A material with Tessellation switched on
  used to break up in-world even with the right password: the decode ran in the stage the domain
  shader calls, after the tessellator had already invented vertices from an interpolated identity.
  It now runs one stage earlier, once per original vertex, before the tessellator sees the mesh -
  measured pixel-identical to the unprotected surface, on static and skinned meshes, for the
  Opaque, Cutout, Transparent and Outline variants. Present in every earlier version; it only
  shows on materials that actually tessellate.
- **Existing avatars: press 'Rebuild Shader' once** (under 'Advanced' on the Mesh Protect Root)
  to pick the fix up - the build deliberately never regenerates a shader family on its own. Until
  you do, a tessellating lilToon material is refused with a warning naming it and ships
  UNPROTECTED and intact, rather than protected and broken the way earlier versions shipped it.
  Nothing changes for materials that do not tessellate.
- **lilSSAO's and lilSSRT's tessellating variants are protected too** - including the
  AOTessellation shaders lilSSRT assigns BY ITSELF whenever its AO is on with default settings,
  which nobody ever picks and nobody can pick their way off. The merged family is already a copy
  this tool writes, so the same early decode is wired into its tessellating containers at merge
  time; a container that cannot be verified safe goes on a deny list recorded at merge time,
  which conversion consults, and the warning names the exact controls that leave the variant.
  Existing merged families pick the wiring up on the same 'Rebuild Shader' press.
- **The summary line stopped under-reporting.** A refused material slot ships its sub-mesh
  readable and intact next to protected neighbours - and the one line an author reads said
  "Protected 12 mesh(es)" with no qualifier. It now counts those sub-meshes, in the Console and
  in last-upload.txt.
- **'Measure Skinning Residual' is now a full pre-upload dry run.** It always ran the whole
  pipeline on a throwaway clone; it now also hands over every warning that run produced - so a
  refused material is knowable before an upload costs anything, in a Console the SDK is not
  modal-blocking.

Upgrading keeps your password, your prepared grafts and your merged host families: the shared
signature they are cached on did not move. Only the avatar's own family needs the one rebuild.

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
