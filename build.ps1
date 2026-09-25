param([switch]$FrameworkDependent, [string]$DotNetPath, [string]$RestoreSource, [switch]$SkipDeliveryCopy)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\AudioFromWhatDevice\AudioFromWhatDevice.csproj'
[string[]]$restoreArgs = if ($RestoreSource) { @("-p:RestoreSources=$RestoreSource") } else { @() }
$tests = Join-Path $PSScriptRoot 'tests\ActivityTests\ActivityTests.csproj'
$output = Join-Path $PSScriptRoot 'artifacts\publish\win-x64'
$localSdk = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
if (-not $DotNetPath) {
    $DotNetPath = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
}
if (-not (& $DotNetPath --list-sdks | Where-Object { $_ -match '^10\.' })) {
    throw 'Install the .NET 10 SDK (x64), then reopen PowerShell. The runtime alone cannot build projects.'
}
& $DotNetPath run --project $tests -c Release @restoreArgs
if ($LASTEXITCODE -ne 0) { throw 'Activity tests failed.' }
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
& $DotNetPath publish $project -c Release -r win-x64 --self-contained $selfContained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false `
    -p:DebugType=None -p:DebugSymbols=false -o $output @restoreArgs
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
if (-not $SkipDeliveryCopy) { Copy-Item -LiteralPath (Join-Path $output 'AudioFromWhatDevice.exe') -Destination (Join-Path $PSScriptRoot 'AudioFromWhatDevice.exe') -Force }
Get-FileHash (Join-Path $output 'AudioFromWhatDevice.exe') -Algorithm SHA256
Write-Host "Published to $output"
