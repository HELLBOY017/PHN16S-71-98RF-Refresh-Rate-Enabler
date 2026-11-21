# Auto-elevate to Administrator if needed
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -Verb RunAs
    exit
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Restoring NVIDIA Display & Removing EDID Overrides" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# Terminate enabler process if running
Get-Process | Where-Object { $_.ProcessName -like "*Enabler*" } | ForEach-Object {
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
}

$base = "HKLM:\SYSTEM\CurrentControlSet\Enum\DISPLAY\SDC41AB"
$removed = $false

if (Test-Path $base) {
    Get-ChildItem -Path $base -ErrorAction SilentlyContinue | ForEach-Object {
        $overridePath = Join-Path $_.PSPath "Device Parameters\EDID_OVERRIDE"
        if (Test-Path $overridePath) {
            try {
                Remove-Item -LiteralPath $overridePath -Recurse -Force -ErrorAction Stop
                Write-Host "SUCCESS: Removed EDID_OVERRIDE from $($_.PSChildName)" -ForegroundColor Green
                $removed = $true
            } catch {
                Write-Host "ERROR: Failed to remove $overridePath - $($_.Exception.Message)" -ForegroundColor Red
            }
        }
    }
}

if (-not $removed) {
    Write-Host "INFO: No corrupted NVIDIA EDID_OVERRIDE was present." -ForegroundColor Yellow
}

Write-Host "`nPower cycling NVIDIA GPU to wake laptop OLED panel..." -ForegroundColor Cyan
try {
    $gpu = Get-PnpDevice -Class Display | Where-Object { $_.FriendlyName -like "*NVIDIA*" } | Select-Object -First 1
    if ($gpu) {
        Disable-PnpDevice -InstanceId $gpu.InstanceId -Confirm:$false -ErrorAction Stop
        Start-Sleep -Seconds 2
        Enable-PnpDevice -InstanceId $gpu.InstanceId -Confirm:$false -ErrorAction Stop
        Write-Host "SUCCESS: NVIDIA GPU reset complete!" -ForegroundColor Green
    }
} catch {
    Write-Host "WARNING: Driver restart error: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host "`nWaking internal panel and restoring display topology..." -ForegroundColor Cyan
Start-Sleep -Seconds 1
& DisplaySwitch.exe /extend
Write-Host "SUCCESS: Display topology reset to Extend mode." -ForegroundColor Green
Write-Host "`n[DONE] Laptop screen should now be ON!" -ForegroundColor Green
Start-Sleep -Seconds 3
