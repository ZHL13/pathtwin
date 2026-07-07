# Architecture

PathTwin is organized as a small Avalonia app with explicit service boundaries. The MVP keeps implementation simple but leaves clear seams for future profile management, conflict resolution UI, and alternate sync backends.

## App Startup

`Program.cs` enforces single-instance mode before starting Avalonia. When launched with `--auto`, it checks the saved profile, the configured startup time window, and remote-root reachability before showing the window.

`App.axaml.cs` wires the app manually:

- `ConfigService` for JSON config
- `LogService` for operation logs
- `DirectoryTreeService` for native directory enumeration
- `WorkSessionService` for Start/End Work Session orchestration
- `SyncBackendFactory` for rclone/native backend selection
- `SyncPlanner` and `SyncExecutor` for push planning and execution

## Main Flows

### Start Work Session

1. Validate the active profile.
2. Normalize selected relative paths.
3. Create a timestamp session id.
4. Scan selected remote files into the session manifest.
5. Optionally create the local directory skeleton.
6. Pull selected folders from remote root to local root while preserving relative paths.
7. Save the session JSON under `<localRoot>.pfs\sessions`.

### End Work Session

1. Scan base files from the saved manifest.
2. Scan current local selected files.
3. Scan current remote selected files.
4. Build a three-way sync plan.
5. Stop if conflicts are found.
6. Back up remote files before overwriting or deleting.
7. Apply uploads/deletions.
8. Save logs and update the session JSON.

## Safety Rules

- Selected folders are treated as complete working copies.
- Unselected skeleton folders are never used to infer remote deletion.
- New local files in unselected skeleton folders may be uploaded, but missing files are not treated as deletions.
- Remote files are backed up before overwrite/delete.
- Path traversal is blocked by `PathSafety`.
- Reparse points are skipped during enumeration.
- History cleanup only removes clearly named old history folders under the configured history root.

## Current Limitations

- The app currently targets local/SMB-style paths for directory listing and native push execution.
- Full conflict resolution actions are modeled but not fully interactive yet.
- Task Scheduler UI includes wake/logon/unlock settings, while registration currently covers logon/unlock and relies on app-side `--auto` checks for the time window.
- Multi-profile UI is not implemented yet, though config is shaped for it.
