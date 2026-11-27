$report = @()
$report += "=== 1C EQUIPMENT CONFIG $(Get-Date) ==="

Get-ChildItem "C:\Program Files\1cv8" -Recurse -Include *.xml,*.ini,*.cfg -ErrorAction SilentlyContinue |
    Select-String -Pattern 'COM[0-9]|Scanner|Serial|Barcode|Port' -ErrorAction SilentlyContinue |
    Select-Object -First 40 | ForEach-Object {
        $report += "$($_.Path):$($_.LineNumber): $($_.Line.Trim())"
    }

$report += ""
$report += "=== LOGGED ON USERS ==="
cmd /c "query user" 2>&1 | ForEach-Object { $report += $_ }

$report += ""
$report += "=== Zebra/Symbol services ==="
Get-Service -ErrorAction SilentlyContinue | Where-Object {
    $_.DisplayName -match 'Zebra|Symbol|CoreScanner|123Scan|Scanner'
} | ForEach-Object {
    $report += "$($_.Name) | $($_.DisplayName) | Status=$($_.Status)"
}

$report += ""
$report += "=== Related processes ==="
Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.Name -match '1cv8|123Scan|CoreScanner|Scanner|Zebra|Symbol|Scan'
} | ForEach-Object {
    $owner = (Get-CimInstance Win32_Process -Filter "ProcessId=$($_.Id)" -ErrorAction SilentlyContinue).GetOwner()
    $user = if ($owner) { "$($owner.Domain)\$($owner.User)" } else { 'unknown' }
    $report += "$($_.Name) PID=$($_.Id) User=$user"
}

$report += ""
$report += "=== 1C conf folder ==="
Get-ChildItem "C:\Program Files\1cv8\conf" -ErrorAction SilentlyContinue | ForEach-Object {
    $report += $_.FullName
    if ($_.Extension -in '.xml','.cfg','.ini') {
        Get-Content $_.FullName -ErrorAction SilentlyContinue | Select-Object -First 50 | ForEach-Object { $report += "  $_" }
    }
}

$report += ""
$report += "=== ProgramData 1C ==="
Get-ChildItem "C:\ProgramData\1C" -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match 'scan|com|serial|equip|device|driver' -or $_.Extension -in '.xml','.ini','.cfg' } |
    Select-Object -First 30 | ForEach-Object { $report += $_.FullName }

$report += ""
$report += "=== Users profiles with 1C ==="
Get-ChildItem "C:\Users" -Directory -ErrorAction SilentlyContinue | ForEach-Object {
    $u = $_.Name
    $paths = @(
        "$($_.FullName)\AppData\Roaming\1C",
        "$($_.FullName)\AppData\Local\1C"
    )
    foreach ($p in $paths) {
        if (Test-Path $p) {
            $report += "User $u has $p"
            Get-ChildItem $p -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match 'scan|com|serial|equip|device' } |
                Select-Object -First 10 | ForEach-Object { $report += "  $($_.FullName)" }
        }
    }
}

$report += ""
$report += "=== CDC/Zebra INF ==="
Select-String -Path "C:\Windows\INF\oem*.inf" -Pattern "VID_05E0|Zebra|Symbol|CDC" -ErrorAction SilentlyContinue |
    Select-Object -First 20 | ForEach-Object { $report += "$($_.Path): $($_.Line.Trim())" }

$report | Out-File "C:\Temp\scanner_diag2_result.txt" -Encoding UTF8
