param(
    [string]$Path = "$env:LOCALAPPDATA\Unity\Editor\Editor.log",
    [string]$Marker = "[LiveProfiler]",
    [int]$Tail = 20,
    [int]$KeepBlocks = 2
)

if (-not (Test-Path $Path)) { "missing: $Path"; exit 1 }

$fs = New-Object System.IO.FileStream($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
$sr = New-Object System.IO.StreamReader($fs)

$blocks = New-Object System.Collections.Generic.List[string]
$current = $null
$remain = 0
$lineNo = 0
$hitLines = New-Object System.Collections.Generic.List[int]

while ($null -ne ($line = $sr.ReadLine())) {
    $lineNo++
    if ($line.Contains($Marker)) {
        if ($current) { $blocks.Add($current) }
        if ($blocks.Count -gt $KeepBlocks) { $blocks.RemoveAt(0) }
        $current = "----- line $lineNo -----`n$line"
        $remain = $Tail
        $hitLines.Add($lineNo)
        continue
    }
    if ($remain -gt 0 -and $current) {
        $current = "$current`n$line"
        $remain--
    }
}
if ($current) { $blocks.Add($current) }
$sr.Close(); $fs.Close()

"total marker hits: $($hitLines.Count)"
if ($hitLines.Count -gt 0) { "hit lines: " + (($hitLines | Select-Object -Last 12) -join ', ') }
""
foreach ($b in $blocks) { $b; "" }
