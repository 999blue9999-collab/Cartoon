# Unity Cloud / Build Automation

This repository is now ready to be built by a Unity Cloud project, but **creating the cloud
project and triggering a build still happens in Unity's own dashboard**: Arena does not expose a
Unity Cloud connector, so no agent-side tool can create the project, attach the Git repo, or start
a build for you. Everything that belongs in the repository is committed here.

## What was added for cloud builds

| File | Purpose |
|---|---|
| `Assets/Scenes/NoreDemo.unity` | Committed **empty** scene. A Player build needs ≥1 enabled scene; no GameObjects are needed because `GameController` bootstraps via `[RuntimeInitializeOnLoadMethod]`. |
| `Assets/Scenes/NoreDemo.unity.meta` | Fixed GUID so build settings survive a clean checkout. |
| `ProjectSettings/EditorBuildSettings.asset` | Enables `NoreDemo.unity` as scene 0 in every build. |
| `Assets/Editor/CloudBuildSetup.cs` | `[InitializeOnLoad]` safety net that regenerates the scene if it fails to import, keeps it in Build Settings, and provides `-executeMethod` entry points for build + verification. |
| `tools/validate_unity_project.py` | Local check that the scene, GUID, build settings, Unity version and JSON data are consistent. |

Menus: `Nore → Cloud Build → Ensure Build Scene / Verify Project / Build Player (local smoke)`.

## Dashboard setup (one-time, manual)

1. **Link the project to Unity Cloud.** In the Unity Editor open **Edit → Project Settings →
   Services**, then either *Use an existing cloud project* or *Create a new Unity cloud project*.
   That gives the project its cloud Project ID ([Unity manual: Configure a project for Unity
   cloud](https://docs.unity3d.com/Manual/SettingUpProjectServices.html)). You can also create the
   cloud project from Unity Hub when creating the Editor project.
2. **Connect source control.** In the Unity Dashboard open **Build Automation → Config → Source
   Control** and connect this GitHub repository (`999blue9999-collab/Cartoon`). A 2026 setup guide
   notes this connection is dashboard-only, not scriptable via API
   ([guide](https://macfree.dev/blog/unity-build-automation-ios-setup)). Pick the branch to watch
   (`main` after PR #3 merged, or your working branch).
3. **Create a build target.** Unity Build Automation connects to version control, watches for
   changes, then downloads and builds the project for the configured platforms
   ([Unity Build Automation docs](https://docs.unity.com/en-us/build-automation)). Configure:
   - Unity Editor version: `6000.0.32f1` (see `ProjectSettings/ProjectVersion.txt`)
   - Platform: Standalone (Windows/macOS/Linux) or Android/iOS
   - Scene list: taken from `EditorBuildSettings` — no manual scene selection needed
   - Optional custom command: `-batchmode -quit -executeMethod Nore.EditorTools.CloudBuildSetup.Build`
4. **Optional pre-build gate.** Run
   `Unity -batchmode -quit -executeMethod Nore.EditorTools.CloudBuildSetup.Verify` before the
   build. It validates all 9 JSON files and runs the 15 headless self-tests, failing the job on
   any error.
5. **Trigger and download.** Enable auto-build or start a build manually in the dashboard.
   Local smoke builds land in `Builds/Nore/Nore[.exe]` (gitignored); cloud builds are downloaded,
   shared or installed from the Build Automation dashboard.

## Why the scene is empty

`GameController` has `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]`,
so it creates its own GameObject in **any** scene, in the Editor and in a Player. Avoiding authored
`MonoBehaviour` references in scene YAML means the committed scene needs no `.meta` GUIDs for
scripts and cannot break when Unity regenerates them on a fresh cloud machine. If Unity ever fails
to import the hand-authored scene, `CloudBuildSetup.CreateEmptyScene()` recreates it through the
Editor API at load time, keeping the same path and GUID.

## Runtime notes for a cloud build

* IMGUI (`OnGUI`) works in Player builds on desktop; no UI package or prefab is required.
* `Packages/manifest.json` has an empty dependency list — Unity resolves its default modules.
* All 9 JSON files live under `Assets/Resources/Data` and are imported as `TextAsset`; the build
  includes them automatically. Runtime overrides at `Application.persistentDataPath/Data` are
  optional.
* Saves go to `Application.persistentDataPath/nore_save.json`, so cloud builds keep local saves.
* If the build reports `UnityProjectNotLinkedException`, link the Project ID in **Project Settings
  → Services** and make sure the dashboard member has access to the project.
* Mobile targets: the IMGUI layout is designed to be usable but not optimized for small screens;
  the browser companion (`preview/`) is the mobile-friendly reference.

## Browser companion alternative

The playable browser build is static (`preview/` + `Assets/Resources/Data/`). It can be hosted on
any static host — including Vercel, which is connected in this conversation — by deploying the
repository root and opening `/preview/`. That is a JS reference implementation, **not** a Unity
Player build.
