# Optional verification fallback when SDK download is unavailable.
# Requires PowerShell 7.6+ (bundled Roslyn) and .NET 10 Desktop Runtime x64.
# This compiles runnable DLLs; it does NOT exercise SDK/MSBuild or self-contained publishing.
param([string]$RuntimeVersion = '10.0.12')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$runtimeRoot = Join-Path $env:ProgramFiles 'dotnet\shared'
$core = Join-Path $runtimeRoot "Microsoft.NETCore.App\$RuntimeVersion"
$desktop = Join-Path $runtimeRoot "Microsoft.WindowsDesktop.App\$RuntimeVersion"
$output = Join-Path $root 'artifacts\local-validation'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$null = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]

function Compile-Local([string]$Name, [string[]]$Files, [bool]$Gui) {
    $trees = [System.Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
    $usings = 'global using System; global using System.IO; global using System.Linq; global using System.Threading; global using System.Collections.Generic;'
    if ($Gui) { $usings += 'global using System.Drawing; global using System.Windows.Forms;' }
    $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($usings))
    foreach ($file in $Files) {
        $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText([IO.File]::ReadAllText($file), $null, $file))
    }
    $references = [System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
    $assemblies = @{}
    foreach ($file in Get-ChildItem $core -Filter '*.dll') { $assemblies[$file.Name] = $file.FullName }
    if ($Gui) { foreach ($file in Get-ChildItem $desktop -Filter '*.dll') { $assemblies[$file.Name] = $file.FullName } }
    foreach ($file in $assemblies.Values) {
        try { $null = [Reflection.AssemblyName]::GetAssemblyName($file) } catch { continue }
        $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file))
    }
    $kind = if ($Gui) { [Microsoft.CodeAnalysis.OutputKind]::WindowsApplication } else { [Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication }
    $options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new($kind).
        WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).
        WithPlatform([Microsoft.CodeAnalysis.Platform]::X64).
        WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable)
    $compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create($Name, $trees, $references, $options)
    $path = Join-Path $output "$Name.dll"
    $stream = [IO.File]::Create($path)
    try { $result = $compilation.Emit($stream) } finally { $stream.Dispose() }
    foreach ($diagnostic in $result.Diagnostics) { Write-Host $diagnostic.ToString() }
    if (-not $result.Success) { throw "Compilation failed: $Name" }
    $framework = if ($Gui) { 'Microsoft.WindowsDesktop.App' } else { 'Microsoft.NETCore.App' }
    @{ runtimeOptions = @{ tfm = 'net10.0'; framework = @{ name = $framework; version = '10.0.0' } } } |
        ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output "$Name.runtimeconfig.json") -Encoding utf8
    Write-Host "Compiled $path"
}
$sources = @(Get-ChildItem (Join-Path $root 'src\AudioFromWhatDevice') -Filter '*.cs' | Select-Object -ExpandProperty FullName)
Compile-Local 'AudioFromWhatDevice' $sources $true
Compile-Local 'ActivityTests' @((Join-Path $root 'tests\ActivityTests\Program.cs'), (Join-Path $root 'src\AudioFromWhatDevice\ActivityState.cs'), (Join-Path $root 'src\AudioFromWhatDevice\TrayLabels.cs'), (Join-Path $root 'src\AudioFromWhatDevice\BluetoothDeviceDetector.cs'), (Join-Path $root 'src\AudioFromWhatDevice\StartupRegistration.cs')) $false
& dotnet (Join-Path $output 'ActivityTests.dll')
if ($LASTEXITCODE -ne 0) { throw 'Activity tests failed.' }
& dotnet (Join-Path $output 'AudioFromWhatDevice.dll') --probe (Join-Path $output 'probe.json')
if ($LASTEXITCODE -ne 0) { throw 'Live Core Audio probe failed; inspect probe.json.' }
Get-Content (Join-Path $output 'probe.json')
