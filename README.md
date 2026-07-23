# PathTwin

<p align="center">
  <img src="src/PathTwin.App/icon.ico" alt="PathTwin logo" width="120">
</p>

PathTwin is a Windows-first desktop app for selective, session-based folder synchronization.

It is not meant to mirror an entire drive. The app is built around a work session: choose a remote root, choose a local root, select only the folders needed for this session, pull them locally with their relative paths preserved, work locally, then end the session and safely push changes back.

## Screenshots

**Profile setup**

<img src="screenshots/Screenshot_config.png" alt="PathTwin profile setup" width="900">

**Selective pull**

<img src="screenshots/Screenshot_pull.png" alt="PathTwin selective folder pull" width="900">

**Active session**

<img src="screenshots/Screenshot_push.png" alt="PathTwin active session" width="900">

## Current MVP

- Avalonia UI on .NET 8
- JSON configuration under the user profile
- One active profile, with config shaped for multiple profiles later
- Lazy-loaded remote directory tree for local/SMB paths
- Three-state checkbox folder selection (checked / unchecked / partial)
- Configurable shallow local skeleton depth, defaulting to 2
- Start Work Session pull with real-time progress
- Start Work safety guard for unfinished previous sessions, with the saved failure phase, error summary, and direct access to the prior session logs
- Explicit `Ignore Previous Session and Force Sync` action for intentionally starting over after reviewing an unfinished session
- Unselected stale local cache cleanup sent to the Windows Recycle Bin after skeleton comparison
- Add Folder / Resume Sync during an active session, with prior selections locked
- End Work Session push with three-way planning and real-time progress
- Session manifest saved under `<localRoot>.pt\sessions`
- Remote backup before overwrite/delete (`overwritten/`, `deleted/`, `uploaded/` buckets)
- Version history auto-cleanup (configurable retention days)
- Unselected non-empty skeleton folders: update-only push; empty skeleton folders are ignored
- Basic conflict/error window and logs
- Single-instance guard
- Windows scheduled task with multi-trigger support (logon + session unlock)
- `--auto` argument handling with time-window and reachability checks
- Auto-startup settings in profile UI
- Optional rclone executable path, with native file-system fallback
- Optional rclone-powered remote file scanning (`lsjson`) and transfer, controlled per profile; Fast, Hybrid, and Content comparison semantics are consistent with the built-in backend
- Built-in file-system synchronization reports throttled progress while it checks large folder trees, including Hybrid hash comparisons and mirror cleanup
- Closing the app always shows the current workflow state and a force-exit warning; a successful End Work exit remains automatic
- Per-profile comparison modes: Hybrid (default), Fast metadata, or Content hash
- Concurrent local/remote comparison with a single transfer queue
- Active sync header identifies the backend currently in use
- Application icon (rounded, 1000×1000)

## Changelog

### v0.1.7

**Sync throughput**
- End Work now scans selected local and remote folders concurrently. As soon as both states for a file are available and its three-way comparison confirms a change, PathTwin queues that file for transfer.
- Confirmed file operations and folder transfers use a single queue so they do not compete for the same network bandwidth. A later conflict stops newly queued work, while changes that already started are retained and reported.
- Pull, selected-folder push, and update-only push skip nested duplicate roots before entering the transfer queue.
- Each concurrent folder task writes to its own log file, while the session log records task start and completion.

