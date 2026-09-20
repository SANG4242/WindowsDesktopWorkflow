$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot 'src\DesktopWorkflow.App\DesktopWorkflow.App.csproj'
$testProjectFile = Join-Path $projectRoot 'src\DesktopWorkflow.Tests\DesktopWorkflow.Tests.csproj'
$appDirectory = Join-Path $projectRoot 'app'
$verificationPublish = Join-Path $projectRoot ("src\DesktopWorkflow.App\obj\verification-publish-$([Guid]::NewGuid().ToString('N'))")

function Get-RelativeFileMap([string]$directory) {
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        throw "目录不存在：$directory"
    }
    $root = [System.IO.Path]::GetFullPath($directory).TrimEnd('\') + '\'
    $map = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $directory -Recurse -File | Sort-Object FullName) {
        $relative = $file.FullName.Substring($root.Length).Replace('\', '/')
        $map[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    return $map
}

function Remove-GeneratedDirectory([string]$directory) {
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        return
    }
    Add-Type -AssemblyName Microsoft.VisualBasic
    [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory(
        $directory,
        [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,
        [Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin)
}

try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw '未找到 dotnet SDK。'
    }
    if (-not (Test-Path -LiteralPath $projectFile)) {
        throw "未找到 WPF 工程：$projectFile"
    }

    & dotnet build $projectFile -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
    & dotnet run --project $testProjectFile -c Release
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
    & dotnet publish $projectFile -c Release -r win-x64 --self-contained false --nologo -o $verificationPublish
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $validationProcess = Start-Process -FilePath (Join-Path $verificationPublish 'DesktopWorkflow.exe') `
        -ArgumentList '--validate-workflows', '--workspace-root', ('"' + $projectRoot + '"') `
        -Wait -PassThru
    if ($validationProcess.ExitCode -ne 0) {
        throw '工作流配置未通过程序真实解析规则。'
    }

    $expected = Get-RelativeFileMap $verificationPublish
    $actual = Get-RelativeFileMap $appDirectory
    $expectedNames = @($expected.Keys)
    $actualNames = @($actual.Keys)
    if (Compare-Object -ReferenceObject $expectedNames -DifferenceObject $actualNames) {
        throw 'app 与当前发布产物的完整文件集合不一致，请先运行 scripts\发布.ps1。'
    }
    foreach ($name in $expectedNames) {
        if ($expected[$name] -ne $actual[$name]) {
            throw "app 中的文件与当前发布产物不一致，请先运行 scripts\发布.ps1：$name"
        }
    }

    Write-Output "OK: 自动化测试、真实工作流解析、WPF 构建和完整发布目录一致性均通过，共 $($expectedNames.Count) 个文件。"
    exit 0
}
finally {
    Remove-GeneratedDirectory $verificationPublish
}
