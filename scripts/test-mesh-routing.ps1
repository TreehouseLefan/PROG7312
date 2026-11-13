<#
.SYNOPSIS
  Part 3 walkthrough: traces a sensor's route by MAC, fails a gateway, shows the self-healed route,
  runs BFS from the gateway, restores it, and queries the sensor's history for 14:00–16:00 yesterday.
#>
param(
    [string]$Api = "http://localhost:5100",
    [string]$Mac = "24:0A:C4:0A:00:00",   # A-01 Soil Moisture
    [string]$Gateway = "gw-a1"
)

$ErrorActionPreference = "Stop"

function Show-Route($report, $title) {
    Write-Host $title -ForegroundColor Cyan
    if (-not $report.mst.reachable) { Write-Host "   (no route: $($report.status))" -ForegroundColor Red; return }
    foreach ($hop in $report.mst.hops) { Write-Host ("   {0,-28} -> {1,-28} {2,7} uJ" -f $hop.fromName, $hop.toName, $hop.costMicrojoules) }
    Write-Host ("   {0} hop(s), {1} uJ total, worst hop {2} uJ" -f $report.mst.hopCount, $report.mst.totalCostMicrojoules, $report.mst.bottleneckMicrojoules)
}

$mesh = Invoke-RestMethod "$Api/api/mesh"
Write-Host ("Mesh: {0} nodes, {1} links, MST {2} uJ (Prim) vs {3} uJ (Kruskal), agree = {4}" -f `
    $mesh.stats.nodes, $mesh.stats.links, $mesh.stats.mstCostMicrojoules, $mesh.stats.kruskalCostMicrojoules, $mesh.stats.primKruskalAgree)

$escaped = [uri]::EscapeDataString($Mac)
Show-Route (Invoke-RestMethod "$Api/api/mesh/route/$escaped") "Route for $Mac before the failure:"

Write-Host "`nFailing $Gateway ..." -ForegroundColor Yellow
$after = Invoke-RestMethod -Method Post "$Api/api/mesh/nodes/$Gateway/fail"
Write-Host ("   {0}" -f $after.events[0].detail)
Show-Route (Invoke-RestMethod "$Api/api/mesh/route/$escaped") "Route for $Mac after self-healing:"

$bfs = Invoke-RestMethod "$Api/api/mesh/traverse?from=gw-a2&mode=bfs"
Write-Host "`nBFS from gw-a2: $($bfs.summary)" -ForegroundColor Cyan
$bfs.hopAdvice | Select-Object -First 5 | ForEach-Object { Write-Host "   $($_.advice)" }

Write-Host "`nRestoring $Gateway ..." -ForegroundColor Green
$null = Invoke-RestMethod -Method Post "$Api/api/mesh/nodes/$Gateway/restore"

$yesterday = (Get-Date).Date.AddDays(-1)
$from = $yesterday.AddHours(14).ToString("o")
$to = $yesterday.AddHours(16).ToString("o")
$history = Invoke-RestMethod ("$Api/api/devices/$escaped/history?from={0}&to={1}&pageSize=5&order=asc" -f `
    [uri]::EscapeDataString($from), [uri]::EscapeDataString($to))
Write-Host ("`nHistory 14:00-16:00 yesterday for {0}: {1} readings, min {2}, mean {3:N2}, max {4} {5}" -f `
    $history.name, $history.summary.count, $history.summary.min, $history.summary.mean, $history.summary.max, $history.unit) -ForegroundColor Cyan
Write-Host ("   AVL height {0} (bound {1}); nodes visited {2} vs {3} for a list scan; {4} us" -f `
    $history.stats.treeHeight, $history.stats.maxAvlHeight,
    ($history.stats.pageNodesVisited + $history.stats.summaryNodesVisited + $history.stats.chartNodesVisited),
    $history.stats.linearScanComparisons, $history.stats.elapsedMicroseconds)
$history.items | ForEach-Object { Write-Host ("   {0:HH:mm}  {1}" -f ([datetimeoffset]$_.timestamp).ToLocalTime(), $_.value) }