**UI**
- While a sync is running, the header shows the active sync tool directly to the left of `Logs`.
- The sync screen separates real-time file comparison and file changes into two scrolling columns.
- On startup, an unfinished prior session shows its status, last recorded phase, error summary, and a link to its logs. The normal Start action stays protected; an explicit danger action can intentionally ignore that prior status and start a new sync.
- The built-in file-system backend now keeps the current operation visible while it checks large source and destination trees after file comparison, so Hybrid verification and mirror cleanup do not appear stalled.
- Native Start and Resume pulls now stream potential file changes into a single transfer worker while the remote file scan is still running. Mirror-only local deletions and empty-directory reconciliation run after that scan completes.
- rclone Hybrid now lists both sides with rclone metadata, then sends only size or modification-time mismatches to rclone for checksum verification and conditional copy. Mirror-only deletions run after that verification. Content mode uses rclone `--checksum` throughout, including the final selected-folder reconciliation pass.
- rclone Hybrid streams checksum status and throttled file events to the sync UI. Its empty-directory phase has separate progress, and local or SMB destinations create those directories directly instead of starting one rclone process per directory.
- The main window renders before the profile is read. `Edit Profile` becomes available once that local configuration load completes.
- Closing PathTwin opens a state-aware confirmation dialog. During sync it warns that file work may be interrupted; during an active session it warns that local changes may remain unpushed.

**Comparison and rclone**
- Profiles can explicitly enable or disable rclone. When enabled with a valid executable, PathTwin uses rclone to scan the remote root and to transfer files.
- `Hybrid` is the default: matching size and modified time are accepted immediately; mismatched local/remote files are hashed before their contents are considered different.
- `Fast` compares only size and modified time.
- `Content` compares SHA-256 content hashes and uses rclone `--checksum` for rclone transfers.

### v0.1.6

**Start Work cleanup**
- Start Work cleanup now sends stale unselected local cache items to the Windows Recycle Bin instead of moving them into `.pt\trash`.
- Cleanup now runs after selected remote files are scanned and the shallow skeleton is compared.
- Existing skeleton directories that still belong to the current remote skeleton are preserved, avoiding unnecessary delete-and-recreate work.
- Removed the local trash setting from the active profile model; older config files with that field remain readable.

### v0.1.5

**Start Work safety**
- Start Work now checks the latest previous session before any local cleanup.
- Cleanup continues only when the previous session is `Completed`, or when no previous session exists.
- `Active`, `Failed`, and `Interrupted` previous sessions block cleanup and show a warning so unpushed local changes are not hidden.
- Session JSON now records a `Status` field while remaining compatible with older session files.
- Edit Profile now returns to the active session view after Cancel, or after saving non-session settings.
- Saving session-affecting profile changes during an active session first ends that session with the original profile settings, then returns to folder selection.
- Sync progress detail now keeps a fixed single-line height so the activity log does not jump when file or folder names vary.

**Local cache cleanup**
- Before the normal skeleton recreation and selected-folder pull, unselected old local cache content is moved to local trash.
- Selected folders are preserved for the existing remote-to-local pull, and ancestor directories are kept only as needed.
- Cleanup preserves app metadata folders, validates paths stay under the local root, and avoids following reparse points during enumeration.

### v0.1.4

**Session workflow**
- Added configurable `SkeletonDepth` for shallow local folder skeleton creation.
- Start Work records `InitialSelectedPaths` and creates only the configured skeleton depth before pulling selected folders.
- Added Add Folder mode during an active session, with existing session folders checked and locked.
- Added Resume Sync to pull only newly selected folders and append them to the active session.

**Final push behavior**
- End Work now categorizes local folders into selected mirror-push folders, unselected non-empty update-only folders, and ignored empty skeleton folders.
- Selected folders keep the existing conflict planning and remote backup behavior before overwrites/deletes.
- Unselected non-empty folders use copy/update behavior and never mirror-delete remote files.
- rclone uses `sync` for selected folders and `copy` for unselected update-only folders.

**Manifest and logs**
- Session JSON records skeleton depth, initial selections, added selections, selected paths, session events, added pull logs, and final push log.
- Push logs include a categorized plan with selected folders, update-only folders, and an empty skeleton folder count.

**UI and docs**
- README now includes the app logo and screenshots.
- Header buttons keep clear contrast in normal, hover, pressed, and disabled states.

### v0.1.3

**Sync progress & responsiveness**
- Pull (Start Session) now shows real-time progress: files scanned, skeleton directories created, folders pulled — each with live per-file/per-dir updates and a determinate progress bar.
- Push (End Session) same treatment: scanning, planning, executing all report live progress.
- Skeleton creation offloaded to background thread via `Task.Run` — UI no longer freezes during large remote tree enumeration.
- Progress detail is a single updating line below the bar; activity log only records milestones, not every file.

