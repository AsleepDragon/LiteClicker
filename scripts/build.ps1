[CmdletBinding()]
param([switch]$AllowCrossBuild)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$windowsHost = $env:OS -eq 'Windows_NT'
if (-not $windowsHost -and -not $AllowCrossBuild) {
    throw '正式验收需要 Windows 11 x64；仅交叉编译请显式使用 -AllowCrossBuild。'
}
if ($windowsHost -and [Environment]::OSVersion.Version.Build -lt 22000) { throw '需要 Windows 11。' }
function Run-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') 失败，退出码 $LASTEXITCODE" }
}
Push-Location $root
try {
    $expectedSdk = (Get-Content global.json -Raw | ConvertFrom-Json).sdk.version
    $actualSdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $expectedSdk) { throw "需要 .NET SDK $expectedSdk；当前为 $actualSdk。" }
    New-Item -ItemType Directory -Force artifacts, dist | Out-Null
    Run-Dotnet @('restore', 'LiteClicker.slnx', '--locked-mode')
    Run-Dotnet @('build', 'LiteClicker.slnx', '-c', 'Release', '--no-restore')
    Run-Dotnet @('test', 'tests/LiteClicker.Tests/LiteClicker.Tests.csproj', '-c', 'Release', '--no-build', '--no-restore', '--logger', 'trx;LogFileName=core-tests.trx', '--results-directory', 'artifacts/test-results')
    $publish = Join-Path $root 'artifacts/publish/win-x64'
    if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
    Run-Dotnet @('publish', 'src/LiteClicker.App/LiteClicker.App.csproj', '-c', 'Release', '--no-restore', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:PublishTrimmed=false', '-p:PublishAot=false', '-p:DebugType=embedded', '-o', $publish)
    $files = @(Get-ChildItem $publish -Recurse -File)
    if ($files.Count -ne 1 -or $files[0].Name -ne 'LiteClicker.exe') { throw '发布目录不是单个 LiteClicker.exe，请检查构建配置。' }
    $exe = Join-Path $root 'dist/LiteClicker-win-x64.exe'
    Copy-Item $files[0].FullName $exe -Force
    $target = Join-Path $root 'artifacts/test-target'
    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    Run-Dotnet @('publish', 'tests/LiteClicker.TestTarget/LiteClicker.TestTarget.csproj', '-c', 'Release', '--no-restore', '-o', $target)
    Copy-Item README.md, THIRD-PARTY-NOTICES.md -Destination dist -Force
    Copy-Item licenses -Destination dist -Recurse -Force
    $hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $root 'dist/SHA256SUMS.txt'), "$hash  LiteClicker-win-x64.exe`n", [Text.UTF8Encoding]::new($false))
    Write-Host "构建、逻辑测试和单文件发布完成：$exe"
    Write-Host '状态：待 Windows 验证。编译和逻辑测试不等于真实输入或干净机验收通过。'
}
finally { Pop-Location }
