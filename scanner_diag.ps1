$out = "C:\Temp\scanner_diag_result.txt"
$ErrorActionPreference = 'SilentlyContinue'
New-Item -Path C:\Temp -ItemType Directory -Force | Out-Null

$report = @()
$report += "=== SCANNER DIAGNOSTIC $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ==="
$report += "Computer: $env:COMPUTERNAME"
$report += "User: $env:USERDOMAIN\$env:USERNAME"

$os = Get-CimInstance Win32_OperatingSystem
$report += "OS: $($os.Caption) Build $($os.BuildNumber) $($os.OSArchitecture)"
$report += ""

$report += "=== COM PORTS (Win32_SerialPort) ==="
Get-CimInstance Win32_SerialPort | ForEach-Object {
    $report += "$($_.DeviceID) | $($_.Name) | $($_.Description) | Status=$($_.Status)"
}
if (-not (Get-CimInstance Win32_SerialPort)) { $report += "(none found)" }

$report += ""
$report += "=== .NET SerialPort.GetPortNames() ==="
$ports = [System.IO.Ports.SerialPort]::GetPortNames()
if ($ports) { $report += ($ports -join ', ') } else { $report += "(none)" }

$report += ""
$report += "=== PnP: Symbol/Zebra/COM/Scanner/USB CDC ==="
Get-CimInstance Win32_PnPEntity | Where-Object {
    $_.Name -match 'Symbol|Zebra|COM\d|Serial|Scanner|Barcode|CDC|Handheld|LS2208|Motorola' -or
    $_.DeviceID -match 'VID_05E0|VID_0A5F'
} | Sort-Object Name | ForEach-Object {
    $report += "NAME=$($_.Name)"
    $report += "  Status=$($_.Status) Problem=$($_.ConfigManagerErrorCode)"
    $report += "  DeviceID=$($_.DeviceID)"
    $report += "  Manufacturer=$($_.Manufacturer)"
    $report += ""
}

$report += "=== USB Controllers with problems ==="
Get-CimInstance Win32_PnPEntity | Where-Object {
    $_.PNPClass -eq 'USB' -and $_.ConfigManagerErrorCode -ne 0
} | ForEach-Object {
    $report += "$($_.Name) Error=$($_.ConfigManagerErrorCode)"
}

$report += ""
$report += "=== Unknown / Error devices ==="
Get-CimInstance Win32_PnPEntity | Where-Object {
    $_.ConfigManagerErrorCode -ne 0 -or $_.Name -match 'Unknown|Error|Failed'
} | ForEach-Object {
    $report += "$($_.Name) | Error=$($_.ConfigManagerErrorCode) | $($_.DeviceID)"
}

$report += ""
$report += "=== Installed Zebra/Symbol software ==="
Get-ItemProperty HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\* |
    Where-Object { $_.DisplayName -match 'Zebra|Symbol|Motorola|CDC|Scanner' } |
    ForEach-Object { $report += "$($_.DisplayName) v$($_.DisplayVersion)" }
Get-ItemProperty HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\* |
    Where-Object { $_.DisplayName -match 'Zebra|Symbol|Motorola|CDC|Scanner' } |
    ForEach-Object { $report += "$($_.DisplayName) v$($_.DisplayVersion)" }

$report += ""
$report += "=== Driver INF files (Zebra/Symbol) ==="
Get-ChildItem C:\Windows\INF -Filter *.inf | Select-String -Pattern 'Zebra|Symbol|05E0|LS2208|CDC' -SimpleMatch:$false |
    Select-Object -First 20 | ForEach-Object { $report += $_.Path }

$report += ""
$report += "=== USB devices VID_05E0 (Symbol/Zebra) ==="
Get-CimInstance Win32_PnPEntity | Where-Object { $_.DeviceID -match 'VID_05E0' } | ForEach-Object {
    $report += "$($_.Name) | $($_.DeviceID) | Status=$($_.Status) Error=$($_.ConfigManagerErrorCode)"
}

$report += ""
$report += "=== 1C installations ==="
@(
    'HKLM:\SOFTWARE\1C\1Cv8',
    'HKLM:\SOFTWARE\WOW6432Node\1C\1Cv8'
) | ForEach-Object {
    if (Test-Path $_) {
        Get-ChildItem $_ -ErrorAction SilentlyContinue | ForEach-Object {
            $report += "1C: $($_.PSChildName) -> $($_.Property)"
        }
    }
}
Get-ChildItem 'C:\Program Files\1cv8' -ErrorAction SilentlyContinue | ForEach-Object { $report += "1C folder: $($_.FullName)" }
Get-ChildItem 'C:\Program Files (x86)\1cv8' -ErrorAction SilentlyContinue | ForEach-Object { $report += "1C folder: $($_.FullName)" }

$report += ""
$report += "=== END ==="
$report | Out-File -FilePath $out -Encoding UTF8
