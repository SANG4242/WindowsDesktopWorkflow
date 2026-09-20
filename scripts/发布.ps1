$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot 'src\DesktopWorkflow.App\DesktopWorkflow.App.csproj'
$appDirectory = Join-Path $projectRoot 'app'
$identifier = [Guid]::NewGuid().ToString('N')
$stagingDirectory = Join-Path $projectRoot ("app.publish-$identifier")
$backupDirectory = Join-Path $projectRoot ("app.backup-$identifier")
$failedDirectory = Join-Path $projectRoot ("app.failed-$identifier")
$publishMutex = [Threading.Mutex]::new($false, 'Local\DesktopWorkflow-Publish')
$ownsMutex = $false
$swapped = $false

function Get-TargetProcesses {
    $target = [System.IO.Path]::GetFullPath((Join-Path $appDirectory 'DesktopWorkflow.exe'))
    return @(Get-Process -Name 'DesktopWorkflow' -ErrorAction SilentlyContinue | Where-Object {
        try { [System.IO.Path]::GetFullPath($_.Path) -eq $target } catch { $false }
    })
}

function Get-RelativeFileMap([string]$directory) {
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
    try {
        $ownsMutex = $publishMutex.WaitOne(0)
    }
    catch [Threading.AbandonedMutexException] {
        $ownsMutex = $true
    }
    if (-not $ownsMutex) {
        throw '另一个发布操作或桌面工作流实例正在占用发布锁。'
    }
    if (Get-TargetProcesses) {
        throw '当前项目的桌面工作流仍在运行。请先从托盘退出，再重新运行发布脚本。'
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw '未找到 dotnet SDK。'
    }

    & dotnet publish $projectFile -c Release -r win-x64 --self-contained false --nologo -o $stagingDirectory
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $validationProcess = Start-Process -FilePath (Join-Path $stagingDirectory 'DesktopWorkflow.exe') `
        -ArgumentList '--validate-workflows', '--workspace-root', ('"' + $projectRoot + '"') `
        -Wait -PassThru
    if ($validationProcess.ExitCode -ne 0) {
        throw '暂存发布产物未通过程序真实工作流解析。'
    }
    $stagedFiles = Get-RelativeFileMap $stagingDirectory
    if ($stagedFiles.Count -eq 0) {
        throw '暂存发布目录为空。'
    }
    if (Get-TargetProcesses) {
        throw '发布期间检测到桌面工作流启动，已取消目录切换。'
    }

    if (Test-Path -LiteralPath $appDirectory) {
        [System.IO.Directory]::Move($appDirectory, $backupDirectory)
    }
    try {
        [System.IO.Directory]::Move($stagingDirectory, $appDirectory)
        $swapped = $true

        $publishedFiles = Get-RelativeFileMap $appDirectory
        if (Compare-Object -ReferenceObject @($stagedFiles.Keys) -DifferenceObject @($publishedFiles.Keys)) {
            throw '目录切换后的完整发布文件集合不一致。'
        }
        foreach ($name in $stagedFiles.Keys) {
            if ($stagedFiles[$name] -ne $publishedFiles[$name]) {
                throw "目录切换后的发布文件哈希不一致：$name"
            }
        }
        $postValidation = Start-Process -FilePath (Join-Path $appDirectory 'DesktopWorkflow.exe') `
            -ArgumentList '--validate-workflows', '--workspace-root', ('"' + $projectRoot + '"') `
            -Wait -PassThru
        if ($postValidation.ExitCode -ne 0) {
            throw '正式 app 未通过程序真实工作流解析。'
        }
    }
    catch {
        if (Test-Path -LiteralPath $appDirectory) {
            [System.IO.Directory]::Move($appDirectory, $failedDirectory)
        }
        if (Test-Path -LiteralPath $backupDirectory) {
            [System.IO.Directory]::Move($backupDirectory, $appDirectory)
        }
        $swapped = $false
        throw
    }

    if (Test-Path -LiteralPath $backupDirectory) {
        Remove-GeneratedDirectory $backupDirectory
    }
    Write-Output "OK: 已通过同卷目录切换发布完整产物到 $appDirectory"
    exit 0
}
finally {
    if (-not $swapped -and (Test-Path -LiteralPath $stagingDirectory)) {
        Remove-GeneratedDirectory $stagingDirectory
    }
    if (Test-Path -LiteralPath $failedDirectory) {
        Remove-GeneratedDirectory $failedDirectory
    }
    if ($ownsMutex) {
        $publishMutex.ReleaseMutex()
    }
    $publishMutex.Dispose()
}
