param(
    [string]$Executable,
    [string]$ExpectedBluetoothEndpointId,
    [string]$ExpectedSpeakerEndpointId,
    [string]$ExpectedDisplayedEndpointId,
    [Alias("RequireSilentDevice")][switch]$RequireIdleSelection
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Executable) { $Executable = Join-Path $root 'artifacts\publish\win-x64\AudioFromWhatDevice.exe' }
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$testRoot = Join-Path $root ('artifacts\exe-smoke-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$copy = Join-Path $testRoot 'AudioFromWhatDevice.exe'
Copy-Item -LiteralPath $Executable -Destination $copy
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $testRoot 'bundle'
$env:DOTNET_HOST_TRACE = '1'
$env:DOTNET_HOST_TRACEFILE = Join-Path $testRoot 'host-trace.log'
$probeFile = Join-Path $testRoot 'probe.json'
$probe = Start-Process -FilePath $copy -ArgumentList @('--probe', ('"' + $probeFile + '"')) -WorkingDirectory $testRoot -WindowStyle Hidden -PassThru
if (-not $probe.WaitForExit(45000)) { Stop-Process -Id $probe.Id; throw 'Probe timed out.' }
if ($probe.ExitCode -ne 0) { throw "Probe exit code: $($probe.ExitCode)" }
$result = Get-Content -LiteralPath $probeFile -Raw | ConvertFrom-Json
if ($result.Error -or @($result.Devices | Where-Object Error).Count -gt 0) { throw 'Endpoint probe reported errors.' }
$trayFile = Join-Path $testRoot 'tray.json'
$tray = Start-Process -FilePath $copy -ArgumentList @('--smoke', ('"' + $trayFile + '"')) -WorkingDirectory $testRoot -WindowStyle Hidden -PassThru
if (-not $tray.WaitForExit(45000)) { Stop-Process -Id $tray.Id; throw 'Tray smoke timed out.' }
if ($tray.ExitCode -ne 0) { throw "Tray exit code: $($tray.ExitCode)" }
$trayResult = Get-Content -LiteralPath $trayFile -Raw | ConvertFrom-Json
if ($trayResult.Error -or @($trayResult.Devices | Where-Object Error).Count -gt 0) { throw 'Tray reported errors.' }
$badges = @(Get-Content ($trayFile + '.badges.json') -Raw | ConvertFrom-Json)
if ($badges.Count -ne 1 -or @($badges | Where-Object { [string]::IsNullOrWhiteSpace($_.Text) }).Count -gt 0) { throw 'Expected exactly one named tray badge.' }
if (-not (Test-Path -LiteralPath ($trayFile + '.png'))) { throw 'Tray visual preview is missing.' }
$available = @($trayResult.Devices | Where-Object IsAvailable)
$playing = @($available | Where-Object { $_.Activity -in @('Signal', 'Recent') })
$selected = @($available | Where-Object { $_.Id -eq $badges[0].Id })
$selectedDevice = $null
if ($available.Count -gt 0) {
    if ($selected.Count -ne 1 -or $badges[0].Text -in @('', '—', '!')) { throw 'Single icon must identify an available device.' }
    $selectedDevice = $selected[0]
    $expectedColor = if ($selectedDevice.IsBluetooth) { '#4169E1' } else { '#12704A' }
    if ($badges[0].BackgroundColor -ne $expectedColor -or -not $badges[0].Description.Contains($selectedDevice.Name)) {
        throw 'Selected device name or transport color is incorrect.'
    }
    $candidates = if ($playing.Count -gt 0) { $playing } else { $available }
    if ($selectedDevice.Id -notin $candidates.Id) { throw 'Single tray icon did not prioritize real audio output.' }
    if ($trayResult.DefaultOutputId -in $candidates.Id -and $selectedDevice.Id -ne $trayResult.DefaultOutputId) {
        throw 'Expected the default device among equally eligible endpoints.'
    }
}
if ($RequireIdleSelection -and ($playing.Count -ne 0 -or $null -eq $selectedDevice -or
    $selectedDevice.Id -ne $trayResult.DefaultOutputId)) {
    throw 'Idle verification requires no playback and a single named default device badge.'
}
if ($ExpectedDisplayedEndpointId -and $badges[0].Id -ne $ExpectedDisplayedEndpointId) {
    throw 'Displayed endpoint differs from the expected real device.'
}
if ($ExpectedBluetoothEndpointId) {
    $device = @($available | Where-Object { $_.Id -eq $ExpectedBluetoothEndpointId })
    if ($device.Count -ne 1 -or -not $device[0].IsBluetooth) { throw 'Expected Bluetooth endpoint was not classified as Bluetooth.' }
}
if ($ExpectedSpeakerEndpointId) {
    $device = @($available | Where-Object { $_.Id -eq $ExpectedSpeakerEndpointId })
    if ($device.Count -ne 1 -or $device[0].IsBluetooth) { throw 'Expected speaker endpoint was missing or misclassified as Bluetooth.' }
}
# .NET 10 can statically include CoreCLR in the single-file host, without extracting coreclr.dll.
$trace = Get-Content (Join-Path $testRoot 'host-trace.log') -Raw
foreach ($marker in @('Detected Single-File app bundle', 'Using internal fxr', 'Executing as a self-contained app', 'is_framework_dependent=0', 'System.Private.CoreLib.dll found in bundle', 'System.Windows.Forms.dll found in bundle')) {
    if (-not $trace.Contains($marker)) { throw "Missing self-contained runtime evidence: $marker" }
}
[pscustomobject]@{
    Result = 'PASS'
    Executable = $Executable
    SHA256 = (Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash
    SizeBytes = (Get-Item -LiteralPath $Executable).Length
    DeviceCount = @($result.Devices).Count
    ConnectedDeviceCount = $available.Count
    TrayIconCount = $badges.Count
    DefaultOutputId = $trayResult.DefaultOutputId
    SelectedDeviceName = $selectedDevice.Name
    SelectedDeviceActivity = $selectedDevice.Activity
    IdleDefaultConfirmed = $playing.Count -eq 0 -and $null -ne $selectedDevice -and $selectedDevice.Id -eq $trayResult.DefaultOutputId
    TrayBadgeTexts = @($badges | ForEach-Object Text)
    TrayBadgeColors = @($badges | ForEach-Object BackgroundColor)
    BluetoothHardwareChecked = [bool]$ExpectedBluetoothEndpointId
    SpeakerHardwareChecked = [bool]$ExpectedSpeakerEndpointId
    TrayPreview = $trayFile + '.png'
    EmbeddedRuntimeConfirmed = $true
    TestDirectory = $testRoot
} | ConvertTo-Json | Tee-Object -FilePath (Join-Path $testRoot 'verification.json')