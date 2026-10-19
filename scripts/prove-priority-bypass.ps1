<#
.SYNOPSIS
  Proves that critical packets bypass the FIFO queue: the API pauses its dispatcher, enqueues N standard
  packets, then one critical packet, resumes, and reports where the critical packet was processed.

.EXAMPLE
  .\scripts\prove-priority-bypass.ps1 -Count 20000
#>
param(
    [int]$Count = 5000,
    [string]$Api = "http://localhost:5100"
)

$ErrorActionPreference = "Stop"
$report = Invoke-RestMethod -Method Post -Uri "$Api/api/stream/burst?count=$Count"

Write-Host $report.summary
Write-Host ""
Write-Host ("Critical packet processed at position: #{0}" -f $report.criticalProcessedPosition) -ForegroundColor $(if ($report.criticalProcessedPosition -eq 1) { "Green" } else { "Red" })
Write-Host ("Critical wait:  {0} ms" -f $report.criticalWaitMs)
Write-Host ("Standard wait:  avg {0} ms, max {1} ms" -f $report.standardAvgWaitMs, $report.standardMaxWaitMs)
