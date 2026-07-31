param(
    [switch]$ShowRawXml
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-EventDataMap {
    param([xml]$Xml)

    $map = @{}
    foreach ($node in $Xml.Event.EventData.Data) {
        $name = [string]$node.Name
        if ([string]::IsNullOrWhiteSpace($name)) {
            continue
        }

        $map[$name] = [string]$node.'#text'
    }

    return $map
}

$logName = 'Microsoft-Windows-PushNotification-Platform/Operational'

try {
    [void](Get-WinEvent -ListLog $logName -ErrorAction Stop)
} catch {
    Write-Host "[ToastWatcher] Event log not found: $logName"
    Write-Host "[ToastWatcher] Your system may use a different notification channel."
    exit 1
}

Write-Host '[ToastWatcher] Starting...'
Write-Host "[ToastWatcher] Listening to: $logName"
Write-Host '[ToastWatcher] Event IDs : 3052(Toast), 3053(Badge), 3054(Cancel), 3055(Clear)'
Write-Host '[ToastWatcher] Press Ctrl+C to stop.'

$queryText = '*[System[(EventID=3052 or EventID=3053 or EventID=3054 or EventID=3055)]]'
$query = New-Object System.Diagnostics.Eventing.Reader.EventLogQuery(
    $logName,
    [System.Diagnostics.Eventing.Reader.PathType]::LogName,
    $queryText
)

$watcher = New-Object System.Diagnostics.Eventing.Reader.EventLogWatcher($query)

$subscription = Register-ObjectEvent -InputObject $watcher -EventName EventRecordWritten -SourceIdentifier ToastWatcherEvent -Action {
    $record = $Event.SourceEventArgs.EventRecord
    if ($null -eq $record) {
        return
    }

    $xml = [xml]$record.ToXml()
    $data = Get-EventDataMap -Xml $xml

    $app = if ($data.ContainsKey('AppUserModelId')) { $data['AppUserModelId'] } else { '' }
    $trackingId = if ($data.ContainsKey('TrackingId')) { $data['TrackingId'] } else { '' }
    $sessionId = if ($data.ContainsKey('SessionId')) { $data['SessionId'] } else { '' }
    $messageId = if ($data.ContainsKey('MessageId')) { $data['MessageId'] } else { '' }
    $kind = if ($record.Id -eq 3052) { 'Toast' } elseif ($record.Id -eq 3053) { 'Badge' } elseif ($record.Id -eq 3054) { 'Cancel' } elseif ($record.Id -eq 3055) { 'Clear' } else { 'Unknown' }
    $description = ($record.FormatDescription() -replace '\r?\n', ' ').Trim()

    Write-Host ''
    Write-Host '=== Notification Captured ==='
    Write-Host ("EventId   : {0} ({1})" -f $record.Id, $kind)
    Write-Host ("Time      : {0}" -f $record.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss'))
    Write-Host ("App       : {0}" -f $app)
    Write-Host ("TrackingId: {0}" -f $trackingId)
    Write-Host ("SessionId : {0}" -f $sessionId)
    Write-Host ("MessageId : {0}" -f $messageId)
    if ($description) {
        Write-Host ("Message   : {0}" -f $description)
    }

    if ($ShowRawXml) {
        Write-Host 'RawXml    :'
        Write-Host $record.ToXml()
    }
}

try {
    $watcher.Enabled = $true
    while ($true) {
        [void](Wait-Event -SourceIdentifier ToastWatcherEvent -Timeout 1)
    }
} finally {
    $watcher.Enabled = $false
    Unregister-Event -SourceIdentifier ToastWatcherEvent -ErrorAction SilentlyContinue
    if ($subscription) {
        Remove-Job -Id $subscription.Id -Force -ErrorAction SilentlyContinue
    }
}
