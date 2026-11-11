param([string]$Step, [string]$Project, [string]$Result, [string]$Log)
$ErrorActionPreference = 'Stop'
$reboot = $false
function Get-FailureReason([int]$Code, [string]$Details) {
    if ($Code -eq 1223 -or $Details -match '(?i)800704c7|cancell?ed|用户.*取消|拒绝.*请求') { return '用户取消或拒绝 UAC 授权；可以重新点击安装并在 Windows 提示中授权。' }
    if ($Code -eq 5 -or $Details -match '(?i)80070005|access.*denied|permission|权限|拒绝访问') { return '权限不足；请检查安装目录权限或 Windows 授权后重试。' }
    if ($Code -eq 1618) { return '另一个 Windows 安装正在进行，请等待其结束后重试。' }
    if ($Details -match '(?i)network|timed?\s*out|ENOTFOUND|ECONN|TLS|certificate|download|网络|下载失败|连接|证书') { return '网络或证书校验失败；请检查网络、代理和系统时间后重试。' }
    return "安装程序失败，退出码 $Code；请查看详细日志。"
}
function Update-ToolPath {
    $env:PATH = [Environment]::GetEnvironmentVariable('PATH','Machine') + ';' + [Environment]::GetEnvironmentVariable('PATH','User') + ';' + $env:PATH + ';' + (Join-Path $env:APPDATA 'npm') + ';' + (Join-Path $env:ProgramFiles 'nodejs')
    $env:PATH = [Environment]::ExpandEnvironmentVariables($env:PATH)
}
function Invoke-Checked([string]$Tool, [string[]]$Arguments) {
    $command = Get-Command $Tool -ErrorAction Stop
    $previousPreference = $ErrorActionPreference
    try {
        # Windows PowerShell 将原生命令 stderr 包装为错误记录；以退出码判断，避免 npm 提示被误判。
        $ErrorActionPreference = 'Continue'
        & $command.Source @Arguments 2>&1 | ForEach-Object { $_.ToString() } | Out-File -LiteralPath $Log -Append -Encoding utf8
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }
    if ($code -ne 0) {
        $details = (Get-Content -LiteralPath $Log -Tail 40 -ErrorAction SilentlyContinue) -join "`n"
        throw ((Get-FailureReason $code $details) + "`n" + $details)
    }
}
try {
    Update-ToolPath
    Set-Location -LiteralPath $Project
    switch ($Step) {
        { $_ -eq 'Node' -or $_ -eq 'NodeRepair' } {
            $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
            if ($winget) {
                $extra = if ($Step -eq 'NodeRepair') { @('--force') } else { @() }
                $previousPreference = $ErrorActionPreference
                try {
                    $ErrorActionPreference = 'Continue'
                    & $winget.Source install --id OpenJS.NodeJS.LTS --exact --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity @extra 2>&1 | Out-File -LiteralPath $Log -Append -Encoding utf8
                    $code = $LASTEXITCODE
                }
                finally { $ErrorActionPreference = $previousPreference }
                if ($code -eq 3010 -or $code -eq 1641) { $reboot = $true }
                elseif ($code -ne 0) {
                    $details = (Get-Content -LiteralPath $Log -Tail 40 -ErrorAction SilentlyContinue) -join "`n"
                    throw ((Get-FailureReason $code $details) + "`n" + $details)
                }
            }
            else {
                $releases = Invoke-RestMethod 'https://nodejs.org/dist/index.json'
                $release = $releases | Where-Object { $_.lts -is [string] } | Select-Object -First 1
                if (!$release) { throw '未找到 Node.js LTS 版本' }
                $architecture = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
                $arch = switch ($architecture) { 'ARM64' { 'arm64' }; 'AMD64' { 'x64' }; 'x86' { 'x86' }; default { throw "不支持的系统架构：$architecture" } }
                $name = 'node-' + $release.version + '-' + $arch + '.msi'
                $base = 'https://nodejs.org/dist/' + $release.version + '/'
                $msi = Join-Path (Split-Path -Parent $Result) $name
                Invoke-WebRequest ($base + $name) -OutFile $msi -UseBasicParsing
                $sums = (Invoke-WebRequest ($base + 'SHASUMS256.txt') -UseBasicParsing).Content
                $line = $sums -split "`n" | Where-Object { $_ -match ('\s+' + [regex]::Escape($name) + '\s*$') } | Select-Object -First 1
                if (!$line -or (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash -ne ($line -split '\s+')[0]) { throw 'Node.js 安装器 SHA-256 校验失败' }
                if ((Get-AuthenticodeSignature -LiteralPath $msi).Status -ne 'Valid') { throw 'Node.js 安装器签名验证失败' }
                $repairArguments = if ($Step -eq 'NodeRepair') { @('REINSTALL=ALL','REINSTALLMODE=vomus') } else { @() }
                $installer = Start-Process msiexec.exe -ArgumentList (@('/i', ('"' + $msi + '"'), '/qn', '/norestart') + $repairArguments) -Verb RunAs -WindowStyle Hidden -Wait -PassThru
                if ($installer.ExitCode -eq 3010 -or $installer.ExitCode -eq 1641) { $reboot = $true }
                elseif ($installer.ExitCode -ne 0) { throw "安装器返回 $($installer.ExitCode)" }
            }
            Update-ToolPath
            Invoke-Checked 'node.exe' @('--version')
            Invoke-Checked 'npm.cmd' @('--version')
        }
        'OpenUpm' { Invoke-Checked 'npm.cmd' @('install','-g','openupm-cli'); Update-ToolPath; Invoke-Checked 'openupm.cmd' @('--version') }
        'YooAsset' {
            $metadata = Invoke-RestMethod 'https://package.openupm.com/com.tuyoogame.yooasset'
            $latest = $metadata.versions.PSObject.Properties.Name | Where-Object { $_ -match '^\d+\.\d+\.\d+$' } | Sort-Object { [version]$_ } -Descending | Select-Object -First 1
            if (!$latest) { throw 'OpenUPM 国际源未返回 YooAsset 正式版本' }
            Invoke-Checked 'openupm.cmd' @('add',('com.tuyoogame.yooasset@' + $latest),'--registry','https://package.openupm.com')
        }
        default { throw "未知安装步骤：$Step" }
    }
    @{ Success = $true; Error = ''; Reboot = $reboot } | ConvertTo-Json | Set-Content -LiteralPath $Result -Encoding utf8
}
catch {
    $_ | Out-String | Out-File -LiteralPath $Log -Append -Encoding utf8
    $message = $_.Exception.Message
    if ($_.Exception -is [System.ComponentModel.Win32Exception]) { $message = (Get-FailureReason $_.Exception.NativeErrorCode $message) + "`n" + $message }
    @{ Success = $false; Error = ('安装失败：' + $message); Reboot = $reboot } | ConvertTo-Json | Set-Content -LiteralPath $Result -Encoding utf8
}
