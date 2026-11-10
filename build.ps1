# 编译 本子猎手.exe（WebView2 液态玻璃版）
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $root) { $root = 'E:\本子猎手' }
$src = Join-Path $root 'src'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $root '本子猎手.exe'
$ui  = Join-Path $root 'ui\index.html'

$refs = @(
  'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
  'System.Web.Extensions.dll', 'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll',
  (Join-Path $root 'Microsoft.Web.WebView2.Core.dll'),
  (Join-Path $root 'Microsoft.Web.WebView2.WinForms.dll')
)

$files = @(Get-ChildItem $src -Filter *.cs | ForEach-Object { $_.FullName })
$argList = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001')
foreach ($r in $refs) { $argList += '/r:' + $r }
$argList += '/win32manifest:' + (Join-Path $src 'app.manifest')
$argList += '/win32icon:' + (Join-Path $root 'app.ico')
$argList += ('/resource:' + $ui + ',index.html')
$argList += '/out:' + $out
$argList += $files

Write-Host "编译中（WebView2 液态玻璃界面）..."
& $csc @argList
if ($LASTEXITCODE -ne 0) { Write-Host "编译失败 exit=$LASTEXITCODE"; exit 1 }

$fi = Get-Item $out
$bytes = [System.IO.File]::ReadAllBytes($out)
$hasManifest = [System.Text.Encoding]::ASCII.GetString($bytes).Contains('supportedOS')
$hasUi = [System.Text.Encoding]::UTF8.GetString($bytes).Contains('backdrop-filter')
Write-Host ("编译成功: {0}  {1:N0} bytes  manifest={2}  ui_embedded={3}" -f $fi.Name, $fi.Length, $hasManifest, $hasUi)