param(
    [Parameter(Mandatory = $true)][string]$UnityEditor,
    [string]$ProjectPath = (Join-Path $PSScriptRoot '../../../..'),
    [string]$OutputDirectory,
    [string]$SdkManifestPath,
    [switch]$SkipTests,
    [ValidateSet('None', 'Addressables', 'YooAsset', 'Both')][string[]]$Combinations = @('None', 'Addressables', 'YooAsset', 'Both')
)
$ErrorActionPreference = 'Stop'
$sourceProject = (Resolve-Path -LiteralPath $ProjectPath).Path
$editorExecutable = (Resolve-Path -LiteralPath $UnityEditor).Path

function ConvertTo-NativeArgument([string]$Argument) {
    # ProcessStartInfo 会将参数重新拼成 Windows 命令行；同时处理路径空格、引号及结尾反斜杠。
    $escaped = [regex]::Replace($Argument, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function Invoke-UnityAndWait([string[]]$UnityArguments) {
    $commandLine = ($UnityArguments | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' '
    # Unity.exe 是 GUI 程序；直接调用不能可靠等待，且 LASTEXITCODE 可能仍来自 robocopy。
    $editorProcess = Start-Process -FilePath $editorExecutable -ArgumentList $commandLine -WindowStyle Hidden -Wait -PassThru
    return $editorProcess.ExitCode
}

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $sourceProject ('Temp/ResourceBackendMatrix-' + [guid]::NewGuid().ToString('N'))
}
$matrixRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $matrixRoot) {
    throw '输出目录已存在。请指定新的空路径；脚本不会删除或覆盖旧验证副本。'
}
$assetsRoot = [IO.Path]::GetFullPath((Join-Path $sourceProject 'Assets'))
if ($matrixRoot.StartsWith($assetsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $matrixRoot -eq $assetsRoot) {
    throw '隔离副本不能位于源工程 Assets 中。'
}
$sdkNames = @('com.unity.addressables', 'com.unity.addressables.android', 'com.tuyoogame.yooasset')
# 共存兼容性测试从独立夹具读取 SDK 声明，绝不为了测试修改主工程依赖。
$fixtureManifestPath = if ($SdkManifestPath) { (Resolve-Path -LiteralPath $SdkManifestPath).Path } else { Join-Path $sourceProject 'Packages/manifest.json' }
$sdkManifest = Get-Content -LiteralPath $fixtureManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$requiredSdkNames = @()
if ($Combinations -contains 'Addressables' -or $Combinations -contains 'Both') { $requiredSdkNames += $sdkNames[0..1] }
if ($Combinations -contains 'YooAsset' -or $Combinations -contains 'Both') { $requiredSdkNames += $sdkNames[2] }
foreach ($sdkName in $requiredSdkNames) {
    if (-not $sdkManifest.dependencies.PSObject.Properties[$sdkName]) {
        throw "测试夹具缺少 $sdkName。请通过 -SdkManifestPath 指定已有完整 SDK 声明的独立 manifest；不要为此在主工程安装全部 SDK。"
    }
}
if ($requiredSdkNames -contains 'com.tuyoogame.yooasset') {
    $yooRegistry = @($sdkManifest.scopedRegistries | Where-Object {
        $scopes = @($_.scopes)
        @($scopes | Where-Object { 'com.tuyoogame.yooasset' -eq $_ -or 'com.tuyoogame.yooasset'.StartsWith($_ + '.') }).Count -gt 0
    })
    if ($yooRegistry.Count -eq 0) { throw '测试夹具缺少 YooAsset 注册源，请提供含 scopedRegistries 的完整 manifest。' }
}
New-Item -ItemType Directory -Path $matrixRoot | Out-Null
$summary = @()
foreach ($combination in $Combinations) {
    $copyProject = Join-Path $matrixRoot $combination
    New-Item -ItemType Directory -Path $copyProject | Out-Null
    foreach ($folder in @('Assets', 'Packages', 'ProjectSettings')) {
        & robocopy (Join-Path $sourceProject $folder) (Join-Path $copyProject $folder) /E /XJ /R:1 /W:1 /NFL /NDL /NJH /NJS | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "复制 $combination/$folder 失败，退出码 $LASTEXITCODE。" }
    }
    $manifestPath = Join-Path $copyProject 'Packages/manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    # 仅将 SDK 声明与其注册源复制到新建副本，其余依赖仍沿用源工程。
    foreach ($sdkName in $requiredSdkNames) {
        $manifest.dependencies | Add-Member -MemberType NoteProperty -Name $sdkName -Value $sdkManifest.dependencies.PSObject.Properties[$sdkName].Value -Force
    }
    if ($combination -in @('YooAsset', 'Both')) {
        $registries = @($manifest.scopedRegistries | Where-Object { $_ })
        foreach ($registry in @($sdkManifest.scopedRegistries)) {
            if (-not $registry) { continue }
            $matchingScopes = @($registry.scopes | Where-Object { 'com.tuyoogame.yooasset' -eq $_ -or 'com.tuyoogame.yooasset'.StartsWith($_ + '.') })
            if ($matchingScopes.Count -eq 0) { continue }
            $existing = $registries | Where-Object { $_.url -eq $registry.url } | Select-Object -First 1
            if ($existing) { $existing.scopes = @(@($existing.scopes) + @('com.tuyoogame.yooasset') | Select-Object -Unique) }
            else { $registries += [pscustomobject]@{ name=$registry.name; url=$registry.url; scopes=@('com.tuyoogame.yooasset') } }
        }
        $manifest | Add-Member -MemberType NoteProperty -Name scopedRegistries -Value $registries -Force
    }
    if ($combination -in @('None', 'YooAsset')) {
        $manifest.dependencies.PSObject.Properties.Remove('com.unity.addressables')
        $manifest.dependencies.PSObject.Properties.Remove('com.unity.addressables.android')
    }
    if ($combination -in @('None', 'Addressables')) {
        $manifest.dependencies.PSObject.Properties.Remove('com.tuyoogame.yooasset')
    }
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 32), (New-Object Text.UTF8Encoding $false))
    # 仅移除刚创建的副本中的锁文件，让 UPM 重新解析该组合；源工程及已有目录从不删除。
    $copyLock = Join-Path $copyProject 'Packages/packages-lock.json'
    if (Test-Path -LiteralPath $copyLock) { Remove-Item -LiteralPath $copyLock }
    $compileLog = Join-Path $matrixRoot ($combination + '-compile.log')
    # 方法自行 Exit：None 组合会跨程序集重载进入真实 Play，不能传 -quit 提前结束。
    $compileArguments = @('-projectPath', $copyProject, '-executeMethod', 'ResourceBackendValidation.VerifyPackageMatrix', '-resourceBackendMatrix', $combination, '-logFile', $compileLog)
    # None 真实 UI 流程依赖 Game 视图渲染帧末，batch 会永久停在 EndOfFrameAsync。
    if ($combination -ne 'None') { $compileArguments = @('-batchmode') + $compileArguments }
    $compileExitCode = Invoke-UnityAndWait $compileArguments
    if ($compileExitCode -ne 0) { throw "$combination 编译失败，退出码 $compileExitCode，请查看 $compileLog。" }
    $evidence = Join-Path $copyProject 'Library/AAGameValidation/package-matrix.txt'
    if (-not (Test-Path -LiteralPath $evidence)) { throw "$combination 未生成包集合验证证据。" }
    if ($combination -eq 'None') {
        $flowEvidence = Join-Path $copyProject 'Library/AAGameValidation/resources-flow.txt'
        if (-not (Test-Path -LiteralPath $flowEvidence) -or (Get-Content -LiteralPath $flowEvidence -Raw -Encoding UTF8) -notmatch 'PASS：') {
            throw "无 SDK Resources 真实 Play 验证未通过，请查看 $compileLog。"
        }
    }
    if (-not $SkipTests) {
        $testLog = Join-Path $matrixRoot ($combination + '-tests.log')
        $testResult = Join-Path $matrixRoot ($combination + '-tests.xml')
        $testExitCode = Invoke-UnityAndWait @('-batchmode', '-projectPath', $copyProject, '-runTests', '-testPlatform', 'EditMode', '-testFilter', 'ComponentAutoBindTool.Tests', '-testResults', $testResult, '-logFile', $testLog)
        if ($testExitCode -ne 0 -or -not (Test-Path -LiteralPath $testResult)) { throw "$combination EditMode 测试失败，退出码 $testExitCode，请查看 $testLog。" }
        [xml]$testXml = Get-Content -LiteralPath $testResult -Raw -Encoding UTF8
        if ($testXml.'test-run'.result -ne 'Passed') { throw "$combination 测试未全部通过，请查看 $testResult。" }
    }
    $summary += "PASS：$combination"
    [IO.File]::WriteAllLines((Join-Path $matrixRoot 'summary.txt'), $summary)
}
Write-Output "本次包组合验证完成（$($Combinations -join ', ')），副本和证据保留在：$matrixRoot"
