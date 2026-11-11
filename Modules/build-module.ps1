# Builds the Magenta Bridge module for MSFS 2024 and stages a Community package.
# Uses the SDK's own toolchain (clang-cl + wasm-ld). No Visual Studio, no Emscripten.
#
#   .\build.ps1                 build and stage into .\package
#   .\build.ps1 -Install        ... and copy the package into the sim's Community folder
#
param(
    [string]$SdkPath = $env:MSFS_SDK,
    [string]$Community = 'D:\MSFS Content\Community',
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $here 'build'
$package = Join-Path $here 'package'

if (-not $SdkPath -or -not (Test-Path $SdkPath)) {
    foreach ($candidate in @('C:\MSFS 2024 SDK', 'C:\MSFS SDK', 'D:\MSFS 2024 SDK')) {
        if (Test-Path $candidate) { $SdkPath = $candidate; break }
    }
}
if (-not $SdkPath -or -not (Test-Path $SdkPath)) {
    throw "MSFS SDK not found. Install it from the sim's Dev Mode tools, then set MSFS_SDK."
}

$clang = Join-Path $SdkPath 'WASM\llvm\bin\clang-cl.exe'
$link = Join-Path $SdkPath 'WASM\llvm\bin\wasm-ld.exe'
$include = Join-Path $SdkPath 'WASM\include'
foreach ($tool in $clang, $link) { if (-not (Test-Path $tool)) { throw "missing tool: $tool" } }

# Native tools write progress to stderr, which under ErrorActionPreference=Stop
# PowerShell treats as a terminating error. Run them with Continue and decide on
# the exit code instead.
function Invoke-Tool {
    param([string]$Exe, [string[]]$Arguments, [string]$What)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Exe @Arguments 2>&1 | ForEach-Object { Write-Host $_ }
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
    }
    if ($code -ne 0) { throw "$What failed (exit $code)" }
}

New-Item -ItemType Directory -Force -Path $out | Out-Null
$object = Join-Path $out 'MagentaBridgeModule.o'
$wasm = Join-Path $out 'MagentaBridgeModule.wasm'

Invoke-Tool $clang @(
    '--target=wasm32-wasi', '-fms-extensions', '/GS-',
    "-I$include", "-I$(Join-Path $SdkPath 'WASM\wasi-sysroot\include')",
    '-c', (Join-Path $here 'MagentaBridgeModule.cpp'), '-o', $object
) 'compile'

Invoke-Tool $link @(
    '--no-entry', '--allow-undefined', '--strip-all',
    '--export=module_init', '--export=module_deinit',
    $object, '-o', $wasm
) 'link'

# --- stage the package -------------------------------------------------------
$modules = Join-Path $package 'modules'
New-Item -ItemType Directory -Force -Path $modules | Out-Null
Copy-Item $wasm (Join-Path $modules 'MagentaBridgeModule.wasm') -Force

$manifestPath = Join-Path $package 'manifest.json'
if (-not (Test-Path $manifestPath)) {
    throw "package\manifest.json is missing"
}

# layout.json lists every file with its size and date; the sim uses it to mount
# the package. It also lists itself, so converge on its own size.
$layoutPath = Join-Path $package 'layout.json'
for ($pass = 0; $pass -lt 4; $pass++) {
    $files = @()
    $files += @{ path = 'manifest.json'; size = (Get-Item $manifestPath).Length; date = (Get-Date (Get-Item $manifestPath).LastWriteTimeUtc -UFormat %s) }
    foreach ($f in Get-ChildItem $modules -File) {
        $files += @{ path = ('modules/' + $f.Name); size = $f.Length; date = (Get-Date $f.LastWriteTimeUtc -UFormat %s) }
    }
    $selfSize = if (Test-Path $layoutPath) { (Get-Item $layoutPath).Length } else { 0 }
    $files += @{ path = 'layout.json'; size = $selfSize; date = [long](Get-Date -UFormat %s) }

    $json = @{ content = @($files | ForEach-Object { [ordered]@{ path = $_.path; size = [long]$_.size; date = [long]$_.date } }) } |
        ConvertTo-Json -Depth 5
    $before = if (Test-Path $layoutPath) { (Get-Item $layoutPath).Length } else { -1 }
    Set-Content -Path $layoutPath -Value $json -Encoding UTF8 -NoNewline
    if ((Get-Item $layoutPath).Length -eq $selfSize) { break }
}

$size = (Get-Item (Join-Path $modules 'MagentaBridgeModule.wasm')).Length
Write-Host ("built: {0} ({1} bytes)" -f $wasm, $size)
Write-Host ("package: {0}" -f $package)

if ($Install) {
    $target = Join-Path $Community 'magenta-bridge-module'
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item (Join-Path $package '*') $target -Recurse -Force
    Write-Host ("installed: {0}" -f $target)
}





