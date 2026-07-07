# PathTwin

PathTwin is a Windows-first desktop app for selective, session-based folder synchronization.

It is not meant to mirror an entire drive. The app is built around a work session: choose a remote root, choose a local root, select only the folders needed for this session, pull them locally with their relative paths preserved, work locally, then end the session and safely push changes back.

## Current MVP

- Avalonia UI on .NET 8
- JSON configuration under the user profile
- One active profile, with config shaped for multiple profiles later
- Native remote directory tree loading for local/SMB paths
- Lightweight remote root picker that only lists the current folder level
- Checkbox folder selection with parent/child behavior
- Start Work Session pull
- Session manifest saved under `<localRoot>.pfs\sessions`
- End Work Session push with three-way planning
- Remote backup before overwrite/delete
- Basic conflict/error window and logs
- Single-instance guard
- Optional Windows scheduled task entry point via `--auto`
- Optional rclone backend path, with native file-system fallback for development/local SMB paths

## Repository Layout

```text
src/PathTwin.App/              Avalonia desktop app
src/PathTwin.App/Backends/     rclone/native backend wrappers
src/PathTwin.App/Sync/         scanning, planning, execution, history cleanup
src/PathTwin.App/Platform/     shell, single-instance, Task Scheduler helpers
src/PathTwin.App/Models/       config/session/sync data models
tools/iconprocessor/           local tool for regenerating app icons
assets/icon-source.png         original icon source image
icon.png                       final 1000x1000 transparent PNG icon
tools/rclone.exe               optional local binary, ignored by Git and not packaged by default
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
scripts/package-release.ps1 -Version 0.1.2
```

The release script creates standalone, self-contained executables that do not require adjacent DLL files:

- `artifacts/PathTwin-0.1.2-win-x64.exe`: versioned release executable
- `artifacts/PathTwin-latest-win-x64.exe`: stable latest executable name

## License

PathTwin is licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE).

PathTwin can use rclone as an optional external tool, but the public release package does not redistribute `rclone.exe`. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Documentation

- [Architecture](docs/ARCHITECTURE.md)
- [Release checklist](docs/RELEASE_CHECKLIST.md)
- [rclone binary policy](docs/RCLONE.md)
- [Icon workflow](docs/ICON.md)
