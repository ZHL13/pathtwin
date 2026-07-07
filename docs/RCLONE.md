# rclone Binary Policy

PathTwin supports a bundled rclone executable at:

```text
tools/rclone.exe
```

That file is intentionally ignored by Git. Keep the source repository lightweight and avoid committing third-party binaries directly.

## Development

For local development, either:

- Place `rclone.exe` at `tools/rclone.exe`, or
- Leave it absent and use the native file-system fallback for local/SMB-style paths.

The app default path is centralized in `AppConstants.DefaultRclonePath`.

## Publishing

Before publishing a release that should include rclone:

1. Download rclone for Windows x64 from the official rclone distribution.
2. Put the executable at `tools/rclone.exe`.
3. Run the publish command from the README.

The project file copies `tools/rclone.exe` into the published app folder when the file exists.

## GitHub Notes

Do not commit:

- `tools/rclone.exe`
- rclone zip files
- extracted rclone folders

For a public release, include rclone licensing/notice information in the packaged artifact.

This repository includes a copy of rclone's license text at `third_party/rclone/COPYING` for packaging notices. The rclone executable itself stays ignored by Git.
