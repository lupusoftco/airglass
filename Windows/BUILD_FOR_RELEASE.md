# Building AirGlass EXE for Release

One-command build for creating the standalone executable.

## Prerequisites

- Windows 10+ or Windows 11
- .NET 8.0 SDK ([Download](https://dotnet.microsoft.com/download/dotnet/8.0))
- PowerShell (built-in)

## Build

```powershell
cd Windows
.\build-release.ps1
```

Done! Find `AirGlass.exe` in `build/output/`.

## For Release Maintainers

To create the release artifacts:

```powershell
# Build
cd Windows
.\build-release.ps1

# Outputs:
# - build/output/AirGlass.exe (~150 MB standalone)
# - AirGlass-Windows-v0.1.0.zip (~60 MB packaged)

# Upload both to GitHub Release v0.1.0
```

That's it!