**Unselected folder optimization**
- Push now skips empty skeleton directories entirely (single-pass file enumeration instead of N+1 directory+file checks).

**UI / UX**
- Settings page is scrollable.
- Header buttons are styled for visibility on dark banner.
- "Edit Profile" button removed from toolbar (kept in header only).
- All progress text unified to "Synchronizing" / "Syncing" terminology.
- Checkboxes default to unchecked on load; last session selections are remembered and restored on next launch.

**Config & defaults**
- History root now defaults to `<LocalRoot>.pt\history` alongside `<LocalRoot>.pt\logs`.
- Local root Browse picker always refreshes Log and History roots to defaults.
- All `.pfs` references replaced with `.pt`.
- `.pt` folders excluded from all sync operations (tree, scanner, skeleton).
- Metadata folder name centralized in `AppConstants.LocalMetadataDirName`.

**Windows Task Scheduler**
- Task creation now uses multi-trigger support: `AtLogOn` + session unlock (via CIM).
- `--auto` argument handling: time-window check, remote reachability check, diagnostic log at `%TEMP%\PathTwin\auto_launch.log`.
- UAC elevation via `runas` verb with clear error messaging.

**Build & packaging**
- Version bumped to 0.1.3.
- Application icon added (rounded PNG + embedded .ico).
- Package script creates standalone, compressed single-file Windows executables only.

### v0.1.2
- Lazy-loaded directory tree (top-level only on startup, expand to load children).
- Multi-trigger scheduled task support.
- `.pfs` → `.pt` migration.
- Settings scroll and header button styling fixes.
- Checkbox three-state refinement (user toggle only checked/unchecked, indeterminate auto-calculated).

### v0.1.1
- Initial public MVP: Avalonia shell, profile config, tree with checkboxes, pull/push with version backup, single-instance, scheduled task entry point.

### v0.1.0
- Initial scaffolding and core sync engine.

## Repository Layout

```text
src/PathTwin.App/              Avalonia desktop app
src/PathTwin.App/Backends/     rclone/native backend wrappers
src/PathTwin.App/Sync/         scanning, planning, execution, history cleanup
src/PathTwin.App/Platform/     shell, single-instance, Task Scheduler helpers
src/PathTwin.App/Models/       config/session/sync data models
src/PathTwin.App/icon.ico       embedded Windows application icon
```

## Build

Requirements:

- .NET 8 SDK or newer
- Windows for the full desktop/runtime experience

```powershell
dotnet restore src/PathTwin.App/PathTwin.App.csproj
dotnet build src/PathTwin.App/PathTwin.App.csproj
```

## Run

```powershell
dotnet run --project src/PathTwin.App/PathTwin.App.csproj
```

## rclone

PathTwin does not bundle `rclone.exe` in the public release package. Users who want to use rclone can download it from the official rclone downloads page and choose the executable path in Settings:

- https://rclone.org/downloads/

rclone's own downloads page describes rclone as a single executable, `rclone.exe` on Windows, distributed as a zip archive that can be extracted anywhere.

## Publish Windows Standalone

```powershell
scripts/package-release.ps1 -Version 0.1.7
```

The release script creates standalone, self-contained executables that do not require adjacent DLL files:

- `artifacts/PathTwin-0.1.7-win-x64.exe`: versioned release executable
- `artifacts/PathTwin-latest-win-x64.exe`: stable latest executable name

## License

PathTwin is licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE).

PathTwin can use rclone as an optional external tool, but the public release package does not redistribute `rclone.exe`. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Documentation

- [Architecture](docs/ARCHITECTURE.md)
- [Release checklist](docs/RELEASE_CHECKLIST.md)
- [rclone binary policy](docs/RCLONE.md)
- [Icon workflow](docs/ICON.md)
