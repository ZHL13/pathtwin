# rclone Policy

PathTwin can use rclone as an optional external executable. The public source repository and release executables do not include `rclone.exe`.

Official rclone downloads:

- https://rclone.org/downloads/

## Development

For local development, either:

- Download rclone and set its path in the app Settings, or
- Leave it absent and use the native file-system fallback for local/SMB-style paths.

`tools/rclone.exe` remains ignored by Git so developers may keep a local copy there, but official package scripts do not include it.

## Publishing

The release package intentionally excludes rclone:

- Smaller artifact
- No third-party executable redistribution
- Users can update rclone independently
- The app remains clear about which binary is maintained by which project

## GitHub Notes

Do not commit:

- `tools/rclone.exe`
- rclone zip files
- extracted rclone folders

Release assets should contain PathTwin only. Link users to the official rclone download page instead of bundling `rclone.exe`.
