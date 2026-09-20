<#
.SYNOPSIS
    一键多格式打包脚本：支持输出自包含单文件 EXE、带独立图标的便携绿色 Zip 包以及 Inno Setup 安装向导。
.PARAMETER Version
    发布的版本号，默认 1.0.0。
.PARAMETER OutputTypes
    打包类型数组，可选 'portable', 'singlefile', 'installer'。默认全部打包。
#>
param(
    [string]$Version = '1.0.0',
    [string[]]$OutputTypes = @('portable', 'singlefile', 'installer')
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot 'src\DesktopWorkflow.App\DesktopWorkflow.App.csproj'
$testProjectFile = Join-Path $projectRoot 'src\DesktopWorkflow.Tests\DesktopWorkflow.Tests.csproj'
$distDirectory = Join-Path $projectRoot 'dist'
$tempStaging = Join-Path $projectRoot ("dist.temp-" + [Guid]::NewGuid().ToString('N'))

function Move-GeneratedFileToRecycleBin([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return
    }
    Add-Type -AssemblyName Microsoft.VisualBasic
    [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile(
        $path,
        [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,
        [Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin)
}

function Move-GeneratedDirectoryToRecycleBin([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        return
    }
    Add-Type -AssemblyName Microsoft.VisualBasic
    [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory(
        $path,
        [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,
        [Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin)
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " 桌面工作流 (DesktopWorkflow) 自动化打包" -ForegroundColor Cyan
Write-Host " 版本: v$Version" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# 1. 环境检查
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '未检测到 dotnet SDK 环境。'
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "版本号必须使用三段数字格式，例如 1.0.0：$Version"
}
$validOutputTypes = @('portable', 'singlefile', 'installer')
$unknownOutputTypes = @($OutputTypes | Where-Object { $_ -notin $validOutputTypes })
if ($unknownOutputTypes.Count -gt 0) {
    throw "不支持的打包类型：$($unknownOutputTypes -join ', ')"
}
$OutputTypes = @($OutputTypes | Select-Object -Unique)
if ($OutputTypes.Count -eq 0) {
    throw '至少需要指定一种打包类型。'
}

# 2. 运行自动化测试
Write-Host "`n[1/5] 运行自动化测试确保质量..." -ForegroundColor Yellow
& dotnet run --project $testProjectFile -c Release
if ($LASTEXITCODE -ne 0) {
    throw "自动化测试未通过，终止打包。"
}
Write-Host "-> 自动化测试全部通过。" -ForegroundColor Green

# 3. 准备输出目录
if (-not (Test-Path -LiteralPath $distDirectory)) {
    New-Item -ItemType Directory -Path $distDirectory | Out-Null
}

try {
    # 4. 生成自包含完整发布目录（作为便携包与安装包的基础）
    Write-Host "`n[2/5] 编译 x64 自包含发布产物..." -ForegroundColor Yellow
    $appPublishDir = Join-Path $tempStaging 'app'
    & dotnet publish $projectFile -c Release -r win-x64 --self-contained true `
        -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false `
        --nologo -o $appPublishDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish 失败。"
    }

    # 复制独立图标资源与样例文件
    $iconDir = Join-Path $tempStaging 'icons'
    New-Item -ItemType Directory -Path $iconDir | Out-Null
    Copy-Item (Join-Path $projectRoot 'src\DesktopWorkflow.App\Assets\desktop-workflow.ico') $iconDir
    Copy-Item (Join-Path $projectRoot 'src\DesktopWorkflow.App\Assets\desktop-workflow.png') $iconDir

    $exampleDir = Join-Path $tempStaging 'examples'
    if (Test-Path (Join-Path $projectRoot 'examples')) {
        Copy-Item (Join-Path $projectRoot 'examples') $tempStaging -Recurse
    }

    # 5. 打包类型 1：便携绿色 Zip 包 (Portable)
    if ($OutputTypes -contains 'portable') {
        Write-Host "`n[3/5] 构建便携绿色压缩包 (Portable Zip)..." -ForegroundColor Yellow
        $portableFolder = Join-Path $tempStaging "DesktopWorkflow-v$Version-Portable-x64"
        New-Item -ItemType Directory -Path $portableFolder | Out-Null
        Copy-Item -Path "$appPublishDir\*" -Destination $portableFolder -Recurse
        Copy-Item -Path $iconDir -Destination $portableFolder -Recurse
        if (Test-Path (Join-Path $projectRoot 'examples')) {
            Copy-Item -Path (Join-Path $projectRoot 'examples') -Destination $portableFolder -Recurse
        }
        Copy-Item (Join-Path $projectRoot 'README.md') $portableFolder
        Copy-Item (Join-Path $projectRoot 'README.en.md') $portableFolder
        Copy-Item (Join-Path $projectRoot 'docs') $portableFolder -Recurse
        Copy-Item (Join-Path $projectRoot 'LICENSE') $portableFolder

        $zipPath = Join-Path $distDirectory "DesktopWorkflow-v$Version-Portable-x64.zip"
        Move-GeneratedFileToRecycleBin $zipPath
        Compress-Archive -Path "$portableFolder\*" -DestinationPath $zipPath
        Write-Host "-> 已生成便携包: $zipPath" -ForegroundColor Green
    }

    # 6. 打包类型 2：自包含单文件 EXE (SingleFile)
    if ($OutputTypes -contains 'singlefile') {
        Write-Host "`n[4/5] 编译独立免安装单文件 EXE..." -ForegroundColor Yellow
        $singleFileStaging = Join-Path $tempStaging 'singlefile'
        & dotnet publish $projectFile -c Release -r win-x64 --self-contained true `
            -p:Version=$Version `
            -p:DebugType=None -p:DebugSymbols=false `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true `
            --nologo -o $singleFileStaging
        if ($LASTEXITCODE -ne 0) {
            throw "单文件编译失败。"
        }
        $singleExe = Join-Path $singleFileStaging 'DesktopWorkflow.exe'
        $destExe = Join-Path $distDirectory "DesktopWorkflow-v$Version-SingleFile-x64.exe"
        Move-GeneratedFileToRecycleBin $destExe
        Copy-Item -LiteralPath $singleExe -Destination $destExe
        Write-Host "-> 已生成单文件版: $destExe" -ForegroundColor Green
    }

    # 7. 打包类型 3：Inno Setup 安装包 (Installer)
    if ($OutputTypes -contains 'installer') {
        Write-Host "`n[5/5] 构建 Inno Setup 安装向导..." -ForegroundColor Yellow
        $issFile = Join-Path $projectRoot 'installer\DesktopWorkflow.iss'
        $isccCandidates = @(
            (Get-Command iscc -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
            "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe"
        )
        $isccPath = $isccCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

        if ($isccPath) {
            Write-Host "找到 Inno Setup 编译器: $isccPath" -ForegroundColor Cyan
            $repositoryUrl = if ($env:GITHUB_SERVER_URL -and $env:GITHUB_REPOSITORY) {
                "$($env:GITHUB_SERVER_URL)/$($env:GITHUB_REPOSITORY)"
            } else {
                'https://github.com/SANG4242/WindowsDesktopWorkflow'
            }
            $installerPath = Join-Path $distDirectory "DesktopWorkflow-Setup-v$Version-x64.exe"
            Move-GeneratedFileToRecycleBin $installerPath
            & $isccPath "/DMyAppVersion=$Version" "/DMyAppURL=$repositoryUrl" "/DSourceDir=$appPublishDir" "/DOutputDir=$distDirectory" $issFile
            if ($LASTEXITCODE -ne 0) {
                throw "Inno Setup 编译失败。"
            }
            Write-Host "-> 已生成安装包: $installerPath" -ForegroundColor Green
        } else {
            throw '请求生成安装包，但未找到 Inno Setup 6 编译器。请安装后重试，或通过 -OutputTypes portable,singlefile 只生成免安装发行物。'
        }
    }

    # 8. 检查请求的发行物并生成校验文件
    $artifactPaths = @()
    if ($OutputTypes -contains 'portable') {
        $artifactPaths += Join-Path $distDirectory "DesktopWorkflow-v$Version-Portable-x64.zip"
    }
    if ($OutputTypes -contains 'singlefile') {
        $artifactPaths += Join-Path $distDirectory "DesktopWorkflow-v$Version-SingleFile-x64.exe"
    }
    if ($OutputTypes -contains 'installer') {
        $artifactPaths += Join-Path $distDirectory "DesktopWorkflow-Setup-v$Version-x64.exe"
    }
    foreach ($artifactPath in $artifactPaths) {
        if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf) -or
            (Get-Item -LiteralPath $artifactPath).Length -le 0) {
            throw "请求的发行物缺失或为空：$artifactPath"
        }
    }
    $releaseArtifacts = @($artifactPaths | ForEach-Object { Get-Item -LiteralPath $_ } | Sort-Object Name)
    $checksumPath = Join-Path $distDirectory "DesktopWorkflow-v$Version-SHA256SUMS.txt"
    Move-GeneratedFileToRecycleBin $checksumPath
    $checksumLines = $releaseArtifacts | ForEach-Object {
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        "$hash  $($_.Name)"
    }
    Set-Content -LiteralPath $checksumPath -Value $checksumLines -Encoding utf8

    Write-Host "`n========================================" -ForegroundColor Cyan
    Write-Host " 打包完成！产物清单 ($distDirectory):" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    @($releaseArtifacts) + @(Get-Item $checksumPath) | ForEach-Object {
        $sizeMb = [Math]::Round($_.Length / 1MB, 2)
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.Substring(0, 16)
        Write-Host ("- {0,-45} {1,8} MB  [SHA256: {2}...]" -f $_.Name, $sizeMb, $hash) -ForegroundColor White
    }
}
finally {
    Move-GeneratedDirectoryToRecycleBin $tempStaging
}
