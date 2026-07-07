# Icon Workflow

The final repository icon is:

```text
icon.png
```

It is generated as a 1000x1000 transparent PNG with rounded corners. The original source image is preserved at:

```text
assets/icon-source.png
```

The Windows app icon is:

```text
src/PathTwin.App/icon.ico
```

## Regenerate Icons

Run:

```powershell
dotnet run --project tools/iconprocessor/IconProcessor/IconProcessor.csproj
```

The icon processor:

1. Reads `assets/icon-source.png`.
2. Detects the non-background pixel bounds.
3. Expands the crop to a square.
4. Resizes to 1000x1000.
5. Applies a transparent rounded-rectangle mask.
6. Writes `icon.png`.
7. Writes the multi-size `src/PathTwin.App/icon.ico`.

Current generated crop:

```text
Detected bounds: X=28, Y=27, Width=1199, Height=1200
Square crop: X=27, Y=27, Width=1200, Height=1200
```
