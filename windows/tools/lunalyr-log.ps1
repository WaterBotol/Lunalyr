<#
.SYNOPSIS
    Reads Lunalyr's log files, with filters and an optional live tail.

.DESCRIPTION
    Lunalyr writes one UTF-8 file per day to %LOCALAPPDATA%\Lunalyr\Logs (lunalyr-yyyy-MM-dd.log, then
    lunalyr-yyyy-MM-dd.1.log and so on when a file reaches 10 MB) and keeps the last 7 days.
    Each entry is "timestamp<TAB>LEVEL<TAB>category<TAB>message"; extra lines (stack traces) follow on lines that
    start with a TAB and are shown or hidden together with their entry. See docs/LOGGING.md.

.PARAMETER Category
    Only these categories: app, queue, sync, lyrics, player, render, settings.

.PARAMETER Level
    Minimum level: debug (everything, the default), info, warn or error.

.PARAMETER Since
    Only entries newer than this: a duration such as 30s, 10m, 2h or 1d, or a date/time such as "2026-11-12 09:30".

.PARAMETER Follow
    Keep printing new entries as they are written (like tail -f), across day and size rollovers. Ctrl+C stops.

.PARAMETER Tail
    Only the last N matching entries (before following, if -Follow is given).

.PARAMETER Path
    The log folder. Defaults to %LOCALAPPDATA%\Lunalyr\Logs.

.EXAMPLE
    tools\lunalyr-log.ps1 -Category sync -Since 10m

.EXAMPLE
    tools\lunalyr-log.ps1 -Level warn -Follow

.EXAMPLE
    tools\lunalyr-log.ps1 -Category queue,sync -Tail 50
#>
[CmdletBinding()]
param(
    [ValidateSet('app', 'queue', 'sync', 'lyrics', 'player', 'render', 'settings')]
    [string[]]$Category,

    [ValidateSet('debug', 'info', 'warn', 'error')]
    [string]$Level = 'debug',

    [string]$Since,

    [switch]$Follow,

    [ValidateRange(0, [int]::MaxValue)]
    [int]$Tail = 0,

    [string]$Path = (Join-Path $env:LOCALAPPDATA 'Lunalyr\Logs'),

    [ValidateRange(50, 60000)]
    [int]$PollMilliseconds = 500
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'

$levelRank = @{ 'DEBUG' = 0; 'INFO' = 1; 'WARN' = 2; 'ERROR' = 3 }
$minRank = $levelRank[$Level.ToUpperInvariant()]
$categories = @{}
if ($Category) { foreach ($c in $Category) { $categories[$c.ToLowerInvariant()] = $true } }
$invariant = [Globalization.CultureInfo]::InvariantCulture
$utf8 = New-Object Text.UTF8Encoding($false)
$headerPattern = [regex]'^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}[+-]\d{2}:\d{2})\t([A-Z]+)\t([a-z]+)\t'
$filePattern = [regex]'^lunalyr-(\d{4}-\d{2}-\d{2})(?:\.(\d{1,6}))?\.log$'

$sinceTime = $null
if ($Since) {
    $m = [regex]::Match($Since, '^\s*(\d+)\s*(s|m|h|d)\s*$')
    if ($m.Success) {
        $n = [double]$m.Groups[1].Value
        switch ($m.Groups[2].Value) {
            's' { $span = [TimeSpan]::FromSeconds($n) }
            'm' { $span = [TimeSpan]::FromMinutes($n) }
            'h' { $span = [TimeSpan]::FromHours($n) }
            'd' { $span = [TimeSpan]::FromDays($n) }
        }
        $sinceTime = [DateTimeOffset]::Now - $span
    }
    else {
        $parsed = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse($Since, [Globalization.CultureInfo]::CurrentCulture, [Globalization.DateTimeStyles]::AssumeLocal, [ref]$parsed)) {
            throw "Can't read -Since '$Since'. Use a duration like 10m, 2h, 1d or a date/time."
        }
        $sinceTime = $parsed
    }
}

function Get-LogFiles {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return @() }
    $files = foreach ($f in Get-ChildItem -LiteralPath $Path -Filter 'lunalyr-*.log' -File) {
        $m = $filePattern.Match($f.Name)
        if (-not $m.Success) { continue }
        $index = 0
        if ($m.Groups[2].Success) { $index = [int]$m.Groups[2].Value }
        [pscustomobject]@{ Day = $m.Groups[1].Value; Index = $index; FullName = $f.FullName }
    }
    if ($sinceTime) {
        $sinceDay = $sinceTime.ToLocalTime().ToString('yyyy-MM-dd', $invariant)
        $files = $files | Where-Object { [string]::CompareOrdinal($_.Day, $sinceDay) -ge 0 }
    }
    return @($files | Sort-Object Day, Index)
}

