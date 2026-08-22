# Diagnostics

The suites in `Tests/Editor` run against things this tool builds itself, so they run anywhere.
These do not: they need a real VRChat avatar in an open scene, with a `MeshProtectRoot` that
has a password on it, and they measure what renaming would do to *that* avatar. They were written
for the parameter and object name work and they are the only thing standing between that work and
a silently broken upload, which is why they are kept here rather than in whichever project they
last ran in.

`Tests` is excluded when the `.unitypackage` is built by the release tooling, which keeps it
out of a package install.
That covers one of the two ways this tool is installed - a VPM or git URL install takes the
repository as it stands, `Tests` included - so the assembly definition here carries
`defineConstraints: ["LILMP_TESTS"]` and compiles nowhere that has not asked for it. Without that,
an install by the other route would put 90KB of diagnostics, a menu item that writes assets into
the project, and a hard reference to the VRChat SDK - which the main package deliberately does
without - into somebody's avatar project.

## Running them

Copy this folder into an avatar project - anywhere under `Assets` - and add `LILMP_TESTS` to
Scripting Define Symbols (Project Settings ▸ Player). Then open the scene with the avatar and use
**Tools ▸ MeshProtect Diag**. Or without opening the editor:

```bash
Unity.exe -batchmode -nographics -quit -projectPath <project> -executeMethod MPDiag.MPParamHarness.RunBatch -logFile <log>
```

`RunBatch` opens the first scene that has an avatar descriptor in it. Both write their report to
`mp-harness-report.txt` / `mp-param-report.txt` in the project root; the last line of the harness
report is `ALL CASES PASS` or a list of what failed.

Delete `Library/ScriptAssemblies` before a batch run that is checking a code change. Unity decides
whether to recompile from timestamps, and a run that quietly tested the previous build reads
exactly like a run that passed.

## What is in them

`MPParamAnalysis` measures: how many names could be renamed, what blocks the rest, what an avatar
mentions before and after a build. No assertions - it is for looking at an avatar you have not
seen before.

`MPParamHarness` asserts. Cases A-H prepare and build the avatar under different combinations of
the options and check the result; the standalone checks in front of them cover one rule each.
Several exist because a bug got past everything else and needed pinning down - the summary above
each says which. A fix without a check here that goes red when the bug is put back is not a fix
that anybody can rely on staying fixed.

They are slow. A full run rebuilds and uploads-in-miniature eight times and takes a few minutes on
a real avatar.
