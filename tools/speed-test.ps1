# Measures how fast a folder can be written to and read from. Run it once for the Time Capsule Drive letter and
# once for the same Time Capsule opened over SMB, on the same computer, and compare the two "RESULT" lines.
#
#   powershell -ExecutionPolicy Bypass -File speed-test.ps1 -Path "T:\tmp" -Label AFP
#   powershell -ExecutionPolicy Bypass -File speed-test.ps1 -Path "\\192.168.1.5\Time Capsule\tmp" -Label SMB
#
# It creates a folder "_speedtest" inside -Path and removes it at the end. Windows' own caching is bypassed
# (write-through, unbuffered reads), so the numbers show the network path and not the memory of this PC.
param([Parameter(Mandatory = $true)][string]$Path, [string]$Label = 'test', [int]$SizeMB = 400, [int]$SmallFiles = 200)
$ErrorActionPreference = 'Stop'
$dir = Join-Path $Path '_speedtest'
function Remove-TestFolder {
    # Some servers (SMB1) report a folder as "not empty" for a moment after its files were deleted: be patient.
    for ($try = 0; $try -lt 10 -and (Test-Path -LiteralPath $dir); $try++) {
        Get-ChildItem -LiteralPath $dir -Force -ErrorAction SilentlyContinue | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
        Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $dir) { Start-Sleep -Milliseconds 700 }
    }
}
Remove-TestFolder
New-Item -ItemType Directory $dir | Out-Null
$big = Join-Path $dir 'big.bin'
$buf = New-Object byte[] (1MB); (New-Object Random 1).NextBytes($buf)
$WriteThrough = [IO.FileOptions]0x80000000; $NoBuffering = [IO.FileOptions]0x20000000

Write-Host "[$Label] $Path"
# 1. one large file, written in 1 MB pieces
Write-Host "  1/5 writing one $SizeMB MB file..." -NoNewline
$sw = [Diagnostics.Stopwatch]::StartNew()
$fs = New-Object IO.FileStream($big, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None, 4096, $WriteThrough)
for ($i = 0; $i -lt $SizeMB; $i++) { $fs.Write($buf, 0, $buf.Length) }
$fs.Flush($true); $fs.Close(); $sw.Stop()
$write = $SizeMB / $sw.Elapsed.TotalSeconds
Write-Host (" {0:N1} MB/s" -f $write)

# 2. the same file read back
Write-Host "  2/5 reading it back..." -NoNewline
$cached = ''
try { $fs = New-Object IO.FileStream($big, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read, 4096, $NoBuffering) }
catch { $fs = [IO.File]::OpenRead($big); $cached = ' (Windows cache could not be bypassed)' }
$sw = [Diagnostics.Stopwatch]::StartNew(); [long]$got = 0
try { while (($n = $fs.Read($buf, 0, $buf.Length)) -gt 0) { $got += $n } }
catch { $fs.Close(); $fs = [IO.File]::OpenRead($big); $got = 0; $cached = ' (Windows cache could not be bypassed)'; $sw.Restart(); while (($n = $fs.Read($buf, 0, $buf.Length)) -gt 0) { $got += $n } }
$fs.Close(); $sw.Stop()
$read = $got / 1MB / $sw.Elapsed.TotalSeconds
Write-Host (" {0:N1} MB/s{1}" -f $read, $cached)

# 3. many small files (10 KB each): create, read back, delete
$small = New-Object byte[] (10KB); (New-Object Random 2).NextBytes($small)
Write-Host "  3/5 creating $SmallFiles small files..." -NoNewline
$sw = [Diagnostics.Stopwatch]::StartNew(); for ($i = 0; $i -lt $SmallFiles; $i++) { [IO.File]::WriteAllBytes((Join-Path $dir "s$i.bin"), $small) }; $sw.Stop(); $create = $SmallFiles / $sw.Elapsed.TotalSeconds
Write-Host (" {0:N0} files/s" -f $create)
Write-Host "  4/5 reading them back..." -NoNewline
$sw = [Diagnostics.Stopwatch]::StartNew(); for ($i = 0; $i -lt $SmallFiles; $i++) { [void][IO.File]::ReadAllBytes((Join-Path $dir "s$i.bin")) }; $sw.Stop(); $readSmall = $SmallFiles / $sw.Elapsed.TotalSeconds
Write-Host (" {0:N0} files/s" -f $readSmall)
Write-Host "  5/5 deleting them..." -NoNewline
$sw = [Diagnostics.Stopwatch]::StartNew(); for ($i = 0; $i -lt $SmallFiles; $i++) { Remove-Item -LiteralPath (Join-Path $dir "s$i.bin") -Force -ErrorAction SilentlyContinue }; $sw.Stop(); $delete = $SmallFiles / $sw.Elapsed.TotalSeconds
Write-Host (" {0:N0} files/s" -f $delete)

"RESULT {0}: write {1:N1} MB/s | read {2:N1} MB/s{3} | small files: create {4:N0}/s, read {5:N0}/s, delete {6:N0}/s  [{7} MB, {8} files, {9}]" -f `
    $Label, $write, $read, $cached, $create, $readSmall, $delete, $SizeMB, $SmallFiles, $Path
Remove-TestFolder
if (Test-Path -LiteralPath $dir) { "note: could not remove $dir - delete it by hand" }
