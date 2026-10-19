<#
.SYNOPSIS
  Shows the Stack<T> undo history: closes every valve in Sub-Zone C, then undoes it.
#>
param([string]$Api = "http://localhost:5100")

$ErrorActionPreference = "Stop"

function Show-Valves {
    (Invoke-RestMethod "$Api/api/commands/shadows") |
        Where-Object { $_.metric -eq "valve_state" } |
        ForEach-Object { Write-Host ("   {0,-12} desired={1,-5} reported={2}" -f $_.name, $_.desired, $_.reported) }
}

Write-Host "Valves before:" -ForegroundColor Cyan; Show-Valves

$body = @{ action = "CloseAllValves"; subZoneId = "sz-c"; issuedBy = "powershell" } | ConvertTo-Json
$closed = Invoke-RestMethod -Method Post -Uri "$Api/api/commands" -ContentType "application/json" -Body $body
Write-Host $closed.message -ForegroundColor Yellow
Write-Host ("Undo stack top: {0}" -f $closed.history.undoStack[0].label)
Start-Sleep -Seconds 2
Write-Host "Valves after closing:" -ForegroundColor Cyan; Show-Valves

$undo = Invoke-RestMethod -Method Post -Uri "$Api/api/commands/undo?issuedBy=powershell"
Write-Host $undo.message -ForegroundColor Green
Start-Sleep -Seconds 2
Write-Host "Valves after undo:" -ForegroundColor Cyan; Show-Valves
