[CmdletBinding()]
param
(
    [string]
    [ValidateSet("X64", "arm64")]
    [Parameter(Mandatory = $true, Position = 0)]
    $Platform
)

if (-not $Platform) {
    Write-Error "Platform parameter is required. Use 'X64' or 'arm64'."
    exit 1
}

$PlatformLower = $Platform.ToLowerInvariant()
$UiPath = $PSScriptRoot
$BinPath = Join-Path $UiPath "bin"
$MsixPath = Join-Path $BinPath "MSIX"
$BuildPath = Join-Path $BinPath "$Platform\Release\net9.0-windows10.0.22621.0\win-$PlatformLower"
$PublishPath = Join-Path $BuildPath "publish"

Remove-Item $BuildPath -Recurse -Force -ErrorAction Ignore

msbuild -m -target:Publish -p:Configuration=Release -p:Platform=$Platform -p:RuntimeIdentifier=win-$PlatformLower -p:NoWarn=1591 ..\InstantTraceViewerUI\InstantTraceViewerUI.csproj

# Patch up the AppxManifest.xml
$AppxManifest = Join-Path $PublishPath "AppxManifest.xml"
[xml]$manifest = Get-Content $AppxManifest
$manifest.Package.Identity.ProcessorArchitecture = $PlatformLower
$manifest.Save($AppxManifest)

# Generate resources.pri files
# makepri createconfig /cf $PublishPath\priconfig.xml /dq lang-en-US /pv 10.0.0 /o
# makepri new /cf $PublishPath\priconfig.xml /pr $PublishPath /of $PublishPath/resources.pri /o
# Remove-Item $PublishPath\priconfig.xml

makeappx pack /d $PublishPath /p $MsixPath\InstantTraceViewer_$Platform.msix /o