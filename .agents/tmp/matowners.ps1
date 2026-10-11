param(
    [string]$Root = "C:\Users\JuziD\proj\umaviewer\UmaViewer\Assets\Scripts",
    [string]$Filter = "umamusume\Gallop\Live",
    [string]$Pattern = '\.materials\b|\.material\b|new Material\(|SetPropertyBlock|GetComponentsInChildren'
)

$files = Get-ChildItem -Recurse -File (Join-Path $Root $Filter) -Include *.cs
foreach ($f in $files) {
    $lines = [System.IO.File]::ReadAllLines($f.FullName)
    $rel = $f.FullName.Substring($Root.Length + 1)
    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($lines[$i] -notmatch $Pattern) { continue }
        # walk backwards for nearest method-looking signature
        $owner = "<top-level>"
        for ($j = $i; $j -ge 0; $j--) {
            if ($lines[$j] -match '^\s{4,10}(public|private|protected|internal|static)[^;={]*\([^;]*\)\s*$') {
                $owner = $lines[$j].Trim()
                break
            }
            if ($lines[$j] -match '^\s{8}(public|private|protected|internal)[^;={]*\([^;]*\)\s*$') {
                $owner = $lines[$j].Trim(); break
            }
        }
        "{0}:{1}  [{2}]  {3}" -f $rel, ($i + 1), $owner, $lines[$i].Trim()
    }
}
