$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$taskVsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $taskVsWhere)) { throw 'Install the Visual Studio C++ build tools (x86/x64) and Windows SDK.' }
$vs = & $taskVsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'A Visual Studio C++ x86/x64 toolchain was not found.' }
$msvc = (Get-ChildItem -LiteralPath (Join-Path $vs 'VC\Tools\MSVC') -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'lib\x86\libcmt.lib') } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1).FullName
$sdk = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -Name KitsRoot10).KitsRoot10
$sdkver = (Get-ChildItem -LiteralPath (Join-Path $sdk 'Include') -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' -and
        (Test-Path -LiteralPath (Join-Path $sdk "Lib\$($_.Name)\um\x86\user32.lib")) -and
        (Test-Path -LiteralPath (Join-Path $_.FullName 'um\dinput.h')) } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1).Name
if (-not $msvc -or -not $sdkver) { throw 'The required C++ runtime and x86 Windows SDK files were not found.' }
$out = Join-Path $root 'native\build\x86'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$clang = (Get-Command clang-cl.exe -ErrorAction Stop).Source
$link = (Get-Command lld-link.exe -ErrorAction Stop).Source
$includes = @('/I' + (Join-Path $msvc 'include'), '/I' + (Join-Path $sdk "Include\$sdkver\ucrt"), '/I' + (Join-Path $sdk "Include\$sdkver\shared"), '/I' + (Join-Path $sdk "Include\$sdkver\um"), '/I' + (Join-Path $sdk "Include\$sdkver\winrt"))
$libs = @('/LIBPATH:' + (Join-Path $msvc 'lib\x86'), '/LIBPATH:' + (Join-Path $sdk "Lib\$sdkver\ucrt\x86"), '/LIBPATH:' + (Join-Path $sdk "Lib\$sdkver\um\x86"))
& $clang --target=i686-pc-windows-msvc /nologo /c /O2 /W4 /MT /EHsc /std:c++17 /DWIN32 /D_WINDOWS @includes (Join-Path $root 'native\dinput8_proxy.cpp') ('/Fo' + (Join-Path $out 'dinput8_proxy.obj'))
if ($LASTEXITCODE) { throw "clang-cl failed with exit code $LASTEXITCODE" }
& $link /nologo /dll /machine:x86 ('/out:' + (Join-Path $out 'dinput8.dll')) ('/def:' + (Join-Path $root 'native\dinput8.def')) @libs (Join-Path $out 'dinput8_proxy.obj') kernel32.lib user32.lib libucrt.lib libcmt.lib libvcruntime.lib
if ($LASTEXITCODE) { throw "lld-link failed with exit code $LASTEXITCODE" }
Write-Host "Built $out\dinput8.dll"
