# rclone Policy

PathTwin can use rclone as an optional external executable. The public source repository and release executables do not include `rclone.exe`.

Official rclone downloads:

- https://rclone.org/downloads/

## Development

For local development, download rclone independently and set its path in the app Settings, or leave it absent and use the native file-system fallback for local/SMB-style paths.

## Publishing

The release package intentionally excludes rclone:

- Smaller artifact
- No third-party executable redistribution
- Users can update rclone independently
- The app remains clear about which binary is maintained by which project

## GitHub Notes

Do not commit rclone binaries, zip files, or extracted rclone folders.

Release assets should contain PathTwin only. Link users to the official rclone download page instead of bundling `rclone.exe`.
