# HotspotKeeper - network helper script
# Author: MingYu (github.com/mingyu0401)
# NOTE: Untested code paths may fail; the C# app surfaces ERR lines to the user.
param(
    [Parameter(Mandatory = $true)][string]$Action,
    [string]$Arg1 = ''
)

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

function Out-Result([string]$s) { Write-Output "RESULT:$s" }
function Out-Err([string]$s) { Write-Output "ERR:$($s -replace '[\r\n]+', ' ')" }

# ---------- WinRT helpers ----------

function Get-TetheringManager {
    $null = [Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType=WindowsRuntime]
    $null = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType=WindowsRuntime]
    $profile = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()
    if ($null -eq $profile) { throw 'No active internet connection profile found (no network to share).' }
    return [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($profile)
}

# Bridge WinRT IAsyncOperation<T> to a .NET Task via System.WindowsRuntimeSystemExtensions.AsTask.
Add-Type -AssemblyName System.Runtime.WindowsRuntime | Out-Null
$script:AsTaskOp = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
} | Select-Object -First 1)

# Fire the tethering start/stop operation WITHOUT blocking: in a headless PowerShell the
# completion callback may never run even though Windows applies the change. We verify the
# effect afterwards by polling the operational state.
function Start-TetheringOp($tm, [string]$method) {
    $op = $tm.$method()
    $resultType = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult, Windows.Networking.NetworkOperators, ContentType=WindowsRuntime]
    $task = $script:AsTaskOp.MakeGenericMethod($resultType).Invoke($null, @($op))
    $null = $task  # fire and forget
}

function Wait-ForState([string]$wanted, [int]$timeoutSec = 20) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ((Get-HotspotState) -eq $wanted) { return $true }
        Start-Sleep -Milliseconds 800
    }
    return $false
}

function Get-HotspotState {
    # returns 'enabled' | 'disabled' | 'unknown'
    try {
        $tm = Get-TetheringManager
        $st = "$($tm.TetheringOperationalState)".ToLower()
        switch ($st) {
            'on'  { return 'enabled' }
            'off' { return 'disabled' }
            default { return $st }
        }
    } catch {
        # Fallback heuristic: Mobile Hotspot's ICS interface always owns 192.168.137.1
        try {
            $ip = Get-NetIPAddress -IPAddress '192.168.137.1' -ErrorAction SilentlyContinue
            if ($ip) { return 'enabled' } else { return 'disabled' }
        } catch { return 'unknown' }
    }
}

function Get-HotspotClientCount {
    # returns count (>=0) or -1 when unknown
    try {
        $tm = Get-TetheringManager
        return [int]$tm.ClientCount
    } catch {
        try {
            $neighbors = Get-NetNeighbor -IPAddress '192.168.137.*' -ErrorAction SilentlyContinue |
                Where-Object {
                    $_.State -eq 'Reachable' -and
                    $_.IPAddress -ne '192.168.137.1' -and
                    $_.IPAddress -notmatch '^(224\.|239\.|ff)'
                }
            return @($neighbors).Count
        } catch { return -1 }
    }
}

# ---------- Adapter helpers ----------
# PhysicalMediaType may come back as a string ('802.3', 'Native 802.11') or a number (14 / 300).

function Test-MediaType($adapter, [string]$pattern, [int]$number) {
    if ("$($adapter.PhysicalMediaType)" -match $pattern) { return $true }
    try { return [int]$adapter.PhysicalMediaType -eq $number } catch { return $false }
}

function Get-WifiAdapters {
    @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object {
        (Test-MediaType $_ '802\.11' 300) -or $_.Name -match 'wi-?fi|wireless|WLAN'
    })
}

function Get-EthernetAdapters {
    @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object {
        Test-MediaType $_ '^802\.3$' 14
    })
}

function Enable-Adapters($adapters) {
    foreach ($a in $adapters) { Enable-NetAdapter -Name $a.Name -Confirm:$false -ErrorAction Stop }
}

function Disable-Adapters($adapters) {
    foreach ($a in $adapters) { Disable-NetAdapter -Name $a.Name -Confirm:$false -ErrorAction Stop }
}

# ---------- Clash helpers ----------

function Start-ClashForWindows {
    $candidates = @()
    if ($Arg1) { $candidates += $Arg1 }
    $candidates += "$env:LOCALAPPDATA\Programs\clash for windows\Clash for Windows.exe"
    $candidates += "$env:LOCALAPPDATA\Programs\Clash for Windows\Clash for Windows.exe"
    $candidates += "$env:LOCALAPPDATA\Programs\Clash-for-Windows\Clash for Windows.exe"
    $candidates += "$env:LOCALAPPDATA\Programs\cfw\Clash for Windows.exe"
    $candidates += 'C:\Program Files\Clash for Windows\Clash for Windows.exe'
    $found = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $found) { throw 'Clash for Windows executable not found. Set its path in Settings first.' }
    Start-Process -FilePath $found
}

function Stop-ClashForWindows {
    Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match 'clash' } |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

# ---------- Dispatch ----------

try {
    switch ($Action) {
        'state' {
            $s = Get-HotspotState
            $c = Get-HotspotClientCount
            Out-Result "state=$s"
            Out-Result "clients=$c"
        }
        'start' {
            $tm = Get-TetheringManager
            if ((Get-HotspotState) -eq 'enabled') { Out-Result 'ok already-on'; break }
            Start-TetheringOp $tm 'StartTetheringAsync'
            if (Wait-ForState 'enabled' 25) { Out-Result 'ok' }
            else { throw 'Hotspot did not turn on within 25s (check WiFi is enabled and Mobile Hotspot is allowed in Settings).' }
        }
        'stop' {
            $tm = Get-TetheringManager
            if ((Get-HotspotState) -eq 'disabled') { Out-Result 'ok already-off'; break }
            Start-TetheringOp $tm 'StopTetheringAsync'
            if (Wait-ForState 'disabled' 25) { Out-Result 'ok' }
            else { throw 'Hotspot did not turn off within 25s.' }
        }
        'wifi-on' {
            $a = Get-WifiAdapters
            if ($a.Count -eq 0) { throw 'No WiFi adapter found.' }
            Enable-Adapters $a
            Out-Result "ok adapters=$($a.Name -join ',')"
        }
        'wifi-off' {
            $a = Get-WifiAdapters
            if ($a.Count -eq 0) { throw 'No WiFi adapter found.' }
            Disable-Adapters $a
            Out-Result "ok adapters=$($a.Name -join ',')"
        }
        'eth-on' {
            $a = Get-EthernetAdapters
            if ($a.Count -eq 0) { throw 'No wired (Ethernet) adapter found.' }
            Enable-Adapters $a
            Out-Result "ok adapters=$($a.Name -join ',')"
        }
        'eth-off' {
            $a = Get-EthernetAdapters
            if ($a.Count -eq 0) { throw 'No wired (Ethernet) adapter found.' }
            Disable-Adapters $a
            Out-Result "ok adapters=$($a.Name -join ',')"
        }
        'clash-start' {
            Start-ClashForWindows
            Out-Result 'ok'
        }
        'clash-stop' {
            Stop-ClashForWindows
            Out-Result 'ok'
        }
        default { throw "Unknown action: $Action" }
    }
} catch {
    Out-Err $_.Exception.Message
    exit 1
}
