param(
    [string]$Path = "$env:LOCALAPPDATA\Unity\Editor\Editor.log",
    [string[]]$Markers = @("[PropsCond]", "[MultiCamSwitch]", "[Director.Props]"),
    [int]$Max = 40
)

if (-not (Test-Path $Path)) { "missing: $Path"; exit 1 }

$fs = New-Object System.IO.FileStream($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
$sr = New-Object System.IO.StreamReader($fs)

$found = @{}
foreach ($m in $Markers) { $found[$m] = New-Object System.Collections.Generic.List[string] }
$counts = @{}
foreach ($m in $Markers) { $counts[$m] = 0 }

while ($null -ne ($line = $sr.ReadLine())) {
    foreach ($m in $Markers) {
        if ($line.Contains($m) -and -not $line.StartsWith("Gallop.") -and -not $line.StartsWith("UnityEngine.")) {
            $counts[$m]++
            if ($found[$m].Count -lt $Max) { $found[$m].Add($line.Trim()) }
        }
    }
}
$sr.Close(); $fs.Close()

foreach ($m in $Markers) {
    "===== $m  (总命中 $($counts[$m])，显示前 $($found[$m].Count) 条) ====="
    if ($found[$m].Count -eq 0) { "  (无)" }
    foreach ($l in $found[$m]) { "  $l" }
    ""
}
