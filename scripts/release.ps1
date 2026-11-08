# 瀚誠系統監控：一鍵打包並發佈到 GitHub Releases
# 用法：先把 GlassMonitor.csproj 的 <Version> 與 installer\GlassMonitor.iss 的 AppVersion 改成新版本，再執行
#   powershell -ExecutionPolicy Bypass -File scripts\release.ps1 -Notes "這次更新的內容"
param(
    [string]$Notes = "",
    [switch]$NoUpload
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# 版本一致性檢查
[xml]$proj = Get-Content GlassMonitor.csproj -Encoding UTF8
$ver = ($proj.Project.PropertyGroup | Where-Object { $_.Version }).Version
if (-not $ver) { throw "GlassMonitor.csproj 找不到 <Version>" }
$iss = Get-Content installer\GlassMonitor.iss -Raw -Encoding UTF8
if ($iss -notmatch [regex]::Escape("#define AppVersion `"$ver`"")) { throw "installer\GlassMonitor.iss 的 AppVersion 與 csproj 的 $ver 不一致" }
Write-Host "版本：$ver"

# PawnIO 驅動安裝檔（未修改的官方檔案，驗證 SHA-256）
$pawnUrl = 'https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe'
$pawnHash = '1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032'
$redist = Join-Path $root 'installer\redist\PawnIO_setup.exe'
if (-not (Test-Path $redist)) {
    New-Item -ItemType Directory -Force (Split-Path $redist) | Out-Null
    Invoke-WebRequest $pawnUrl -OutFile $redist -UseBasicParsing
}
if ((Get-FileHash $redist -Algorithm SHA256).Hash -ne $pawnHash) { throw "PawnIO_setup.exe 雜湊不符" }

# Intel PresentMon（遊戲 FPS 用，未修改的官方檔案，驗證 SHA-256）
$pmUrl = 'https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe'
$pmHash = 'B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF'
$pm = Join-Path $root 'installer\redist\PresentMon.exe'
if (-not (Test-Path $pm)) { Invoke-WebRequest $pmUrl -OutFile $pm -UseBasicParsing }
if ((Get-FileHash $pm -Algorithm SHA256).Hash -ne $pmHash) { throw "PresentMon.exe 雜湊不符" }

# 發佈單一執行檔
dotnet publish GlassMonitor.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失敗" }

# 打包安裝檔
$iscc = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
if (-not (Test-Path $iscc)) { $iscc = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' }
& $iscc /Q installer\GlassMonitor.iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup 編譯失敗" }

$name = "HanchengMonitor_Setup_$ver.exe"
$setup = Join-Path $root "dist\$name"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()
$shaFile = "$setup.sha256"
[IO.File]::WriteAllText($shaFile, "$hash  $name")
Write-Host "安裝檔：$setup"
Write-Host "SHA-256：$hash"

if ($NoUpload) { return }

if (-not $Notes) { $Notes = "瀚誠系統監控 $ver" }
gh release create "v$ver" $setup $shaFile --title "瀚誠系統監控 $ver" --notes $Notes
if ($LASTEXITCODE -ne 0) { throw "建立 GitHub Release 失敗" }
Write-Host "已發佈 v$ver"
