param([string]$Source = "$PSScriptRoot\..\device\ERAgaze.cs", [string]$Output = "$env:TEMP\era-volume-regression")
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Output | Out-Null
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
# Compile the real engine with only the legacy watchdog paths redirected into
# the test sandbox. No family configuration or installed watchdog is touched.
$code = [IO.File]::ReadAllText((Resolve-Path $Source))
$code = $code.Replace('@"C:\Users\Public\Documents\volcap.exe"', ('@"' + $Output + '\volcap.exe"'))
$code = $code.Replace('@"C:\Users\Public\Documents\volcap.conf"', ('@"' + $Output + '\volcap.conf"'))
[IO.File]::WriteAllText("$Output\engine.cs", $code)
& $csc /nologo /target:winexe "/out:$Output\volcap.exe" "$PSScriptRoot\..\volume-cap\volcap.cs"
if ($LASTEXITCODE -ne 0) { throw 'Watchdog compilation failed' }
& $csc /nologo /target:exe /main:VolumeCapRegression "/out:$Output\regression.exe" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll "$Output\engine.cs" "$PSScriptRoot\VolumeCapRegression.cs"
if ($LASTEXITCODE -ne 0) { throw 'Engine regression compilation failed' }
& "$Output\regression.exe" "--base=$Output"
if ($LASTEXITCODE -ne 0) { throw 'Volume cap regression failed' }
