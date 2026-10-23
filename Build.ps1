[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The 64-bit .NET Framework C# compiler was not found. This build requires Windows with .NET Framework 4.x.'
}
$source = Join-Path $PSScriptRoot 'G14FanGuard.cs'
$output = Join-Path $PSScriptRoot 'G14FanGuard.exe'
& $compiler /nologo /target:exe /platform:x64 /r:System.Management.dll "/out:$output" $source
if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE." }
& $output --self-test
if ($LASTEXITCODE -ne 0) { throw "Self-test failed with exit code $LASTEXITCODE." }
Get-FileHash -LiteralPath $output -Algorithm SHA256
Write-Output 'Build and self-test passed.'
