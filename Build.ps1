param([switch]$FrameworkDependent, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
& (Join-Path $taskRoot 'NativeBuild.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Native proxy build failed.' }
$taskVersion = ([xml](Get-Content -LiteralPath (Join-Path $taskRoot 'src\Tweak.App\Tweak.App.csproj') -Raw)).Project.PropertyGroup.Version | Select-Object -First 1
$taskOutput = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $taskRoot "dist\Tweakmania-$taskVersion" }
$selfContained = if ($FrameworkDependent.IsPresent) { 'false' } else { 'true' }
dotnet publish (Join-Path $taskRoot 'src\Tweak.App\Tweak.App.csproj') -c Release -r win-x64 --self-contained $selfContained -o $taskOutput
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md'),(Join-Path $taskRoot 'LICENSE'),(Join-Path $taskRoot 'THIRD-PARTY-NOTICES.md') -Destination $taskOutput
New-Item -ItemType Directory -Path (Join-Path $taskOutput 'native\x86') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRoot 'native\build\x86\dinput8.dll') -Destination (Join-Path $taskOutput 'native\x86\dinput8.dll') -Force
New-Item -ItemType Directory -Path (Join-Path $taskOutput 'profiles') -Force | Out-Null
Copy-Item -Path (Join-Path $taskRoot 'profiles\*.json') -Destination (Join-Path $taskOutput 'profiles') -Force
if (-not $FrameworkDependent) {
    $taskAssets = Get-Content -LiteralPath (Join-Path $taskRoot 'src\Tweak.App\obj\project.assets.json') -Raw | ConvertFrom-Json
    $taskConfig = Get-Content -LiteralPath (Join-Path $taskOutput 'Tweakmania.runtimeconfig.json') -Raw | ConvertFrom-Json
    foreach ($taskFramework in $taskConfig.runtimeOptions.includedFrameworks) {
        $taskPack = $taskFramework.name.ToLowerInvariant() + '.runtime.win-x64'
        foreach ($taskFolder in $taskAssets.packageFolders.PSObject.Properties.Name) {
            $taskPackPath = Join-Path $taskFolder ($taskPack + '\' + $taskFramework.version)
            foreach ($taskNotice in @('LICENSE','LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')) {
                $taskNoticePath = Join-Path $taskPackPath $taskNotice
                if (Test-Path -LiteralPath $taskNoticePath) {
                    Copy-Item -LiteralPath $taskNoticePath -Destination (Join-Path $taskOutput ($taskFramework.name + '-' + $taskNotice + '.txt')) -Force
                }
            }
        }
    }
}
$taskArchive = Join-Path $taskRoot "dist\Tweakmania-$taskVersion-win-x64.zip"
if (Test-Path -LiteralPath $taskArchive) { Remove-Item -LiteralPath $taskArchive -Force }
Compress-Archive -Path (Join-Path $taskOutput '*') -DestinationPath $taskArchive -CompressionLevel Optimal
Write-Output "Ready: $taskOutput\Tweakmania.exe"
Write-Output "Portable release: $taskArchive"
Set-Content -LiteralPath (Join-Path $taskRoot 'dist\latest.txt') -Value ([IO.Path]::GetRelativePath((Join-Path $taskRoot 'dist'), (Join-Path $taskOutput 'Tweakmania.exe'))) -Encoding Ascii
