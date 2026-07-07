# Release Checklist

Use this before pushing to GitHub or preparing a Windows release.

## Git Hygiene

```powershell
git status --short
git status --ignored --short
```

Expected ignored local artifacts:

- `publish/`
- `bin/`
- `obj/`
- `tools/rclone.exe`
- `*.pdb`
- logs and temporary files

The repository expects source files, docs, icons, and the icon source to be committed. Build outputs and third-party executables should stay out of Git. GitHub/GitLab release pages provide source archives automatically, so do not build or upload a separate source zip.

## Remote Setup

If the remote is not configured yet, add the public repository URL:

```powershell
git remote add origin https://github.com/<owner>/<repository>.git
git branch -M main
```

Before pushing, confirm the remote is correct:

```powershell
git remote -v
```

## Validate

```powershell
dotnet restore src/PathTwin.App/PathTwin.App.csproj
dotnet build src/PathTwin.App/PathTwin.App.csproj
dotnet run --project tools/iconprocessor/IconProcessor/IconProcessor.csproj
```

## Publish

The public release package does not include `rclone.exe`.

```powershell
scripts/package-release.ps1 -Version 0.1.0
```

Confirm:

- `artifacts/PathTwin-0.1.0-win-x64.exe` exists.
- `artifacts/PathTwin-latest-win-x64.exe` exists.
- The single-file exe runs without adjacent DLL files.
- The app starts and shows the setup/profile screen.
- Logs open from the UI.

## Release Artifact

Upload both release assets:

- `artifacts/PathTwin-0.1.0-win-x64.exe`
- `artifacts/PathTwin-latest-win-x64.exe`

Do not commit `publish/` or `artifacts/`.

Do not upload `tools/rclone.exe`. Link users to https://rclone.org/downloads/ instead.
