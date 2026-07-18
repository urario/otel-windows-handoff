<#
.SYNOPSIS
E2 の WPR 記録と ProcDump ハング監視を補助します。

.DESCRIPTION
WPR は必ずこのスクリプトの -Start で開始し、開始成功を確認してからアプリを
起動してください。この順序により、EventSource の有効化前イベント欠落を避けます。
記録終了は -Stop、UIフリーズのDump監視は -Hang を使います。

.EXAMPLE
.\scripts\Invoke-E2Capture.ps1 -Start

.EXAMPLE
.\scripts\Invoke-E2Capture.ps1 -Hang -ProcessName App.WinUI -Out .\artifacts\dumps

.EXAMPLE
.\scripts\Invoke-E2Capture.ps1 -Stop .\artifacts\handoff.etl
#>
[CmdletBinding()]
param(
    [switch]$Start,

    [string]$Stop,

    [switch]$Hang,

    [string]$ProcessName,

    [string]$Out,

    [string]$Profile
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-RequiredTool {
    param([string]$Name, [string]$InstallHint)

    $command = Get-Command $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $command) {
        throw "$Name が見つかりません。$InstallHint"
    }
    return $command
}

function Get-ToolVersion {
    param($Command)

    try {
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($Command.Source).FileVersion
        if ([string]::IsNullOrWhiteSpace($version)) { return "unknown" }
        return $version
    }
    catch {
        return "unknown"
    }
}

$operationCount = @($Start.IsPresent, (-not [string]::IsNullOrWhiteSpace($Stop)), $Hang.IsPresent) |
    Where-Object { $_ } |
    Measure-Object |
    Select-Object -ExpandProperty Count
if ($operationCount -ne 1) {
    throw "-Start、-Stop <etl>、-Hang のいずれか1つだけを指定してください。"
}

if ($Start) {
    if (-not (Test-Administrator)) {
        throw "WPRの開始には管理者権限が必要です。PowerShellを管理者として開き直してください。"
    }
    if ([string]::IsNullOrWhiteSpace($Profile)) {
        $Profile = Join-Path $repositoryRoot "collector\handoff.wprp"
    }
    elseif (-not [IO.Path]::IsPathRooted($Profile)) {
        $Profile = Join-Path $repositoryRoot $Profile
    }
    if (-not (Test-Path -LiteralPath $Profile -PathType Leaf)) {
        throw "WPRプロファイルが見つかりません。"
    }

    $wpr = Get-RequiredTool -Name "wpr.exe" -InstallHint "Windows Performance Toolkitをインストールしてください。"
    Write-Host "WPR version=$(Get-ToolVersion $wpr)"
    & $wpr.Source -start $Profile -filemode
    if ($LASTEXITCODE -ne 0) {
        throw "WPR記録を開始できませんでした。"
    }
    Write-Host "WPR記録を開始しました。ここで初めて、別のPowerShellからアプリを起動してください。"
    exit 0
}

if (-not [string]::IsNullOrWhiteSpace($Stop)) {
    if (-not (Test-Administrator)) {
        throw "WPRの停止には管理者権限が必要です。PowerShellを管理者として開き直してください。"
    }
    $etlPath = $Stop
    if (-not [IO.Path]::IsPathRooted($etlPath)) {
        $etlPath = Join-Path $repositoryRoot $etlPath
    }
    if ([IO.Path]::GetExtension($etlPath) -ne ".etl") {
        throw "-Stop には拡張子 .etl の出力ファイルを指定してください。"
    }
    $etlDirectory = Split-Path -Parent $etlPath
    New-Item -ItemType Directory -Force $etlDirectory | Out-Null

    $wpr = Get-RequiredTool -Name "wpr.exe" -InstallHint "Windows Performance Toolkitをインストールしてください。"
    Write-Host "WPR version=$(Get-ToolVersion $wpr)"
    & $wpr.Source -stop $etlPath
    if ($LASTEXITCODE -ne 0) {
        throw "WPR記録を停止できませんでした。"
    }
    Write-Host "WPR記録を停止しました。ETLは指定した出力先に保存されています。"
    exit 0
}

if ([string]::IsNullOrWhiteSpace($ProcessName)) {
    throw "-Hang では -ProcessName を指定してください。"
}
if ([string]::IsNullOrWhiteSpace($Out)) {
    $Out = Join-Path $repositoryRoot "artifacts\dumps"
}
elseif (-not [IO.Path]::IsPathRooted($Out)) {
    $Out = Join-Path $repositoryRoot $Out
}
New-Item -ItemType Directory -Force $Out | Out-Null

$normalizedProcessName = if ($ProcessName.EndsWith(".exe", [StringComparison]::OrdinalIgnoreCase)) {
    $ProcessName.Substring(0, $ProcessName.Length - 4)
}
else {
    $ProcessName
}
$targetProcesses = @(Get-Process -Name $normalizedProcessName -ErrorAction SilentlyContinue)
if ($targetProcesses.Count -eq 0) {
    throw "対象プロセスが見つかりません。WPR開始後にアプリを起動してから再実行してください。"
}
if ($targetProcesses.Count -gt 1) {
    throw "同名プロセスが複数あります。対象を1つにしてから再実行してください。"
}

$targetProcess = $targetProcesses[0]
$dumpTimestamp = [DateTime]::UtcNow.ToString("yyyyMMddTHHmmssZ")
$dumpFileName = "$($targetProcess.Id)_$dumpTimestamp.dmp"
$dumpPath = Join-Path $Out $dumpFileName
$procDump = Get-RequiredTool -Name "procdump.exe" -InstallHint "Sysinternals ProcDumpをインストールしてPATHへ追加してください。"
Write-Host "ProcDump version=$(Get-ToolVersion $procDump)"

$startInfo = New-Object Diagnostics.ProcessStartInfo
$startInfo.FileName = $procDump.Source
$startInfo.Arguments = "-accepteula -h -ma $($targetProcess.Id) `"$dumpPath`""
$startInfo.WorkingDirectory = $repositoryRoot
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$monitor = [Diagnostics.Process]::Start($startInfo)
Start-Sleep -Milliseconds 500
if ($monitor.HasExited -and $monitor.ExitCode -ne 0) {
    throw "ProcDumpハング監視を開始できませんでした。終了コード=$($monitor.ExitCode)"
}

Write-Host "ProcDumpハング監視を開始しました。target_pid=$($targetProcess.Id) monitor_pid=$($monitor.Id)"
Write-Host "dump_file=$dumpFileName"
Write-Host "監視開始後にUIフリーズを発生させ、Dump取得後に -Stop でWPRを停止してください。"
