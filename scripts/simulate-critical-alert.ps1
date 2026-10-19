<#
.SYNOPSIS
  Sends one critical reading to the Smart-X API exactly like an ESP32 would (HTTP POST /api/telemetry)
  and shows that it took the PRIORITY lane.

.EXAMPLE
  .\scripts\simulate-critical-alert.ps1                 # critical moisture drop on a Sub-Zone B sensor
  .\scripts\simulate-critical-alert.ps1 -Kind power     # severe power spike on a Sub-Zone C meter
#>
param(
    [ValidateSet("moisture", "power")]
    [string]$Kind = "moisture",
    [string]$Api = "http://localhost:5100"
)

$ErrorActionPreference = "Stop"

# Look the device up through the search API (backed by the Dictionary / inverted index).
$query = if ($Kind -eq "power") { "pump a meter" } else { "b-01 soil moisture" }
$search = Invoke-RestMethod "$Api/api/stream/devices?query=$([uri]::EscapeDataString($query))"
$device = $search.results | Select-Object -First 1
if (-not $device) { throw "No device matched '$query'. Is the API running and seeded?" }

$mac = $device.device.macAddress
$body = if ($Kind -eq "power") {
    @{ deviceMac = $mac; payloadType = "int"; value = 6500 } | ConvertTo-Json
} else {
    @{ deviceMac = $mac; payloadType = "float"; value = 12.5 } | ConvertTo-Json
}

Write-Host "POST $Api/api/telemetry  ($($device.device.displayName), $mac)" -ForegroundColor Cyan
$result = Invoke-RestMethod -Method Post -Uri "$Api/api/telemetry" -ContentType "application/json" -Body $body

Write-Host ("Lane:      {0}" -f $result.lane) -ForegroundColor $(if ($result.lane -eq "Priority") { "Red" } else { "Yellow" })
Write-Host ("Reason:    {0}" -f $result.priorityReason)
Write-Host ("Sequence:  #{0}" -f $result.sequence)
Write-Host ("Queued ahead in FIFO lane: {0} | priority lane depth: {1}" -f $result.standardDepth, $result.priorityDepth)

Start-Sleep -Milliseconds 800
$board = Invoke-RestMethod "$Api/api/alerts"
Write-Host ""
Write-Host "Active alerts (HashSet<AlertKey>):" -ForegroundColor Cyan
$board.active | Select-Object type, severity, deviceName, duplicatesSuppressed | Format-Table -AutoSize
Write-Host ("Duplicates suppressed so far: {0}" -f $board.duplicatesSuppressed)
