# publish-store.ps1
# Builds a Microsoft Store-ready .msixupload bundle (x64 + arm64) for
# zRover.Retriever using the Partner Center identity values.
#
# Output: bin\Store\zRover.Retriever_<version>_x64_arm64.msixupload
#
# The .msixupload file is what you upload to Partner Center > Packages.
# It contains the .msixbundle (which contains per-arch .msix payloads).
# Store re-signs on ingestion, so the local cert used here is irrelevant
# to end users — only the manifest Identity/Publisher must match the
# values Partner Center assigned to your app.
#
# Usage:
#   .\publish-store.ps1                  # x64 + arm64 (default)
#   .\publish-store.ps1 -Archs x64       # single arch
#
# Prereqs:
#   - .NET 9 SDK
#   - Windows SDK (makeappx.exe, signtool.exe) — auto-located from
#     microsoft.windows.sdk.buildtools NuGet or installed Windows Kits.

param(
    [string[]] $Archs   = @('x64','arm64'),
    [ValidateSet('Debug','Release')]
    [string]   $Config  = 'Release'
)

$ErrorActionPreference = 'Stop'
$ProjectDir   = $PSScriptRoot
$ProjectFile  = Join-Path $ProjectDir 'zRover.Retriever.csproj'
$ManifestPath = Join-Path $ProjectDir 'Package.appxmanifest'

# ── Store identity (from Partner Center > Product identity) ──────────────────
$StoreIdentityName       = '58996ARCADIOGARCA.zRoverRetriever'
$StorePublisher          = 'CN=5F63796F-84F4-4C45-860F-616E76512FFB'
$StorePublisherDisplay   = 'ARCADIO GARCÍA'

# ── Read version ─────────────────────────────────────────────────────────────
$xml = [xml](Get-Content $ManifestPath -Raw)
$ns  = @{ a = 'http://schemas.microsoft.com/appx/manifest/foundation/windows10' }
$Version = (Select-Xml -Xml $xml -XPath '/a:Package/a:Identity/@Version' -Namespace $ns).Node.Value
if (-not $Version) { throw 'Could not read Version from Package.appxmanifest' }
Write-Host "=== Store build: zRover.Retriever $Version ===" -ForegroundColor Cyan
Write-Host "Identity:  $StoreIdentityName"
Write-Host "Publisher: $StorePublisher"
Write-Host "Archs:     $($Archs -join ', ')"

# ── Locate SDK tools ─────────────────────────────────────────────────────────
function Find-SdkTool([string] $name) {
    $tool = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" `
        -Recurse -Filter $name -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $tool) {
        $tool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' `
            -Recurse -Filter $name -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $tool) { throw "$name not found. Restore NuGet or install Windows SDK." }
    return $tool
}
$makeappx = Find-SdkTool 'makeappx.exe'
$signtool = Find-SdkTool 'signtool.exe'
Write-Host "makeappx: $makeappx"
Write-Host "signtool: $signtool"

# ── Output dirs ──────────────────────────────────────────────────────────────
$StoreDir   = Join-Path $ProjectDir 'bin\Store'
$BundleDir  = Join-Path $StoreDir   'bundle'  # holds per-arch .msix files
if (Test-Path $StoreDir)  { Remove-Item $StoreDir -Recurse -Force }
New-Item -ItemType Directory -Path $BundleDir -Force | Out-Null

# ── Backup the dev manifest, patch identity, restore on exit ─────────────────
$ManifestBackup = "$ManifestPath.devbackup"
Copy-Item $ManifestPath $ManifestBackup -Force

