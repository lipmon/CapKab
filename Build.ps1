$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    New-Item -ItemType Directory -Path 'outputs' -Force | Out-Null
    & .\work\Build.ps1
} finally {
    Pop-Location
}
