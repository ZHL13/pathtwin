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
4. Check the latest previous session status.
5. Stop before cleanup if the previous session is `Active`, `Failed`, or `Interrupted`.
6. Save the current session as `Active`.
7. Move unselected stale local cache content to local trash while preserving selected folders and needed ancestors.
8. Scan selected remote files into the session manifest.
9. Optionally create the local directory skeleton down to the configured skeleton depth.
10. Pull selected folders from remote root to local root while preserving relative paths.
11. Save the session JSON under `<localRoot>.pt\sessions`.

### Add Folder / Resume Sync

1. Reload the remote tree with current session selections restored and locked.
2. Allow only additional folders to be selected.
3. Append newly selected folders to the active session.
4. Scan newly added remote folders into the manifest baseline.
5. Refresh the configured local skeleton.
6. Pull only newly added folders while preserving relative paths.

### End Work Session

1. Scan base files from the saved manifest.
2. Scan current local selected files.
3. Scan current remote selected files.
4. Build a three-way sync plan for selected folders.
5. Stop if conflicts are found.
6. Back up remote selected-folder files before overwriting or deleting.
7. Mirror-push selected folders.
8. Copy/update unselected non-empty folders without remote mirror deletes.
9. Ignore empty skeleton folders and log their count.
10. Save logs and update the session JSON.

## Safety Rules

- Selected folders are treated as complete working copies.
- Unselected skeleton folders are never used to infer remote deletion.
- New local files in unselected skeleton folders may be uploaded, but missing files are not treated as deletions.
- Remote files are backed up before overwrite/delete.
- Path traversal is blocked by `PathSafety`.
- Reparse points are skipped during enumeration.
- Start Work cleanup refuses to run after unfinished previous sessions.
- Start Work cleanup moves stale unselected local content to `<localRoot>.pt\trash` by default.
- History cleanup only removes clearly named old history folders under the configured history root.

## Current Limitations

- The app currently targets local/SMB-style paths for directory listing and native push execution.
- Full conflict resolution actions are modeled but not fully interactive yet.
- Task Scheduler UI includes wake/logon/unlock settings, while registration currently covers logon/unlock and relies on app-side `--auto` checks for the time window.
- Multi-profile UI is not implemented yet, though config is shaped for it.
