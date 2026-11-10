$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $root) { $root = 'E:\本子猎手' }
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $root 'benchtest.exe'
$refs = @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll')
$files = @((Join-Path $root 'src\Core.cs'), (Join-Path $root 'src\Sites.cs'), (Join-Path $root 'tests\Test.cs'))
$a = @('/nologo','/target:exe','/platform:anycpu','/codepage:65001')
foreach ($r in $refs) { $a += '/r:' + $r }
$a += '/out:' + $out
$a += $files
& $csc @a
Write-Host ("test build exit=" + $LASTEXITCODE)
