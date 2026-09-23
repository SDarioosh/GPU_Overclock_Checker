# Builds a single self-contained GpuOcChecker.exe into .\publish (needs the .NET 8 SDK).
$ErrorActionPreference = "Stop"
dotnet test -c Release
dotnet publish src/GpuOcChecker -c Release -r win-x64 -o publish
Write-Host "`nDone: $(Resolve-Path publish\GpuOcChecker.exe)"
