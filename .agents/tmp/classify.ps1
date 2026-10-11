param(
    [string]$WorkspaceRoot = "C:\Users\JuziD\proj\umaviewer\UmaViewer\Assets\Scripts",
    [string]$ExampleRoot = "C:\Users\JuziD\proj\umaviewer\example\Scripts",
    [string]$Filter = "umamusume\Gallop\Live",
    [int]$MinLen = 25
)

function Get-LineSet([string]$path) {
    $set = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($line in [System.IO.File]::ReadAllLines($path)) {
        $t = $line.Trim()
        if ($t.Length -lt $MinLen) { continue }
        [void]$set.Add(($t -replace '\s+', ' '))
    }
    return $set
}

$exFiles = Get-ChildItem -Recurse -File (Join-Path $ExampleRoot $Filter) -Include *.cs -ErrorAction SilentlyContinue
$exMap = @{}
foreach ($f in $exFiles) { $exMap[$f.FullName.Substring($ExampleRoot.Length + 1)] = Get-LineSet $f.FullName }

$rows = @()
foreach ($f in (Get-ChildItem -Recurse -File (Join-Path $WorkspaceRoot $Filter) -Include *.cs)) {
    $rel = $f.FullName.Substring($WorkspaceRoot.Length + 1)
    $ws = Get-LineSet $f.FullName
    if ($ws.Count -eq 0) { continue }
    $bestCov = 0.0; $bestName = ""
    foreach ($kv in $exMap.GetEnumerator()) {
        if ($kv.Value.Count -eq 0) { continue }
        $inter = 0
        foreach ($l in $ws) { if ($kv.Value.Contains($l)) { $inter++ } }
        $cov = [double]$inter / [double]$ws.Count
        if ($cov -gt $bestCov) { $bestCov = $cov; $bestName = $kv.Key }
    }
    if ($bestCov -lt 0.80) { continue }

    $text = [System.IO.File]::ReadAllText($f.FullName)
    $lines = [System.IO.File]::ReadAllLines($f.FullName)
    $methodLines = ($lines | Where-Object { $_ -match '^\s*(public|private|protected|internal|static)\s[^;=]*\([^;]*\)\s*$' }).Count
    $flowLines = ($lines | Where-Object { $_ -match '^\s*(if|for|foreach|while|switch|try|return|else)\b' }).Count
    $fieldLines = ($lines | Where-Object { $_ -match '^\s*(public|private|protected|internal|static|\[)' -and $_ -match ';\s*$' }).Count
    $rows += [pscustomobject]@{
        File = $rel
        Cov = [math]::Round($bestCov * 100, 1)
        Lines = $lines.Count
        Methods = $methodLines
        Flow = $flowLines
        Fields = $fieldLines
        Kind = if ($flowLines -ge 8) { "BEHAVIOR" } elseif ($flowLines -ge 3) { "MIXED" } else { "DATA-ONLY" }
        Match = $bestName
    }
}

$rows | Sort-Object Kind, @{Expression="Cov";Descending=$true} | Format-Table -AutoSize | Out-String -Width 220 | Write-Output
Write-Output "=== summary by kind ==="
$rows | Group-Object Kind | ForEach-Object { "$($_.Name): $($_.Count) files" }
