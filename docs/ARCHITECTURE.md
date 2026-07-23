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

## Comparison Modes

Profiles choose one comparison mode. `Hybrid` is the default: matching size and modification time are accepted immediately, while mismatched local/remote files are hashed before their content is considered different. `Fast` uses only metadata. `Content` uses SHA-256 for file equality and rclone `--checksum` when rclone is active.

With `Use rclone` enabled and a valid executable selected, remote file inventories use rclone `lsjson`; otherwise PathTwin uses the native scanner.

## Main Flows

### Start Work Session

1. Validate the active profile.
2. Normalize selected relative paths.
3. Create a timestamp session id.
4. Check the latest previous session status.
5. Stop before cleanup if the previous session is `Active`, `Failed`, or `Interrupted`. Startup surfaces its saved failure phase, error summary, last recorded activity, and log location.
6. Only the explicit force-sync action may ignore that prior status. The decision is logged in the new session's app log.
7. Save the current session as `Active`.
8. With the native backend, scan selected remote files into the session manifest while a bounded single-worker queue immediately applies candidate pulls. rclone first records its remote inventory, then performs the selected transfer.
9. Compare the current shallow remote skeleton down to the configured skeleton depth.
10. Send stale unselected local cache content to the Windows Recycle Bin while preserving selected folders, needed ancestors, and current skeleton directories.
11. Create any missing local skeleton directories.
12. Pull independent top-level selected folders from remote root to local root through a single transfer queue, preserving relative paths.
13. Save the session JSON under `<localRoot>.pt\sessions`.

The native backend emits throttled progress while it builds folder skeletons, checks source/destination files, performs Hybrid hash comparisons, and removes mirror-only entries. This keeps long SMB operations visible without flooding the UI event queue. Its pull queue is bounded and single-worker, so scan and transfer overlap without competing for network bandwidth.

When rclone is active, Hybrid first lists both roots with `rclone lsjson` and compares size plus UTC modification time. Only mismatched relative paths are passed to `rclone copy --files-from-raw ... --checksum`, which performs the content check and transfers only files whose contents differ. That command streams throttled file events and a heartbeat to the UI. Mirror-only deletions follow the content check. Empty directories are listed through rclone; local and SMB destinations create them directly off the UI thread, while named rclone remotes retain rclone directory commands. `Content` uses rclone `--checksum` for the whole transfer; `Fast` uses metadata only.

### Add Folder / Resume Sync

1. Reload the remote tree with current session selections restored and locked.
2. Allow only additional folders to be selected.
3. Append newly selected folders to the active session.
4. Scan newly added remote folders into the manifest baseline.
5. Refresh the configured local skeleton.
6. Pull only newly added folders while preserving relative paths.

### End Work Session

1. Scan base files from the saved manifest.
2. Scan current local and remote selected files concurrently.
3. Compare a file as soon as both sides have been scanned; queue confirmed file changes immediately in the single transfer queue.
4. Complete comparisons for files that are missing on either side after both scans finish.
5. On conflict, stop queuing new changes; work already started remains completed and is reported with the conflict.
6. Back up remote selected-folder files before overwriting or deleting.
7. For rclone, run a final selected-folder reconciliation pass to preserve mirror semantics for empty directories.
8. Copy/update independent unselected non-empty folders through the same single transfer queue, without remote mirror deletes.
9. Ignore empty skeleton folders and log their count.
10. Save logs and update the session JSON.

## Safety Rules

- Selected folders are treated as complete working copies.
- Unselected skeleton folders are never used to infer remote deletion.
- New local files in unselected skeleton folders may be uploaded, but missing files are not treated as deletions.
- Remote files are backed up before overwrite/delete.
- Path traversal is blocked by `PathSafety`.
- Reparse points are skipped during enumeration.
- Start Work cleanup refuses to run after unfinished previous sessions unless the user chooses the explicit force-sync action after reviewing the displayed diagnostics.
- Failed and interrupted session records persist the active phase and error summary; older records fall back to the latest `ERROR:` entry in their app log when available.
- Every window-close attempt is confirmed. The dialog shows the current workflow state and operation; force exit warns when a transfer can be interrupted or an active session can remain unpushed.
- Start Work cleanup sends stale unselected local content to the Windows Recycle Bin.
- Start Work cleanup preserves current skeleton directories to avoid delete-and-recreate churn.
- History cleanup only removes clearly named old history folders under the configured history root.

## Current Limitations

- The app currently targets local/SMB-style paths for directory listing and native push execution.
- Full conflict resolution actions are modeled but not fully interactive yet.
- Task Scheduler UI includes wake/logon/unlock settings, while registration currently covers logon/unlock and relies on app-side `--auto` checks for the time window.
- Multi-profile UI is not implemented yet, though config is shaped for it.
