# PlainSnip 构建脚本（用户约定：一律 PowerShell）
param(
    [switch]$SelfTest
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

Write-Host '== PlainSnip build =='

# 检查 dotnet
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Error '未找到 dotnet CLI，请安装 .NET SDK。'
    exit 1
}

# 发布：framework-dependent 单 exe（需目标机安装 .NET 8 Desktop Runtime）
dotnet publish src/PlainSnip/PlainSnip.csproj `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o dist

if ($LASTEXITCODE -ne 0) {
    Write-Error 'publish 失败。'
    exit 1
}

# 清掉可能残留的 pdb
Remove-Item dist\*.pdb -ErrorAction SilentlyContinue

$exe = Join-Path (Get-Location) 'dist\PlainSnip.exe'
Write-Host "产物: $exe"

if ($SelfTest) {
    Write-Host '== 运行 --selftest =='
    # GUI 程序直接 & 调用不会等待，退出码与输出都会丢；用管道强制等待并收集输出。
    & $exe --selftest | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Error 'selftest 失败。' }
    exit $LASTEXITCODE
}

exit 0
