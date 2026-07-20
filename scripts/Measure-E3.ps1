<#
.SYNOPSIS
E3 の4条件をウォームアップ1回と指定回数で測定し、CSVへ出力します。

.DESCRIPTION
off、sdk、Collector稼働中のotlpは run ごとに開始条件をローテーションします。
Collector停止中のotlpだけは、Collector停止後に最後へまとめて実行します。
Collector稼働中の条件では file exporter の増分と ProcessJob ルート Span 数を取得します。
Collectorが未稼働ならotlpの2条件をスキップします。

.EXAMPLE
.\scripts\Measure-E3.ps1 -Target console -Runs 5

.EXAMPLE
.\scripts\Measure-E3.ps1 -Target winui -Runs 5 -GenerateData

.EXAMPLE
.\scripts\Measure-E3.ps1 -CollectorStopAction { docker stop otel-collector }
#>
[CmdletBinding()]
param(
    [ValidateSet("winui", "console")]
    [string]$Target = "winui",

    [ValidateRange(1, 1000)]
    [int]$Runs = 5,

    [string]$InputDirectory,

    [switch]$GenerateData,

    [ValidateRange(1, 1000000)]
    [int]$GenerateCount = 100,

    [ValidateRange(1, 10240)]
    [int]$GenerateSizeMb = 1,

    [ValidateRange(1, 1024)]
    [int]$Parallel = 2,

    [ValidateRange(0, 600000)]
    [int]$FlushTimeoutMs = 5000,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$SkipBuild,

    [string]$ApplicationPath,

    [string]$ResultsDirectory,

    [string]$CollectorOutputPath,

    [string]$CollectorHost = "localhost",

    [ValidateRange(1, 65535)]
    [int]$CollectorPort = 4317,

    [string]$CollectorProcessName = "otelcol-contrib",

    [scriptblock]$CollectorStopAction,

    [ValidateRange(1, 60)]
    [int]$CollectorWaitSeconds = 5
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($InputDirectory)) {
    $InputDirectory = Join-Path $repositoryRoot "data\in"
}
elseif (-not [IO.Path]::IsPathRooted($InputDirectory)) {
    $InputDirectory = Join-Path $repositoryRoot $InputDirectory
}

if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path $repositoryRoot "results"
}
elseif (-not [IO.Path]::IsPathRooted($ResultsDirectory)) {
    $ResultsDirectory = Join-Path $repositoryRoot $ResultsDirectory
}

if ([string]::IsNullOrWhiteSpace($CollectorOutputPath)) {
    $CollectorOutputPath = Join-Path $repositoryRoot "artifacts\otel\telemetry.json"
}
elseif (-not [IO.Path]::IsPathRooted($CollectorOutputPath)) {
    $CollectorOutputPath = Join-Path $repositoryRoot $CollectorOutputPath
}

$timestamp = [DateTime]::UtcNow.ToString("yyyyMMddTHHmmssZ")
$csvPath = Join-Path $ResultsDirectory "e3_$timestamp.csv"
$environmentPath = Join-Path $ResultsDirectory "e3_${timestamp}_environment.txt"
$logDirectory = Join-Path $ResultsDirectory "e3_${timestamp}_logs"
$workDirectory = Join-Path $ResultsDirectory "e3_${timestamp}_work"

New-Item -ItemType Directory -Force $ResultsDirectory, $logDirectory, $workDirectory | Out-Null

