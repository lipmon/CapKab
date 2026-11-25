$ErrorActionPreference = 'Stop'
$compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
# -Encoding UTF8 is required: Windows PowerShell 5.1 otherwise reads the Thai text as ANSI and corrupts it.
$source = Get-Content -Raw -Encoding UTF8 -LiteralPath work/Capture.cs

# Keep the UI test isolated from the user's saved language and theme.
$languageWrite = '(?ms)(    void ToggleLanguage\(\) \{\s*ApplyLanguage\(!thai\);)\s*try \{.*?(?=\r?\n    \}\r?\n\r?\n    void ToggleTheme)'
$themeWrite = '(?ms)(    void ToggleTheme\(\) \{\s*darkMode = !darkMode;\s*ApplyTheme\(darkMode\);\s*UpdateThemeButton\(\);)\s*try \{.*?(?=\r?\n    \}\r?\n\r?\n    void UpdateThemeButton)'
if ($source -notmatch $languageWrite -or $source -notmatch $themeWrite) {
    throw 'UI test isolation must be updated to match the theme and language handlers.'
}
$source = $source -replace $languageWrite, '$1'
$source = $source -replace $themeWrite, '$1'

# Everything else the test reads or saves (Preferences, the shutter option, start with Windows) goes to a
# disposable registry key, so the test starts from defaults and never changes the user's own settings.
$settingsKey = 'internal const string ProductKey = @"Software\CapKab";'
$startupKey = 'internal const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";'
if (-not $source.Contains($settingsKey) -or -not $source.Contains($startupKey)) {
    throw 'UI test isolation must be updated to match the registry key constants.'
}
$source = $source.Replace($settingsKey, 'internal const string ProductKey = @"Software\CapKab.UiTest";')
$source = $source.Replace($startupKey, 'internal const string StartupKey = @"Software\CapKab.UiTest\Run";')
Set-Content -Encoding UTF8 -LiteralPath work/CaptureUiProbe.cs -Value $source

& $compiler /nologo /main:UiTest /target:exe /win32icon:work\capkab.ico /resource:work\shutter.wav,shutter.wav /out:work\UiTest.exe work\CaptureUiProbe.cs work\ProductInfo.cs work\UiTest.cs
if ($LASTEXITCODE) { throw 'UI test compilation failed.' }
& work/UiTest.exe
if ($LASTEXITCODE) { throw 'UI regression checks failed.' }
