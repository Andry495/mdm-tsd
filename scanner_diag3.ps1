$report = @()
$report += "=== COM3 + REBOOT + CONFLICT CHECK $(Get-Date) ==="

# Pending reboot?
$pending = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired',
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending',
    'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\PendingFileRenameOperations'
)
$report += "=== Pending reboot indicators ==="
foreach ($k in $pending) {
    if (Test-Path $k) { $report += "PENDING: $k" } else { $report += "OK: $k absent" }
}

# COM3 open test
$report += ""
$report += "=== COM3 open test ==="
try {
    Add-Type -AssemblyName System.IO.Ports
    $sp = New-Object System.IO.Ports.SerialPort COM3,9600,None,8,One
    $sp.ReadTimeout = 2000
    $sp.Open()
    $report += "COM3 OPEN: SUCCESS (port is available)"
    $sp.Close()
    $sp.Dispose()
} catch {
    $report += "COM3 OPEN: FAILED - $($_.Exception.Message)"
}

# CoreScanner holding device?
$report += ""
$report += "=== CoreScanner service details ==="
Get-CimInstance Win32_Service -Filter "Name='CoreScanner'" | ForEach-Object {
    $report += "State=$($_.State) StartMode=$($_.StartMode) Path=$($_.PathName)"
}

# All PnP on COM3
$report += ""
$report += "=== COM3 device full info ==="
Get-CimInstance Win32_PnPEntity | Where-Object { $_.Name -like '*COM3*' -or $_.DeviceID -like '*PID_1701*' } | ForEach-Object {
    $report += "Name=$($_.Name)"
    $report += "PNPClass=$($_.PNPClass)"
    $report += "Service=$($_.Service)"
    $report += "DriverVersion=$((Get-CimInstance Win32_PnPSignedDriver -Filter \"DeviceID='$($_.DeviceID)'\" -ErrorAction SilentlyContinue).DriverVersion)"
    $report += "DriverProvider=$((Get-CimInstance Win32_PnPSignedDriver -Filter \"DeviceID='$($_.DeviceID)'\" -ErrorAction SilentlyContinue).DriverProviderName)"
}

# resh 1C files
$report += ""
$report += "=== resh 1C config ==="
$paths = @(
    'C:\Users\resh\AppData\Roaming\1C\1CEStart\ibases.v8i',
    'C:\Users\resh\AppData\Roaming\1C\1cv8\ExtCompT\registry.xml'
)
foreach ($p in $paths) {
    if (Test-Path $p) {
        $report += "--- $p ---"
        Get-Content $p | ForEach-Object { $report += $_ }
    } else {
        $report += "MISSING: $p"
    }
}

# Recent 1C logs for resh
$report += ""
$report += "=== resh 1C logs (recent) ==="
Get-ChildItem 'C:\Users\resh\AppData\Local\1C' -Recurse -Include *.log -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 5 | ForEach-Object {
        $report += "$($_.FullName) ($($_.LastWriteTime))"
        Select-String -Path $_.FullName -Pattern 'COM|scan|Scan|Serial|оборуд|ошиб' -ErrorAction SilentlyContinue |
            Select-Object -Last 10 | ForEach-Object { $report += "  $($_.Line.Trim())" }
    }

# Desktop shortcuts / programs
$report += ""
$report += "=== Desktop shortcuts resh ==="
Get-ChildItem 'C:\Users\resh\Desktop','C:\Users\Public\Desktop' -Filter *.lnk -ErrorAction SilentlyContinue |
    ForEach-Object { $report += $_.FullName }

$report | Out-File 'C:\Temp\scanner_diag3_result.txt' -Encoding UTF8
