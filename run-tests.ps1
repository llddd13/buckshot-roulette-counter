# run-tests.ps1 - run RackDetector against real game screenshots.
# Usage: powershell -ExecutionPolicy Bypass -File run-tests.ps1 -Dir <screenshot dir> [-Debug]
#   -Dir points at a folder of Buckshot Roulette screenshots you took yourself.
# ASCII only: Windows PowerShell 5.1 reads BOM-less files as ANSI.
param(
    [string]$Dir = ".\screenshots",
    [switch]$Debug
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$roslyn = Get-ChildItem 'C:\Program Files\dotnet\sdk' -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName 'Roslyn\bincore\csc.dll' } |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $roslyn) { throw 'Roslyn csc.dll not found (need .NET SDK)' }

$ref = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$refs = @('mscorlib.dll','System.dll','System.Core.dll','System.Drawing.dll') |
    ForEach-Object { Join-Path $ref $_ }

$out = Join-Path $root 'tools\RackTest.exe'
$cscArgs = @(
    '/nologo','/noconfig','/nostdlib+','/unsafe','/langversion:latest','/codepage:65001',
    '/target:exe', "/out:$out",
    (Join-Path $root 'src\RackDetector.cs'),
    (Join-Path $root 'tools\RackTest.cs')
) + ($refs | ForEach-Object { "/r:$_" })

& dotnet exec $roslyn @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Test build failed ($LASTEXITCODE)" }

if ($Debug) { & $out $Dir -debug } else { & $out $Dir }
exit $LASTEXITCODE
