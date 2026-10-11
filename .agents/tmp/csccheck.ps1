param(
    [string]$Project = "C:\Users\JuziD\proj\umaviewer\UmaViewer\umamusume.csproj",
    [string]$WorkDir = "C:\Users\JuziD\proj\umaviewer\UmaViewer",
    [string]$OutDir  = "C:\Users\JuziD\proj\umaviewer\UmaViewer\Temp\csc_check"
)

$ErrorActionPreference = "Stop"

$csprojDir = Split-Path -Parent $Project
$text = [System.IO.File]::ReadAllText($Project)

# ---- defines (first Debug|AnyCPU block) ----
$defines = $null
foreach ($m in [regex]::Matches($text, '<DefineConstants>([^<]+)</DefineConstants>')) {
    $defines = $m.Groups[1].Value
    break
}

# ---- compile items ----
$compile = @()
foreach ($m in [regex]::Matches($text, '<Compile Include="([^"]+)"')) {
    $compile += (Join-Path $csprojDir $m.Groups[1].Value)
}

# Unity 只在重新导入时才刷新 csproj，新加的文件不会在里面。
# 这里把 umamusume/Gallop/Live 下尚未登记的 .cs 自动收进来（其它 asmdef 目录不动，避免重复类型）。
$liveRoot = Join-Path $csprojDir "Assets\Scripts\umamusume\Gallop\Live"
if (Test-Path $liveRoot) {
    $known = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($c in $compile) { [void]$known.Add($c) }
    foreach ($f in (Get-ChildItem -Recurse -File $liveRoot -Include *.cs)) {
        if (-not $known.Contains($f.FullName)) {
            $compile += $f.FullName
            Write-Output "  (+new) $($f.FullName.Substring($csprojDir.Length + 1))"
        }
    }
}

# ---- reference hint paths ----
$refs = @()
foreach ($m in [regex]::Matches($text, '<HintPath>([^<]+)</HintPath>')) {
    $refs += $m.Groups[1].Value
}

# ---- project references -> Library/ScriptAssemblies outputs ----
$scriptAsm = Join-Path $WorkDir "Library\ScriptAssemblies"
foreach ($m in [regex]::Matches($text, '<ProjectReference Include="([^"]+)"')) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($m.Groups[1].Value)
    $dll = Join-Path $scriptAsm "$name.dll"
    if (Test-Path $dll) { $refs += $dll }
}

# ---- netstandard 2.1 reference assemblies ----
$nsRef = "C:\Program Files\Unity\Hub\Editor\2022.3.62f3\Editor\Data\NetStandard\ref\2.1.0"
if (Test-Path $nsRef) {
    $refs += (Get-ChildItem $nsRef -Filter *.dll | ForEach-Object { $_.FullName })
} else {
    $refs += (Get-ChildItem "C:\Program Files\dotnet\packs\NETStandard.Library.Ref\*\ref\netstandard2.1" -Filter *.dll -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
}

$refs = $refs | Where-Object { $_ -and (Test-Path $_) } | Sort-Object -Unique

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$rsp = Join-Path $OutDir "check.rsp"

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("-noconfig")
$lines.Add("-nostdlib+")
$lines.Add("-target:library")
$lines.Add("-langversion:9.0")
$lines.Add("-nowarn:0169,CS0169,CS0649,CS0414,CS0108,CS0067,CS0219,CS1701,CS0168")
$lines.Add("-unsafe-")
if ($defines) { $lines.Add("-define:$defines") }
$lines.Add("-out:`"$(Join-Path $OutDir 'umamusume_check.dll')`"")
foreach ($r in $refs) { $lines.Add("-r:`"$r`"") }
foreach ($c in $compile) { $lines.Add("`"$c`"") }

[System.IO.File]::WriteAllLines($rsp, $lines)

"compile items : $($compile.Count)"
"references    : $($refs.Count)"
"rsp           : $rsp"

$csc = "C:\Program Files\Unity\Hub\Editor\2022.3.62f3\Editor\Data\DotNetSdkRoslyn\csc.dll"
& dotnet $csc "@$rsp" 2>&1 | Select-Object -First 80
"exit=$LASTEXITCODE"
