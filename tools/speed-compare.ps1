# Compares the speed of the Time Capsule Drive (AFP) with the same Time Capsule opened over Windows' built-in SMB.
# Runs both tests several times, one after the other, and prints the averages. Nothing to type or confirm.
#
#   powershell -ExecutionPolicy Bypass -File speed-compare.ps1
#
# The drive letter, the address of the Time Capsule and the disk name are taken from the installed program;
# override them with -Drive, -Smb if needed. A folder "_speedtest" is created in "tmp" on the disk and removed.
param(
    [string]$Drive = '',                 # e.g. T:   (default: the installed Time Capsule Drive)
    [string]$Smb = '',                   # e.g. \\192.168.1.5\Time Capsule   (default: worked out from the drive)
    [int]$Runs = 3, [int]$SizeMB = 400, [int]$SmallFiles = 200
)
$ErrorActionPreference = 'Stop'

# ---- find the drive and the SMB path
$statusFile = Join-Path $env:ProgramData 'TimeCapsuleDrive\status.txt'
$status = @{}
if (Test-Path $statusFile) { Get-Content $statusFile | ForEach-Object { $i = $_.IndexOf('='); if ($i -gt 0) { $status[$_.Substring(0, $i)] = $_.Substring($i + 1) } } }
if (-not $Drive) { $Drive = $status['drive'] }
if (-not $Drive) { $d = [IO.DriveInfo]::GetDrives() | Where-Object { $_.IsReady -and $_.DriveFormat -eq 'AFP' } | Select-Object -First 1; if ($d) { $Drive = $d.Name.Substring(0, 2) } }
if (-not $Drive) { throw 'Time Capsule Drive not found. Pass its letter: -Drive T:' }
$Drive = $Drive.TrimEnd('\')
if (-not $Smb) {
    $label = (New-Object IO.DriveInfo $Drive.Substring(0, 1)).VolumeLabel
    if (-not $status['host'] -or -not $label) { throw 'Cannot work out the SMB path. Pass it: -Smb "\\192.168.1.5\Disk name"' }
    $Smb = "\\$($status['host'])\$label"
}
$targets = [ordered]@{ 'AFP' = "$Drive\tmp"; 'SMB' = (Join-Path $Smb 'tmp') }

$WriteThrough = [IO.FileOptions]0x80000000; $NoBuffering = [IO.FileOptions]0x20000000
$buf = New-Object byte[] (1MB); (New-Object Random 1).NextBytes($buf)
$small = New-Object byte[] (10KB); (New-Object Random 2).NextBytes($small)

function Remove-TestFolder($dir) {
    # Some servers (SMB1) report a folder as "not empty" for a moment after its files were deleted: be patient.
    for ($try = 0; $try -lt 10 -and (Test-Path -LiteralPath $dir); $try++) {
        Get-ChildItem -LiteralPath $dir -Force -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
        Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $dir) { Start-Sleep -Milliseconds 700 }
    }
}

# One pass over one path. Windows' own caching is bypassed (write-through, unbuffered reads).
function Measure-Path($path) {
    if (-not (Test-Path -LiteralPath $path)) { New-Item -ItemType Directory $path | Out-Null }
    $dir = Join-Path $path '_speedtest'; Remove-TestFolder $dir; New-Item -ItemType Directory $dir | Out-Null
    $big = Join-Path $dir 'big.bin'; $r = [ordered]@{}

    Write-Host "    writing $SizeMB MB..." -NoNewline
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $fs = New-Object IO.FileStream($big, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None, 4096, $WriteThrough)
    for ($i = 0; $i -lt $SizeMB; $i++) { $fs.Write($buf, 0, $buf.Length) }
    $fs.Flush($true); $fs.Close(); $sw.Stop(); $r.write = $SizeMB / $sw.Elapsed.TotalSeconds
    Write-Host (" {0:N1} MB/s" -f $r.write)

    Write-Host "    reading it back..." -NoNewline
    try { $fs = New-Object IO.FileStream($big, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read, 4096, $NoBuffering) } catch { $fs = [IO.File]::OpenRead($big) }
    $sw = [Diagnostics.Stopwatch]::StartNew(); [long]$got = 0
    try { while (($n = $fs.Read($buf, 0, $buf.Length)) -gt 0) { $got += $n } }
    catch { $fs.Close(); $fs = [IO.File]::OpenRead($big); $got = 0; $sw.Restart(); while (($n = $fs.Read($buf, 0, $buf.Length)) -gt 0) { $got += $n } }
    $fs.Close(); $sw.Stop(); $r.read = $got / 1MB / $sw.Elapsed.TotalSeconds
    Write-Host (" {0:N1} MB/s" -f $r.read)

    Write-Host "    $SmallFiles small files: create..." -NoNewline
    $sw = [Diagnostics.Stopwatch]::StartNew(); for ($i = 0; $i -lt $SmallFiles; $i++) { [IO.File]::WriteAllBytes((Join-Path $dir "s$i.bin"), $small) }; $sw.Stop(); $r.create = $SmallFiles / $sw.Elapsed.TotalSeconds
    Write-Host (" {0:N0}/s, read..." -f $r.create) -NoNewline
    $sw = [Diagnostics.Stopwatch]::StartNew(); for ($i = 0; $i -lt $SmallFiles; $i++) { [void][IO.File]::ReadAllBytes((Join-Path $dir "s$i.bin")) }; $sw.Stop(); $r.readSmall = $SmallFiles / $sw.Elapsed.TotalSeconds
    Write-Host (" {0:N0}/s, delete..." -f $r.readSmall) -NoNewline
    $sw = [Diagnostics.Stopwatch]::StartNew(); for ($i = 0; $i -lt $SmallFiles; $i++) { Remove-Item -LiteralPath (Join-Path $dir "s$i.bin") -Force -ErrorAction SilentlyContinue }; $sw.Stop(); $r.delete = $SmallFiles / $sw.Elapsed.TotalSeconds
    Write-Host (" {0:N0}/s" -f $r.delete)
    Remove-TestFolder $dir
    return $r
}

