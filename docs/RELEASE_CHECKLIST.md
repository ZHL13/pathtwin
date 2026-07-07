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

The repository currently expects source files, docs, icons, and the icon source to be committed. Build outputs and third-party executables should stay out of Git.

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

Place `rclone.exe` at `tools/rclone.exe` first if it should be included in the release package.

```powershell
scripts/package-release.ps1 -Version 0.1.0
```

Confirm:

- `publish/win-x64/PathTwin.App.exe` exists.
- `publish/win-x64/tools/rclone.exe` exists when bundling rclone.
- `artifacts/PathTwin-0.1.0-win-x64.zip` exists.
- The app starts and shows the setup/profile screen.
- Logs open from the UI.

## Release Artifact

Upload `artifacts/PathTwin-0.1.0-win-x64.zip`. Do not commit `publish/` or `artifacts/`.

If rclone is bundled, include `THIRD_PARTY_NOTICES.md` and `third_party/rclone/COPYING` in the release artifact.
