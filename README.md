# PathTwin

PathTwin is a Windows-first desktop app for selective, session-based folder synchronization.

It is not meant to mirror an entire drive. The app is built around a work session: choose a remote root, choose a local root, select only the folders needed for this session, pull them locally with their relative paths preserved, work locally, then end the session and safely push changes back.

## Current MVP

- Avalonia UI on .NET 8
- JSON configuration under the user profile
- One active profile, with config shaped for multiple profiles later
- Native remote directory tree loading for local/SMB paths
- Checkbox folder selection with parent/child behavior
- Start Work Session pull
- Session manifest saved under `<localRoot>.pfs\sessions`
- End Work Session push with three-way planning
- Remote backup before overwrite/delete
- Basic conflict/error window and logs
- Single-instance guard
- Optional Windows scheduled task entry point via `--auto`
- Bundled-rclone support, with native file-system fallback for development/local SMB paths

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
tools/rclone.exe               optional local binary, ignored by Git
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

## Publish Windows Standalone

Place `rclone.exe` at `tools/rclone.exe` before publishing if the release should include rclone. The file is intentionally ignored by Git.

```powershell
dotnet publish src/PathTwin.App/PathTwin.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o publish/win-x64
```

The publish output is ignored by Git and should be treated as a release artifact, not source.

For the packaged release zip with notices included:

```powershell
scripts/package-release.ps1 -Version 0.1.0
```

## License

PathTwin is licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE).

Binary releases may bundle rclone as a separate third-party executable. rclone is MIT licensed; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Documentation

- [Architecture](docs/ARCHITECTURE.md)
- [Release checklist](docs/RELEASE_CHECKLIST.md)
- [rclone binary policy](docs/RCLONE.md)
- [Icon workflow](docs/ICON.md)