Write-Host "Time Capsule speed comparison: $Runs run(s) each, $SizeMB MB file and $SmallFiles small files"
foreach ($k in $targets.Keys) { Write-Host ("  {0}: {1}" -f $k, $targets[$k]) }
$all = @{ 'AFP' = @(); 'SMB' = @() }; $failed = @{}
for ($run = 1; $run -le $Runs; $run++) {
    foreach ($k in $targets.Keys) {
        if ($failed[$k]) { continue }
        Write-Host "[$run/$Runs] $k"
        try { $all[$k] += , (Measure-Path $targets[$k]) }
        catch { $failed[$k] = $_.Exception.Message; Write-Host "    FAILED: $($failed[$k])" }
    }
}

# ---- summary
$rows = @(@('write', 'Large file, write (MB/s)', 1), @('read', 'Large file, read (MB/s)', 1), @('create', 'Small files, create (per s)', 0),
    @('readSmall', 'Small files, read (per s)', 0), @('delete', 'Small files, delete (per s)', 0))
function Avg($k, $key) { if ($all[$k].Count -eq 0) { return $null }; ($all[$k] | ForEach-Object { $_[$key] } | Measure-Object -Average).Average }
$lines = @('', '==================== RESULT ====================',
    ("{0,-30} {1,10} {2,10}   {3}" -f '', 'SMB', 'AFP', 'AFP vs SMB'))
foreach ($row in $rows) {
    $s = Avg 'SMB' $row[0]; $a = Avg 'AFP' $row[0]; $f = "N$($row[2])"
    $ratio = if ($s -and $a) { if ($a -ge $s) { "{0:N1}x faster" -f ($a / $s) } else { "{0:N0}% slower" -f ((1 - $a / $s) * 100) } } else { '' }
    $lines += ("{0,-30} {1,10} {2,10}   {3}" -f $row[1], $(if ($s) { $s.ToString($f) } else { 'n/a' }), $(if ($a) { $a.ToString($f) } else { 'n/a' }), $ratio)
}
foreach ($k in 'SMB', 'AFP') {
    $lines += ("{0} runs: {1}" -f $k, (($all[$k] | ForEach-Object { "{0:N1}/{1:N1}/{2:N0}/{3:N0}/{4:N0}" -f $_.write, $_.read, $_.create, $_.readSmall, $_.delete }) -join '  '))
    if ($failed[$k]) { $lines += "$k FAILED: $($failed[$k])" }
}
$lines += ("averages of {0} run(s); {1} MB file, {2} small files; {3}; AFP={4}; SMB={5}" -f $Runs, $SizeMB, $SmallFiles, (Get-Date -Format 'yyyy-MM-dd HH:mm'), $targets['AFP'], $targets['SMB'])
$lines += '================================================'
$lines | ForEach-Object { Write-Host $_ }
$out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'timecapsule-speed-result.txt'
try { $lines | Set-Content -LiteralPath $out -Encoding UTF8; Write-Host "saved to $out" } catch { }