# Reads the complete lines appended to a file since $Offset (a byte position). A partly written last line is
# left for the next read, so multi-byte characters and lines are never split.
function Read-NewLines([string]$File, [long]$Offset) {
    $stream = $null
    try {
        $stream = New-Object IO.FileStream($File, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        if ($stream.Length -lt $Offset) { $Offset = 0 } # Replaced or truncated.
        $count = [int]($stream.Length - $Offset)
        if ($count -le 0) { return @{ Offset = $Offset; Lines = @() } }
        $bytes = New-Object byte[] $count
        [void]$stream.Seek($Offset, [IO.SeekOrigin]::Begin)
        $read = 0
        while ($read -lt $count) {
            $n = $stream.Read($bytes, $read, $count - $read)
            if ($n -le 0) { break }
            $read += $n
        }
        $end = [Array]::LastIndexOf($bytes, [byte]10, $read - 1)
        if ($end -lt 0) { return @{ Offset = $Offset; Lines = @() } }
        $text = $utf8.GetString($bytes, 0, $end)
        return @{ Offset = $Offset + $end + 1; Lines = $text.Split([char]10) }
    }
    catch {
        # Deleted by retention, or briefly locked: try again on the next poll.
        return @{ Offset = $Offset; Lines = @() }
    }
    finally {
        if ($stream) { $stream.Dispose() }
    }
}

# Groups lines into entries and keeps those that pass the filters. Continuation lines at the start of a chunk
# belong to the previous chunk's last entry; $script:lastPassed remembers whether that one was shown.
$script:lastPassed = $false
function Select-Entries([string[]]$Lines) {
    $entries = New-Object Collections.Generic.List[object]
    $current = $null
    foreach ($line in $Lines) {
        $line = $line.TrimEnd([char]13)
        $m = $headerPattern.Match($line)
        if ($m.Success) {
            $pass = $true
            $rank = $levelRank[$m.Groups[2].Value]
            if ($null -eq $rank -or $rank -lt $minRank) { $pass = $false }
            if ($pass -and $categories.Count -gt 0 -and -not $categories.ContainsKey($m.Groups[3].Value)) { $pass = $false }
            if ($pass -and $sinceTime) {
                $stamp = [DateTimeOffset]::ParseExact($m.Groups[1].Value, 'yyyy-MM-ddTHH:mm:ss.fffzzz', $invariant)
                if ($stamp -lt $sinceTime) { $pass = $false }
            }
            $script:lastPassed = $pass
            $current = New-Object Collections.Generic.List[string]
            $current.Add($line)
            if ($pass) { $entries.Add($current) }
        }
        elseif ($line.Length -gt 0 -and $script:lastPassed) {
            if ($null -eq $current) {
                # Continuation of an entry shown in an earlier chunk.
                $current = New-Object Collections.Generic.List[string]
                $entries.Add($current)
            }
            $current.Add($line)
        }
    }
    return , $entries
}

function Write-Entries($Entries) {
    foreach ($entry in $Entries) { foreach ($line in $entry) { Write-Output $line } }
}

# Initial pass over the existing files.
$offsets = @{}
$all = New-Object Collections.Generic.List[object]
foreach ($file in Get-LogFiles) {
    $chunk = Read-NewLines $file.FullName 0
    $offsets[$file.FullName] = $chunk.Offset
    $selected = Select-Entries $chunk.Lines
    if ($Tail -gt 0) {
        $all.AddRange($selected)
        if ($all.Count -gt $Tail) { $all.RemoveRange(0, $all.Count - $Tail) }
    }
    else {
        Write-Entries $selected
    }
}
if ($Tail -gt 0) { Write-Entries $all }

if (-not $Follow) { return }

if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
    Write-Verbose "Waiting for $Path to appear"
}
while ($true) {
    Start-Sleep -Milliseconds $PollMilliseconds
    foreach ($file in Get-LogFiles) {
        $offset = 0L
        if ($offsets.ContainsKey($file.FullName)) { $offset = $offsets[$file.FullName] }
        $chunk = Read-NewLines $file.FullName $offset
        $offsets[$file.FullName] = $chunk.Offset
        if ($chunk.Lines.Count -gt 0) { Write-Entries (Select-Entries $chunk.Lines) }
    }
}
