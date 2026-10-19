<#
.SYNOPSIS
  Exercises the dictionary structures:
   1. O(1) device lookup by MAC in any format (Dictionary<MacKey, SensorDevice>)
   2. Inverted-index search ("pump a") and category / state filters (Dictionary<..., HashSet<MacKey>>)
   3. A timeline read from the device's SortedDictionary
   4. The Dictionary vs List benchmark with hash-collision counts
#>
param([string]$Api = "http://localhost:5100")

$ErrorActionPreference = "Stop"

$first = (Invoke-RestMethod "$Api/api/devices") | Select-Object -First 1
$mac = $first.macAddress
$dashless = $mac.Replace(":", "").ToLower()

Write-Host "1) Exact MAC lookup using a different format ($dashless):" -ForegroundColor Cyan
$exact = Invoke-RestMethod "$Api/api/stream/devices?query=$dashless"
Write-Host ("   {0} -> {1} in {2} us via: {3}" -f $dashless, $exact.results[0].device.displayName, $exact.elapsedMicroseconds, $exact.strategy)

Write-Host "2) Search 'pump a' plus filters:" -ForegroundColor Cyan
$pumps = Invoke-RestMethod "$Api/api/stream/devices?query=pump%20a"
$pumps.results | ForEach-Object { Write-Host ("   {0} ({1})" -f $_.device.displayName, $_.state) }
Write-Host ("   strategy: {0}" -f $pumps.strategy)
$actuators = Invoke-RestMethod "$Api/api/stream/devices?category=Actuator"
Write-Host ("   Actuators: {0} devices" -f $actuators.matched)
$alerting = Invoke-RestMethod "$Api/api/stream/devices?alertsOnly=true"
Write-Host ("   Devices with active alerts: {0}" -f $alerting.matched)

Write-Host "3) Timeline from the SortedDictionary (last 15 minutes):" -ForegroundColor Cyan
$timeline = Invoke-RestMethod "$Api/api/devices/$dashless/timeline?minutes=15&maxPoints=60"
Write-Host ("   {0}: {1} stored points -> {2} buckets in {3} us, first {4}, last {5}" -f $timeline.name, $timeline.storedPoints, $timeline.returnedPoints, $timeline.elapsedMicroseconds, $timeline.points[0].timestamp, $timeline.points[-1].timestamp)

Write-Host "4) Dictionary vs list scan benchmark:" -ForegroundColor Cyan
$bench = Invoke-RestMethod "$Api/api/diagnostics/lookup?devices=20000&lookups=5000"
Write-Host ("   {0}" -f $bench.summary)
