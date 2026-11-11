# Runs a spike for N seconds and samples, once a second, the process's CPU, memory and GPU-engine
# utilisation (Windows performance counters, which need no admin rights). Prints a JSON summary.
# The spike's own --stats file supplies frame timings. Nothing here plays sound or touches the network.
param(
    [Parameter(Mandatory)][string]$Exe,
    [string]$SpikeArgs = "",
    [int]$Seconds = 30,
    [Parameter(Mandatory)][string]$Out
)
$ErrorActionPreference = 'Stop'
Get-Process | Where-Object { $_.Path -eq $Exe } | Stop-Process -Force -ErrorAction SilentlyContinue
$statsPath = [IO.Path]::ChangeExtension($Out, ".stats.json")
$p = Start-Process $Exe -ArgumentList "$SpikeArgs --no-playback --seconds $($Seconds + 6) --stats `"$statsPath`"" -PassThru
Start-Sleep 3   # skip start-up
$cores = [Environment]::ProcessorCount
$samples = @()
$prevCpu = $p.TotalProcessorTime; $prevT = Get-Date
$end = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $end) {
    Start-Sleep -Milliseconds 500   # Get-Counter itself takes ~1 s, so samples land every ~1.5 s
    $p.Refresh()
    if ($p.HasExited) { break }
    $now = Get-Date; $cpu = $p.TotalProcessorTime
    $cpuPct = ($cpu - $prevCpu).TotalMilliseconds / ($now - $prevT).TotalMilliseconds / $cores * 100
    $prevCpu = $cpu; $prevT = $now
    $gpu3d = 0.0; $gpuAll = 0.0
    try {
        $c = Get-Counter "\GPU Engine(pid_$($p.Id)_*)\Utilization Percentage" -ErrorAction Stop
        foreach ($s in $c.CounterSamples) {
            $gpuAll += $s.CookedValue
            if ($s.InstanceName -like '*engtype_3d*') { $gpu3d += $s.CookedValue }
        }
    } catch {}
    $mem = 0.0
    try { $mem = (Get-Counter "\GPU Process Memory(pid_$($p.Id)_*)\Dedicated Usage" -ErrorAction Stop).CounterSamples | Measure-Object CookedValue -Sum | ForEach-Object { $_.Sum / 1MB } } catch {}
    $samples += [pscustomobject]@{ cpu = $cpuPct; gpu3d = $gpu3d; gpuAll = $gpuAll; ws = $p.WorkingSet64 / 1MB; priv = $p.PrivateMemorySize64 / 1MB; gpuMem = $mem }
}
$p.WaitForExit(30000) | Out-Null
function Avg($prop) { [math]::Round(($samples | Measure-Object $prop -Average).Average, 2) }
function Max($prop) { [math]::Round(($samples | Measure-Object $prop -Maximum).Maximum, 2) }
$stats = if (Test-Path $statsPath) { Get-Content $statsPath -Raw | ConvertFrom-Json } else { $null }
$power = (Get-CimInstance Win32_Battery -ErrorAction SilentlyContinue | Select-Object -First 1)
$summary = [ordered]@{
    exe = Split-Path $Exe -Leaf; args = $SpikeArgs; seconds = $Seconds; samples = $samples.Count
    onBattery = if ($power) { $power.BatteryStatus -eq 1 } else { $false }
    cpuPercentAvg = Avg cpu; cpuPercentMax = Max cpu
    gpu3dPercentAvg = Avg gpu3d; gpuAllEnginesPercentAvg = Avg gpuAll
    workingSetMBAvg = Avg ws; privateMBAvg = Avg priv; gpuDedicatedMBAvg = Avg gpuMem
    fps = $stats.fps; frameIntervalMs = $stats.frameIntervalMs; cpuMsPerFrame = $stats.cpuMsPerFrame; slowFrames = $stats.slowFrames
    adapter = $stats.adapter; monitors = $stats.monitors; startupMs = $stats.startupMs
}
$summary | ConvertTo-Json -Depth 5 | Set-Content $Out -Encoding utf8
$summary | ConvertTo-Json -Depth 5
