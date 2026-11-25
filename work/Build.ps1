$ErrorActionPreference = 'Stop'
$csc="$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:exe /optimize+ /out:work\BuildAssets.exe work\Assets.cs work\BuildAssets.cs
if ($LASTEXITCODE) { throw "Asset compilation failed" }
& work\BuildAssets.exe (Join-Path $PWD 'work')
if ($LASTEXITCODE) { throw "Asset generation failed" }
function Compile-Capture {
    param($Target, $Output)
    & $csc /nologo "/target:$Target" /optimize+ /win32icon:work\capkab.ico /resource:work\shutter.wav,shutter.wav "/out:$Output" work\Capture.cs work\ProductInfo.cs
    if ($LASTEXITCODE) { throw "App compilation failed" }
}
function Compile-Setup {
    param($Target, $Output)
    & $csc /nologo "/target:$Target" /optimize+ /win32icon:work\capkab.ico /resource:work\CapKab.exe,CapKab.exe /resource:work\License.txt,License.txt "/out:$Output" work\Setup.cs work\ProductInfo.cs
    if ($LASTEXITCODE) { throw "Setup compilation failed" }
}
Compile-Capture winexe work\CapKab.exe
Compile-Capture exe work\CaptureTest.exe
Compile-Setup winexe work\CapKab-Setup.exe
Compile-Setup exe work\SetupTest.exe
& work\CaptureTest.exe --self-test
if ($LASTEXITCODE) { throw "Capture self-test failed" }
$expected=(Get-FileHash work\CapKab.exe).Hash
$actual= & work\SetupTest.exe --verify-package
if ($LASTEXITCODE -or $expected -ne $actual) { throw "Package verification failed" }
"Package SHA256 verified: $actual"
Copy-Item work\CapKab-Setup.exe outputs\CapKab-Setup.exe
Copy-Item work\License.txt outputs\CapKab-LICENSE.txt
Get-Item outputs\CapKab-Setup.exe | Select-Object Name,Length

