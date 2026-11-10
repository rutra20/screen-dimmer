# Builds ScreenDimmer.exe with the C# compiler that ships with Windows.
# No SDK, no NuGet, no admin rights.

$ErrorActionPreference = 'Stop'

$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $compiler)) {
    $compiler = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $compiler)) {
    throw 'csc.exe not found - .NET Framework 4.x is required.'
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $root 'src\ScreenDimmer.cs'
$dist = Join-Path $root 'dist'
$target = Join-Path $dist 'ScreenDimmer.exe'

New-Item -ItemType Directory -Path $dist -Force | Out-Null

& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /out:$target `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    $source

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

$info = Get-Item $target
Write-Host ("Built {0} ({1:N0} bytes)" -f $info.FullName, $info.Length)
