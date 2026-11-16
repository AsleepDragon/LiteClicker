[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [ValidateRange(1,7200)][int]$Seconds = 60,
    [string]$OutputPath = (Join-Path (Get-Location) 'process-measurement.csv')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$logical = [Environment]::ProcessorCount
$process = Get-Process -Id $ProcessId
$initialCpu = $process.TotalProcessorTime.TotalSeconds
$clock = [Diagnostics.Stopwatch]::StartNew()
$previousSeconds = 0.0
$previousCpu = $initialCpu
$rows = [Collections.Generic.List[object]]::new()
for ($i = 0; $i -lt $Seconds; $i++) {
    Start-Sleep -Milliseconds 1000
    $process.Refresh()
    if ($process.HasExited) { throw '目标进程在测量期间退出。' }
    $time = $clock.Elapsed.TotalSeconds
    $cpu = $process.TotalProcessorTime.TotalSeconds
    $rows.Add([pscustomobject]@{
        ElapsedSeconds = $time
        CpuPercentOfMachine = 100 * ($cpu - $previousCpu) / ($time - $previousSeconds) / $logical
        WorkingSetMiB = $process.WorkingSet64 / 1MB
        PrivateBytesMiB = $process.PrivateMemorySize64 / 1MB
        Threads = $process.Threads.Count
        Handles = $process.HandleCount
    })
    $previousSeconds = $time; $previousCpu = $cpu
}
$rows | Export-Csv -LiteralPath $OutputPath -NoTypeInformation -Encoding UTF8
$computer = Get-CimInstance Win32_ComputerSystem
$cpuInfo = Get-CimInstance Win32_Processor | Select-Object -ExpandProperty Name
$summary = [pscustomobject]@{
    OS = [Environment]::OSVersion.VersionString
    CPU = $cpuInfo
    LogicalProcessors = $logical
    TotalMemoryGiB = $computer.TotalPhysicalMemory / 1GB
    ProcessId = $ProcessId
    ApplicationVersion = $process.MainModule.FileVersionInfo.FileVersion
    Seconds = $clock.Elapsed.TotalSeconds
    MeanCpuPercentOfMachine = 100 * ($previousCpu - $initialCpu) / $previousSeconds / $logical
    MeanWorkingSetMiB = ($rows | Measure-Object WorkingSetMiB -Average).Average
    PeakWorkingSetMiB = ($rows | Measure-Object WorkingSetMiB -Maximum).Maximum
}
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$OutputPath.summary.json" -Encoding UTF8
$summary | Format-List