function ConvertTo-CommandLineArgument {
    param([AllowEmptyString()][string]$Value)

    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }

    $builder = New-Object Text.StringBuilder
    [void]$builder.Append('"')
    $backslashCount = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashCount++
            continue
        }

        if ($character -eq '"') {
            [void]$builder.Append('\', (2 * $backslashCount) + 1)
            [void]$builder.Append('"')
        }
        else {
            if ($backslashCount -gt 0) {
                [void]$builder.Append('\', $backslashCount)
            }
            [void]$builder.Append($character)
        }
        $backslashCount = 0
    }

    if ($backslashCount -gt 0) {
        [void]$builder.Append('\', 2 * $backslashCount)
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Test-TcpEndpoint {
    param(
        [string]$HostName,
        [int]$Port,
        [int]$TimeoutMilliseconds = 750
    )

    $client = New-Object Net.Sockets.TcpClient
    try {
        $asyncResult = $client.BeginConnect($HostName, $Port, $null, $null)
        if (-not $asyncResult.AsyncWaitHandle.WaitOne($TimeoutMilliseconds, $false)) {
            return $false
        }
        $client.EndConnect($asyncResult)
        return $true
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

function Find-ApplicationExecutable {
    param(
        [ValidateSet("winui", "console")]
        [string]$ApplicationTarget,
        [string]$ExplicitPath
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $candidate = $ExplicitPath
        if (-not [IO.Path]::IsPathRooted($candidate)) {
            $candidate = Join-Path $repositoryRoot $candidate
        }
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            throw "指定されたアプリ実行ファイルが見つかりません。"
        }
        return [IO.Path]::GetFullPath($candidate)
    }

    $projectName = if ($ApplicationTarget -eq "winui") { "App.WinUI" } else { "App.Console" }
    $searchRoot = Join-Path $repositoryRoot "src\$projectName\bin"
    $configurationPattern = '[\\/]' + [regex]::Escape($Configuration) + '[\\/]'
    $candidate = Get-ChildItem -LiteralPath $searchRoot -Filter "$projectName.exe" -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object {
            $_.FullName -match $configurationPattern -and
            $_.FullName -notmatch '[\\/]ref[\\/]'
        } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $candidate) {
        throw "$projectName の実行ファイルが見つかりません。-SkipBuild を外して再実行してください。"
    }
    return $candidate.FullName
}

function Invoke-Build {
    param([ValidateSet("winui", "console")][string]$ApplicationTarget)

    $projectName = if ($ApplicationTarget -eq "winui") { "App.WinUI" } else { "App.Console" }
    $projectPath = Join-Path $repositoryRoot "src\$projectName\$projectName.csproj"
    Write-Host "Building $projectName ($Configuration)..."
    & dotnet build $projectPath --configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "$projectName のビルドに失敗しました。"
    }
}

function Invoke-MeasuredProcess {
    param(
        [string]$Executable,
        [string[]]$Arguments,
        [string]$StandardOutputPath,
        [string]$StandardErrorPath
    )

    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $Executable
    $startInfo.Arguments = (($Arguments | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join " ")
    $startInfo.WorkingDirectory = $repositoryRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    if (-not $process.Start()) {
        throw "アプリを起動できませんでした。"
    }

    $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
    $standardErrorTask = $process.StandardError.ReadToEndAsync()
    [long]$privateBytesPeak = 0
    do {
        try {
            $process.Refresh()
            $privateBytesPeak = [Math]::Max($privateBytesPeak, $process.PrivateMemorySize64)
        }
        catch {
            # 終了直後はプロセス情報を更新できない場合がある。
        }
    } while (-not $process.WaitForExit(50))

    $process.WaitForExit()
    $stopwatch.Stop()
    $standardOutput = $standardOutputTask.GetAwaiter().GetResult()
    $standardError = $standardErrorTask.GetAwaiter().GetResult()
    Set-Content -LiteralPath $StandardOutputPath -Value $standardOutput -Encoding UTF8
    Set-Content -LiteralPath $StandardErrorPath -Value $standardError -Encoding UTF8

    $result = [pscustomobject]@{
        ExitCode = $process.ExitCode
        ElapsedMilliseconds = $stopwatch.ElapsedMilliseconds
        PrivateBytesPeak = $privateBytesPeak
        StandardOutput = $standardOutput
        StandardError = $standardError
    }
    $process.Dispose()
    return $result
}

function Get-ExporterLength {
    if (-not (Test-Path -LiteralPath $CollectorOutputPath -PathType Leaf)) {
        return [long]0
    }
    return (Get-Item -LiteralPath $CollectorOutputPath).Length
}

function Wait-ExporterStable {
    param([long]$InitialLength)

    $deadline = [DateTime]::UtcNow.AddSeconds($CollectorWaitSeconds)
    [long]$previousLength = -1
    $stableCount = 0
    do {
        Start-Sleep -Milliseconds 250
        [long]$currentLength = Get-ExporterLength
        if ($currentLength -gt $InitialLength -and $currentLength -eq $previousLength) {
            $stableCount++
        }
        else {
            $stableCount = 0
        }
        if ($stableCount -ge 2) {
            return
        }
        $previousLength = $currentLength
    } while ([DateTime]::UtcNow -lt $deadline)
}

function Read-ExporterDelta {
    param([long]$InitialLength)

    if (-not (Test-Path -LiteralPath $CollectorOutputPath -PathType Leaf)) {
        return [pscustomobject]@{ ByteCount = [long]0; ProcessJobCount = 0; Note = "collector_output_missing" }
    }

    $stream = New-Object IO.FileStream(
        $CollectorOutputPath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite)
    try {
        $note = ""
        if ($stream.Length -lt $InitialLength) {
            $InitialLength = 0
            $note = "collector_output_rotated"
        }
        [void]$stream.Seek($InitialLength, [IO.SeekOrigin]::Begin)
        $memory = New-Object IO.MemoryStream
        try {
            $stream.CopyTo($memory)
            $bytes = $memory.ToArray()
        }
        finally {
            $memory.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    $text = [Text.Encoding]::UTF8.GetString($bytes)
    $processJobCount = [regex]::Matches($text, '"name"\s*:\s*"ProcessJob"').Count
    return [pscustomobject]@{
        ByteCount = [long]$bytes.LongLength
        ProcessJobCount = $processJobCount
        Note = $note
    }
}

function Stop-ConfiguredCollector {
    if ($null -ne $CollectorStopAction) {
        Write-Host "Stopping Collector with -CollectorStopAction..."
        & $CollectorStopAction
        return
    }

    $collectorProcesses = @(Get-Process -Name $CollectorProcessName -ErrorAction SilentlyContinue)
    if ($collectorProcesses.Count -eq 0) {
        throw "Collectorプロセスが見つかりません。別の起動方式では -CollectorStopAction を指定してください。"
    }
    Write-Host "Stopping Collector process..."
    $collectorProcesses | Stop-Process -ErrorAction Stop
    $collectorProcesses | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

function Wait-CollectorStopped {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        if (-not (Test-TcpEndpoint -HostName $CollectorHost -Port $CollectorPort)) {
            return $true
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    return $false
}

function Get-EnvironmentLines {
    param([string]$Executable)

    $cpu = "unknown"
    $ramBytes = "unknown"
    $os = [Environment]::OSVersion.VersionString
    try {
        $processor = Get-CimInstance -ClassName Win32_Processor | Select-Object -First 1
        if ($null -ne $processor) { $cpu = $processor.Name.Trim() }
        $computerSystem = Get-CimInstance -ClassName Win32_ComputerSystem
        if ($null -ne $computerSystem) { $ramBytes = [string]$computerSystem.TotalPhysicalMemory }
        $operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem
        if ($null -ne $operatingSystem) {
            $os = "$($operatingSystem.Caption) $($operatingSystem.Version) build $($operatingSystem.BuildNumber)"
        }
    }
    catch {
        # CIMが使えない環境では取得できた標準情報だけを記録する。
    }

    $dotnetVersion = (& dotnet --version 2>$null | Select-Object -First 1)
    $applicationVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($Executable).ProductVersion
    $collectorVersion = "not_running"
    $collectorProcess = Get-Process -Name $CollectorProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $collectorProcess) {
        try {
            $productVersion = $collectorProcess.MainModule.FileVersionInfo.ProductVersion
            $collectorVersion = if ([string]::IsNullOrWhiteSpace($productVersion)) { "unknown" } else { $productVersion }
        }
        catch {
            $collectorVersion = "unknown"
        }
    }

    return @(
        "timestamp_utc=$([DateTime]::UtcNow.ToString('O'))",
        "target=$Target",
        "cpu=$cpu",
        "ram_bytes=$ramBytes",
        "os=$os",
        "powershell=$($PSVersionTable.PSVersion)",
        "dotnet_sdk=$dotnetVersion",
        "application_version=$applicationVersion",
        "collector_version=$collectorVersion",
        "collector_config_expected=collector/otelcol-e3.yaml",
        "measurement_limitations=OS file cache and antivirus scanning are not controlled"
    )
}

if (-not $SkipBuild) {
    Invoke-Build -ApplicationTarget $Target
    if ($GenerateData -and $Target -ne "console") {
        Invoke-Build -ApplicationTarget "console"
    }
}

$applicationExecutable = Find-ApplicationExecutable -ApplicationTarget $Target -ExplicitPath $ApplicationPath

if ($GenerateData) {
    $consoleExecutable = if ($Target -eq "console") {
        $applicationExecutable
    }
    else {
        Find-ApplicationExecutable -ApplicationTarget "console" -ExplicitPath ""
    }
    New-Item -ItemType Directory -Force $InputDirectory | Out-Null
    Write-Host "Generating deterministic input data..."
    & $consoleExecutable generate-data --dir $InputDirectory --count $GenerateCount --size-mb $GenerateSizeMb
    if ($LASTEXITCODE -ne 0) {
        throw "入力データの生成に失敗しました。"
    }
}

if (-not (Test-Path -LiteralPath $InputDirectory -PathType Container)) {
    throw "入力ディレクトリがありません。既存データを用意するか -GenerateData を指定してください。"
}

$jobCount = @(Get-ChildItem -LiteralPath $InputDirectory -File).Count
if ($jobCount -eq 0) {
    throw "入力ディレクトリにファイルがありません。"
}

Get-EnvironmentLines -Executable $applicationExecutable | Set-Content -LiteralPath $environmentPath -Encoding UTF8

$rows = New-Object Collections.ArrayList
function Save-Rows {
    $rows | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8
}

function Add-SkippedCondition {
    param([string]$Condition, [string]$Reason)

    [void]$rows.Add([pscustomobject][ordered]@{
        condition = $Condition
        target = $Target
        run_index = -1
        process_elapsed_ms = $null
        pipeline_elapsed_ms = $null
        private_bytes_peak = $null
        flush_elapsed_ms = $null
        exporter_file_delta_bytes = $null
        processjob_rootspan_missing = $null
        notes = "skipped:$Reason"
    })
    Save-Rows
}

function Invoke-ConditionRun {
    param(
        [string]$Condition,
        [string]$OtelMode,
        [int]$RunIndex,
        [string]$AdditionalNote = ""
    )

        $runKind = if ($RunIndex -eq 0) { "warmup" } else { "run$RunIndex" }
        Write-Host "[$Condition] $runKind/$Runs"
        $runOutputDirectory = Join-Path $workDirectory "${Condition}_$runKind"
        New-Item -ItemType Directory -Force $runOutputDirectory | Out-Null
        $stdoutPath = Join-Path $logDirectory "${Condition}_${runKind}.stdout.log"
        $stderrPath = Join-Path $logDirectory "${Condition}_${runKind}.stderr.log"

        $arguments = if ($Target -eq "console") {
            @(
                "run", "--input", $InputDirectory, "--output", $runOutputDirectory,
                "--otel", $OtelMode, "--parallel", [string]$Parallel,
                "--flush-timeout-ms", [string]$FlushTimeoutMs
            )
        }
        else {
            @(
                "--auto-run", "--exit-after", "--input", $InputDirectory,
                "--output", $runOutputDirectory, "--otel", $OtelMode,
                "--parallel", [string]$Parallel, "--flush-timeout-ms", [string]$FlushTimeoutMs
            )
        }

        [long]$exporterLengthBefore = Get-ExporterLength
        $processResult = Invoke-MeasuredProcess `
            -Executable $applicationExecutable `
            -Arguments $arguments `
            -StandardOutputPath $stdoutPath `
            -StandardErrorPath $stderrPath

        $notes = New-Object Collections.Generic.List[string]
        if ($RunIndex -eq 0) { $notes.Add("warmup") }
        if (-not [string]::IsNullOrWhiteSpace($AdditionalNote)) { $notes.Add($AdditionalNote) }
        if ($processResult.ExitCode -ne 0) { $notes.Add("exit_code=$($processResult.ExitCode)") }
        if (-not [string]::IsNullOrWhiteSpace($processResult.StandardError)) { $notes.Add("stderr_logged") }

        $pipelineElapsedMilliseconds = $null
        $pipelineMatches = [regex]::Matches(
            $processResult.StandardOutput,
            '(?m)^pipeline elapsed_ms=(\d+)\s*$')
        if ($pipelineMatches.Count -gt 0) {
            $pipelineElapsedMilliseconds = [long]$pipelineMatches[$pipelineMatches.Count - 1].Groups[1].Value
        }
        else {
            $notes.Add("pipeline_line_missing")
        }

        $flushMilliseconds = $null
        $flushMatches = [regex]::Matches(
            $processResult.StandardOutput,
            '(?m)^otel flush mode=\S+ elapsed_ms=(\d+) success=(true|false)\s*$')
        if ($flushMatches.Count -gt 0) {
            $lastFlush = $flushMatches[$flushMatches.Count - 1]
            $flushMilliseconds = [long]$lastFlush.Groups[1].Value
            if ($lastFlush.Groups[2].Value -ne "true") { $notes.Add("flush_failed") }
        }
        else {
            $notes.Add("flush_line_missing")
        }

        $exporterFileDeltaBytes = $null
        $processJobRootSpanMissing = $null
        if ($Condition -eq "otlp_up") {
            Wait-ExporterStable -InitialLength $exporterLengthBefore
            $exporterDelta = Read-ExporterDelta -InitialLength $exporterLengthBefore
            $exporterFileDeltaBytes = $exporterDelta.ByteCount
            $processJobRootSpanMissing = [Math]::Max(0, $jobCount - $exporterDelta.ProcessJobCount)
            if (-not [string]::IsNullOrWhiteSpace($exporterDelta.Note)) { $notes.Add($exporterDelta.Note) }
            if ($exporterDelta.ProcessJobCount -gt $jobCount) { $notes.Add("collector_has_concurrent_input") }
        }
        elseif ($Condition -eq "otlp_down") {
            $exporterFileDeltaBytes = [long]0
            $processJobRootSpanMissing = $jobCount
        }

        [void]$rows.Add([pscustomobject][ordered]@{
            condition = $Condition
            target = $Target
            run_index = $RunIndex
            process_elapsed_ms = $processResult.ElapsedMilliseconds
            pipeline_elapsed_ms = $pipelineElapsedMilliseconds
            private_bytes_peak = $processResult.PrivateBytesPeak
            flush_elapsed_ms = $flushMilliseconds
            exporter_file_delta_bytes = $exporterFileDeltaBytes
            processjob_rootspan_missing = $processJobRootSpanMissing
            notes = ($notes -join ";")
        })
        Save-Rows
}

$collectorAvailable = Test-TcpEndpoint -HostName $CollectorHost -Port $CollectorPort
$interleavedConditions = @(
    [pscustomobject]@{ Condition = "off"; OtelMode = "off" },
    [pscustomobject]@{ Condition = "sdk"; OtelMode = "sdk" }
)
if ($collectorAvailable) {
    $interleavedConditions += [pscustomobject]@{ Condition = "otlp_up"; OtelMode = "otlp" }
}

foreach ($condition in $interleavedConditions) {
    Invoke-ConditionRun -Condition $condition.Condition -OtelMode $condition.OtelMode -RunIndex 0
}

$conditionCount = $interleavedConditions.Count
for ($runIndex = 1; $runIndex -le $Runs; $runIndex++) {
    $rotationStart = ($runIndex - 1) % $conditionCount
    for ($position = 0; $position -lt $conditionCount; $position++) {
        $condition = $interleavedConditions[($rotationStart + $position) % $conditionCount]
        Invoke-ConditionRun `
            -Condition $condition.Condition `
            -OtelMode $condition.OtelMode `
            -RunIndex $runIndex `
            -AdditionalNote "interleaved_position=$($position + 1)/$conditionCount"
    }
}

if (-not $collectorAvailable) {
    Write-Warning "Collectorがlocalhost相当の指定エンドポイントで応答しないため、otlp_up/otlp_downをスキップします。"
    Add-SkippedCondition -Condition "otlp_up" -Reason "collector_not_running"
    Add-SkippedCondition -Condition "otlp_down" -Reason "collector_not_running"
}
else {
    try {
        Stop-ConfiguredCollector
        if (-not (Wait-CollectorStopped)) {
            throw "Collectorの待受ポートが閉じませんでした。"
        }
        Invoke-ConditionRun `
            -Condition "otlp_down" `
            -OtelMode "otlp" `
            -RunIndex 0 `
            -AdditionalNote "order_fixed:last_after_collector_stop"
        for ($runIndex = 1; $runIndex -le $Runs; $runIndex++) {
            Invoke-ConditionRun `
                -Condition "otlp_down" `
                -OtelMode "otlp" `
                -RunIndex $runIndex `
                -AdditionalNote "order_fixed:last_after_collector_stop"
        }
    }
    catch {
        Write-Warning "Collector停止条件を実行できませんでした: $($_.Exception.Message)"
        Add-SkippedCondition -Condition "otlp_down" -Reason "collector_stop_failed"
    }
}

Write-Host "CSV: results/$([IO.Path]::GetFileName($csvPath))"
Write-Host "Environment: results/$([IO.Path]::GetFileName($environmentPath))"
Write-Host "Raw logs: results/$([IO.Path]::GetFileName($logDirectory))/"
