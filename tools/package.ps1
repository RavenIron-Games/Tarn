# Builds the release zip: dist\RavenIron-Tarn-<version>.zip, in Thunderstore's package format (the one Hexium takes).
# Ported from RagnaroksWrath\tools\package.ps1 (2026-10-06), keeping its two guards:
#  - the THREE places the version lives (Plugin.Version, csproj, manifest.json) must agree, or nothing is packaged;
#  - entries are written by hand with forward slashes: PS 5.1's Compress-Archive makes zips Hexium's parser rejects, and
#    .NET Framework's CreateFromDirectory writes backslashes. Store files at the root, the DLL under plugins/.

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

$pluginVer   = (Select-String -Path "$root\Tarn\Plugin.cs" -Pattern 'Version\s*=\s*"([^"]+)"').Matches[0].Groups[1].Value
$csprojVer   = (Select-String -Path "$root\Tarn\Tarn.csproj" -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
$manifestVer = (Get-Content "$root\manifest.json" -Raw | ConvertFrom-Json).version_number
if (($pluginVer -ne $csprojVer) -or ($pluginVer -ne $manifestVer)) {
    Write-Host "VERSION MISMATCH - refusing to package:" -ForegroundColor Red
    Write-Host "  Plugin.Version: $pluginVer"
    Write-Host "  csproj        : $csprojVer"
    Write-Host "  manifest.json : $manifestVer"
    exit 1
}

dotnet build "$root\Tarn\Tarn.csproj" -c Release -v q --nologo
if ($LASTEXITCODE -ne 0) { Write-Host "Build failed." -ForegroundColor Red; exit 1 }
$dll = "$root\Tarn\bin\Release\Tarn.dll"
if (-not (Test-Path $dll)) { Write-Host "No Release DLL at $dll" -ForegroundColor Red; exit 1 }

$dist = "$root\dist"
$stage = "$dist\stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path "$stage\plugins" | Out-Null
Copy-Item "$root\manifest.json", "$root\README.md", "$root\CHANGELOG.md", "$root\icon.png" -Destination $stage
Copy-Item $dll -Destination "$stage\plugins"

$zip = "$dist\RavenIron-Tarn-$pluginVer.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $_.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
Remove-Item $stage -Recurse -Force
Write-Host "Packaged $zip" -ForegroundColor Green
