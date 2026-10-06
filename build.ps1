# Build script for Buckshot Roulette Shell Counter (ASCII only, to avoid codepage issues)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$srcDir = Join-Path $root 'src'
$out = Join-Path $root ([char]0x6076 + [char]0x9B54 + [char]0x8F6E + [char]0x76D8 + [char]0x8BB0 + [char]0x5F39 + [char]0x5668 + '.exe')

# all sources except the test files and the unused experimental detector
$sources = Get-ChildItem $srcDir -Filter *.cs |
    Where-Object { $_.Name -ne 'DetectorTest.cs' -and $_.Name -ne 'RackTest.cs' -and $_.Name -ne 'RackDetector.cs' } |
    Sort-Object Name | ForEach-Object { $_.FullName }

$roslyn = Get-ChildItem 'C:\Program Files\dotnet\sdk' -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName 'Roslyn\bincore\csc.dll' } |
    Where-Object { Test-Path $_ } | Select-Object -First 1

$refRoot = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework'
$ref = Join-Path $refRoot 'v4.8'
if (-not (Test-Path (Join-Path $ref 'System.Windows.Forms.dll'))) {
    $ref = Get-ChildItem $refRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^v4\.\d+(\.\d+)?$' } |
        Sort-Object { [version]($_.Name -replace '^v','') } -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $ref) { throw "Cannot find .NET Framework reference assemblies: $refRoot" }

$refs = @('mscorlib.dll','System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll') |
    ForEach-Object { Join-Path $ref $_ }
foreach ($r in $refs) { if (-not (Test-Path $r)) { throw "Missing reference: $r" } }

$cscArgs = @(
    '/nologo','/noconfig','/nostdlib+','/unsafe','/langversion:latest','/codepage:65001',
    '/target:winexe','/platform:anycpu','/optimize+',
    "/out:$out"
) + $sources + ($refs | ForEach-Object { "/r:$_" })

if ($roslyn) {
    Write-Host "Compiler : Roslyn"
    & dotnet exec $roslyn @cscArgs
} else {
    $fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    Write-Host "Compiler : Framework csc"
    $fwArgs = $cscArgs | Where-Object { $_ -ne '/langversion:latest' }
    & $fw @fwArgs
}

if ($LASTEXITCODE -ne 0) { throw "Compile failed, exit code $LASTEXITCODE" }
if (-not (Test-Path $out)) { throw "Output not produced" }
$fi = Get-Item $out
Write-Host ("OK: {0}  ({1:N0} bytes)" -f $fi.Name, $fi.Length)
