[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$CsvPath,[ValidateRange(1,100)][int]$TargetCps = 10)
$ErrorActionPreference = 'Stop'
$lines = @(Get-Content -LiteralPath $CsvPath)
$header = $lines[0]
if ($header -notmatch 'measurementStartTicks=(\d+)') { throw 'CSV 未启用固定 60 秒窗口。' }
$start = [double]$Matches[1]
if ($header -notmatch 'stopwatchFrequency=(\d+)') { throw '缺少单调时钟频率。' }
$frequency = [double]$Matches[1]
if ($header -match 'overflow=True') { throw '接收端记录溢出，不可用于验收。' }
$data = @($lines | Where-Object { -not $_.StartsWith('#') } | ConvertFrom-Csv)
if ($data.Count -eq 0) { throw '没有收到鼠标消息，无法判断是否完成测量。' }
# Require evidence that export occurred after the full window; exportedAtTicks is set by the receiver.
if ($header -notmatch 'exportedAtTicks=(\d+)' -or [double]$Matches[1] -lt ($start + 60 * $frequency)) { throw '固定 60 秒窗口尚未结束。' }
$receivedDown = @($data | Where-Object { $_.liteClickerMarked -eq 'True' -and $_.inFixedWindow -eq 'True' -and $_.message -in @('WM_LBUTTONDOWN','WM_LBUTTONDBLCLK') }).Count
$receivedUp = @($data | Where-Object { $_.liteClickerMarked -eq 'True' -and $_.inFixedWindow -eq 'True' -and $_.message -eq 'WM_LBUTTONUP' }).Count
$actual = $receivedDown / 60.0
$deviation = [Math]::Abs($actual - $TargetCps) / $TargetCps
$limit = if ($TargetCps -le 20) { 0.10 } else { 0.15 }
[pscustomobject]@{TargetCps=$TargetCps;ReceivedDown=$receivedDown;ReceivedUp=$receivedUp;ActualCps=$actual;DeviationPercent=100*$deviation;AllowedPercent=100*$limit;FrequencyPassed=$deviation -le $limit} | Format-List
if ($deviation -gt $limit) { exit 1 }
