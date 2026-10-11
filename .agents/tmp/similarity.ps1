param(
    [string]$ExampleRoot = "C:\Users\JuziD\proj\umaviewer\example\Scripts",
    [string]$WorkspaceRoot = "C:\Users\JuziD\proj\umaviewer\UmaViewer\Assets\Scripts",
    [string]$Filter = "umamusume",
    [int]$MinLen = 25,
    [double]$TopN = 3
)

function Get-LineSet([string]$path) {
    $set = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($line in [System.IO.File]::ReadAllLines($path)) {
        $t = $line.Trim()
        if ($t.Length -lt $MinLen) { continue }
        # collapse whitespace so formatting-only differences do not count
        $t = ($t -replace '\s+', ' ')
        [void]$set.Add($t)
    }
    return $set
}

$exFiles = Get-ChildItem -Recurse -File (Join-Path $ExampleRoot $Filter) -Include *.cs -ErrorAction SilentlyContinue
$wsFiles = Get-ChildItem -Recurse -File (Join-Path $WorkspaceRoot $Filter) -Include *.cs -ErrorAction SilentlyContinue

Write-Output "indexing example files: $($exFiles.Count)"
$exMap = @{}
foreach ($f in $exFiles) {
    $rel = $f.FullName.Substring($ExampleRoot.Length + 1)
    $exMap[$rel] = Get-LineSet $f.FullName
}

$results = @()
foreach ($f in $wsFiles) {
    $rel = $f.FullName.Substring($WorkspaceRoot.Length + 1)
    $wsLines = Get-LineSet $f.FullName
    if ($wsLines.Count -eq 0) { continue }

    $best = @()
    foreach ($kv in $exMap.GetEnumerator()) {
        $exLines = $kv.Value
        if ($exLines.Count -eq 0) { continue }
        $inter = 0
        foreach ($l in $wsLines) { if ($exLines.Contains($l)) { $inter++ } }
        if ($inter -eq 0) { continue }
        $cov = [double]$inter / [double]$wsLines.Count      # how much of the workspace file exists verbatim in the example file
        $jac = [double]$inter / [double]($wsLines.Count + $exLines.Count - $inter)
        $best += [pscustomobject]@{ Ex = $kv.Key; Inter = $inter; Cov = $cov; Jac = $jac; ExLines = $exLines.Count }
    }
    if ($best.Count -eq 0) { continue }
    $best = $best | Sort-Object -Property Cov -Descending | Select-Object -First $TopN
    $results += [pscustomobject]@{
        WsFile   = $rel
        WsLines  = $wsLines.Count
        Top1     = $best[0].Ex
        Cov1     = [math]::Round($best[0].Cov * 100, 1)
        Jac1     = [math]::Round($best[0].Jac * 100, 1)
        Top2     = if ($best.Count -gt 1) { $best[1].Ex } else { "" }
        Cov2     = if ($best.Count -gt 1) { [math]::Round($best[1].Cov * 100, 1) } else { 0 }
    }
}

$results = $results | Sort-Object -Property Cov1 -Descending
$results | Format-Table -AutoSize | Out-String -Width 240 | Write-Output

Write-Output ""
Write-Output "=== distribution of verbatim-coverage of workspace files by example files (Cov1) ==="
$buckets = @{ ">=80%" = 0; "60-80%" = 0; "40-60%" = 0; "20-40%" = 0; "<20%" = 0 }
foreach ($r in $results) {
    $c = $r.Cov1
    if ($c -ge 80) { $buckets[">=80%"]++ }
    elseif ($c -ge 60) { $buckets["60-80%"]++ }
    elseif ($c -ge 40) { $buckets["40-60%"]++ }
    elseif ($c -ge 20) { $buckets["20-40%"]++ }
    else { $buckets["<20%"]++ }
}
$buckets.GetEnumerator() | Sort-Object Name | Format-Table -AutoSize | Out-String -Width 80 | Write-Output
