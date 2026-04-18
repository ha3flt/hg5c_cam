param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$setupProject = Join-Path $repoRoot 'hg5c_cam.Setup\hg5c_cam.Setup.wixproj'

if (-not (Test-Path $setupProject)) {
    Write-Error "Setup project not found: $setupProject"
}

Write-Host "`nDo NOT forget to increase all the VERSION numbers (hg5c_cam.csproj, Product.wxs)." -ForegroundColor Yellow

if (-not $NoPause) {
    Read-Host "Press Enter to continue or Ctrl+C to stop"
}

Write-Host "Building MSI from: $setupProject" -ForegroundColor Cyan

dotnet build $setupProject -c $Configuration -p:Platform=x64 -p:InstallerPlatform=x64
$exitCode = $LASTEXITCODE

if ($exitCode -eq 0) {
    Write-Host "`nMSI build succeeded." -ForegroundColor Green
} else {
    Write-Host "`nMSI build failed (exit code: $exitCode)." -ForegroundColor Red
}

if (-not $NoPause) {
    Read-Host "Press Enter to close"
}

exit $exitCode
