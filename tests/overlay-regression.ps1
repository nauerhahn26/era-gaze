param([string]$Source = "$PSScriptRoot\..\device\ERAgaze.cs", [string]$Output = "$env:TEMP\era-overlay-regression", [switch]$CompileOnly)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Output | Out-Null
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$original = [IO.File]::ReadAllText((Resolve-Path $Source))
foreach ($variant in @('real', 'fault')) {
    $code = $original
    if ($variant -eq 'fault') {
        $needle = 'bmp.GetHbitmap(Color.FromArgb(0))'
        if (($code.Split(@($needle), [StringSplitOptions]::None).Count - 1) -ne 1) { throw 'GetHbitmap fault site changed' }
        $code = $code.Replace($needle, 'OverlayRegression.GetHbitmap(bmp)')
    }
    $inputPath = "$Output\engine-$variant.cs"
    [IO.File]::WriteAllText($inputPath, $code)
    & $csc /nologo /target:winexe /main:OverlayRegression "/out:$Output\$variant.exe" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll $inputPath "$PSScriptRoot\OverlayRegression.cs"
    if ($LASTEXITCODE -ne 0) { throw "Compile failed: $variant" }
}
if ($CompileOnly) { Write-Output "Compiled real.exe and fault.exe in $Output"; exit 0 }
foreach ($variant in @('real', 'fault')) {
    $receipt = "$Output\$variant.txt"
    Remove-Item $receipt -ErrorAction SilentlyContinue
    $arguments = @('"' + $receipt + '"')
    if ($variant -eq 'fault') { $arguments += 'fault' }
    $process = Start-Process "$Output\$variant.exe" -ArgumentList $arguments -PassThru
    if (!$process.WaitForExit(60000)) { $process.Kill(); throw "Timed out: $variant" }
    if (!(Test-Path $receipt)) { throw "No receipt: $variant" }
    $result = Get-Content $receipt -Raw
    if ($process.ExitCode -ne 0 -or !$result.StartsWith('PASS:')) { throw $result }
    Write-Output $result
}