try {
    # Patch identity to Store values
    $manifestXml = [xml](Get-Content $ManifestPath -Raw)
    $nsm = New-Object System.Xml.XmlNamespaceManager($manifestXml.NameTable)
    $nsm.AddNamespace('m', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')

    $idNode = $manifestXml.SelectSingleNode('//m:Identity', $nsm)
    $idNode.Name      = $StoreIdentityName
    $idNode.Publisher = $StorePublisher

    $propsPub = $manifestXml.SelectSingleNode('//m:Properties/m:PublisherDisplayName', $nsm)
    $propsPub.InnerText = $StorePublisherDisplay

    Write-Host "`nPatched manifest with Store identity." -ForegroundColor Green

    # ── Build each arch ──────────────────────────────────────────────────────
    foreach ($arch in $Archs) {
        Write-Host "`n--- $arch ---" -ForegroundColor Yellow
        $rid = "win-$arch"

        # Per-arch manifest with ProcessorArchitecture set
        $idNode.SetAttribute('ProcessorArchitecture', $arch)
        $manifestXml.Save($ManifestPath)

        Write-Host "Publishing $rid..."
        dotnet publish $ProjectFile -c $Config -r $rid /p:AppxPackage=false /p:GenerateAppxPackageOnBuild=false | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "publish failed for $arch" }

        $buildOutDir = Join-Path $ProjectDir "bin\$Config\net9.0-windows10.0.19041.0\$rid"
        if (-not (Test-Path $buildOutDir)) { throw "Build output not found: $buildOutDir" }

        # Stage layout — copy build output, write AppxManifest, normalize PRI
        $layoutDir = Join-Path $StoreDir "layout-$arch"
        if (Test-Path $layoutDir) { Remove-Item $layoutDir -Recurse -Force }
        New-Item -ItemType Directory -Path $layoutDir -Force | Out-Null

        Copy-Item "$buildOutDir\*" $layoutDir -Recurse -Force
        # Drop the publish\ subdirectory (duplicate DLLs — would inflate package)
        $publishSubdir = Join-Path $layoutDir 'publish'
        if (Test-Path $publishSubdir) { Remove-Item $publishSubdir -Recurse -Force }

        # AppxManifest.xml in the layout (must be the patched one)
        Copy-Item $ManifestPath (Join-Path $layoutDir 'AppxManifest.xml') -Force

        # resources.pri (makeappx requires this exact name)
        $priSrc = Get-ChildItem $layoutDir -Filter '*.pri' -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ne 'resources.pri' } | Select-Object -First 1
        if ($priSrc) {
            Move-Item $priSrc.FullName (Join-Path $layoutDir 'resources.pri') -Force
        } elseif (-not (Test-Path (Join-Path $layoutDir 'resources.pri'))) {
            throw "No .pri file in $layoutDir"
        }

        # Pack the per-arch .msix
        $msixPath = Join-Path $BundleDir "zRover.Retriever_${Version}_$arch.msix"
        Write-Host "Packing $msixPath"
        & $makeappx pack /d "$layoutDir" /p "$msixPath" /o | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed for $arch" }

        Remove-Item $layoutDir -Recurse -Force
    }

    # ── Bundle the per-arch MSIX files into a .msixbundle ───────────────────
    $bundlePath = Join-Path $StoreDir "zRover.Retriever_${Version}_$($Archs -join '_').msixbundle"
    Write-Host "`nBundling -> $bundlePath" -ForegroundColor Cyan
    & $makeappx bundle /d "$BundleDir" /p "$bundlePath" /bv $Version /o | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'makeappx bundle failed' }

    # ── Wrap into .msixupload (the file Partner Center accepts) ─────────────
    # An .msixupload is a plain ZIP of the .msixbundle (and optionally .appxsym
    # symbol bundles, which we omit here).
    $uploadPath = Join-Path $StoreDir "zRover.Retriever_${Version}_$($Archs -join '_').msixupload"
    if (Test-Path $uploadPath) { Remove-Item $uploadPath -Force }

    Write-Host "Wrapping -> $uploadPath" -ForegroundColor Cyan
    Compress-Archive -Path $bundlePath -DestinationPath $uploadPath -CompressionLevel Optimal -Force

    # Cleanup the per-arch .msix files (they're inside the bundle now)
    Remove-Item $BundleDir -Recurse -Force

    Write-Host ""
    Write-Host "===================================================" -ForegroundColor Green
    Write-Host " Store package ready" -ForegroundColor Green
    Write-Host "===================================================" -ForegroundColor Green
    Write-Host "  Upload to Partner Center:" -ForegroundColor Green
    Write-Host "    $uploadPath" -ForegroundColor White
    Write-Host ""
    Write-Host "  Also keep the bundle locally for reference:"
    Write-Host "    $bundlePath"
    Write-Host ""
    Write-Host "Next steps:"
    Write-Host "  1. Open Partner Center > zRover Retriever > Submissions"
    Write-Host "  2. Add the .msixupload under 'Packages'"
    Write-Host "  3. Provide capability justifications for:"
    Write-Host "       - appDiagnostics (terminate packaged apps via AppDiagnosticInfo)"
    Write-Host "       - packageManagement (install/uninstall MSIX packages on user request)"
    Write-Host "  4. Submit"
}
finally {
    # Always restore the dev manifest so subsequent local builds work
    Move-Item $ManifestBackup $ManifestPath -Force
    Write-Host "`nRestored dev manifest." -ForegroundColor DarkGray
}
