# Builds a release of CNC Studio for installing on another computer:
# one self-contained "CNC Studio.exe" (no .NET installation needed on the target) in
#   <CNC Studio-mappen>\Programversioner\CNC Studio <version>\
# The CNC Studio folder is the sync folder chosen in the app (Arkiv -> Synkmapp), else Dokument\CNC Studio.
# Run:  powershell -ExecutionPolicy Bypass -File tools\publish.ps1   [-Target <folder>]
param([string]$Target)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# Version from the window title constant, e.g. "CNC Studio 0.8.5".
$cam = Get-Content (Join-Path $root "src\CNCStudio.Desktop\MainWindow.Cam.cs") -Raw -Encoding UTF8
$version = [regex]::Match($cam, 'AppTitle\s*=\s*"CNC Studio ([0-9.]+)"').Groups[1].Value
if (-not $version) { throw "Hittar inte versionsnumret (AppTitle) i MainWindow.Cam.cs." }

if (-not $Target) {
    $settings = Join-Path $env:APPDATA "CNCStudio\settings.json"
    $data = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "CNC Studio"
    if (Test-Path $settings) {
        $sync = (Get-Content $settings -Raw | ConvertFrom-Json).SyncFolder
        if ($sync -and (Test-Path $sync)) { $data = $sync }
    }
    $Target = Join-Path $data "Programversioner"
}
$out = Join-Path $Target "CNC Studio $version"
if (Test-Path $out) { throw "$out finns redan. Höj versionen eller ta bort mappen först." }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("cncstudio-publish-" + [guid]::NewGuid().ToString("N"))
dotnet publish (Join-Path $root "src\CNCStudio.Desktop\CNCStudio.Desktop.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:Version=$version -o $tmp --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish misslyckades." }

New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item (Join-Path $tmp "CNCStudio.Desktop.exe") (Join-Path $out "CNC Studio.exe")
Remove-Item $tmp -Recurse -Force

$readme = @"
CNC Studio $version
===================

Installera på en ny dator
1. Kopiera mappen "CNC Studio $version" till datorn, t.ex. till C:\Program\CNC Studio $version
   (eller kör direkt härifrån från Google Drive – första starten tar då några sekunder längre).
2. Starta "CNC Studio.exe". Inget annat behöver installeras (.NET följer med i filen).
3. Högerklicka på exe-filen -> "Fäst i Start" eller "Skicka till -> Skrivbord (skapa genväg)".
4. I CNC Studio: Arkiv -> Synkmapp (Google Drive) -> välj samma CNC Studio-mapp som på den andra datorn.
   Då delar datorerna projekt, verktygsbibliotek, materialbibliotek och maskinprofil.

Windows kan visa "Windows skyddade datorn" första gången, eftersom programmet inte är signerat.
Klicka "Mer information" -> "Kör ändå".

Byggd $(Get-Date -Format "yyyy-MM-dd HH:mm") från git-taggen v$version.
"@
Set-Content -Path (Join-Path $out "Installera.txt") -Value $readme -Encoding UTF8
"Klart: $out"
Get-ChildItem $out | Select-Object Name, @{n="MB";e={[math]::Round($_.Length / 1MB, 1)}}
