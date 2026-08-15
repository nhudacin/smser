<#
.SYNOPSIS
    Shows the most recent visits to smser from the production audit log.

.DESCRIPTION
    Reads the `visits` table in Azure Table Storage, newest first.

    The table is partitioned by UTC date and rows are keyed on descending ticks, so a
    single partition already comes back newest-first. This walks backwards a day at a
    time until it has enough rows, which keeps the common case ("what happened today")
    to one cheap query.

    Needs the Azure CLI and an account with read access to the storage account:
        az login

.PARAMETER Count
    How many entries to return. Default 100.

.PARAMETER Days
    How many days back to look before giving up. Default 14.

.PARAMETER Event
    Only show one kind: page, roster-viewed, roster-created, roster-updated.

.PARAMETER Raw
    Emit objects instead of a formatted table, for grouping and exporting.

.EXAMPLE
    .\scripts\list-visits.ps1
    The last 100 visits.

.EXAMPLE
    .\scripts\list-visits.ps1 -Count 20 -Event roster-created
    The last 20 rosters created.

.EXAMPLE
    .\scripts\list-visits.ps1 -Count 500 -Raw | Group-Object Ip | Sort-Object Count -Descending | Select-Object -First 10
    The ten busiest addresses.

.EXAMPLE
    .\scripts\list-visits.ps1 -Count 1000 -Raw | Export-Csv visits.csv -NoTypeInformation
#>
[CmdletBinding()]
param(
    [int]$Count = 100,
    [int]$Days = 14,
    [ValidateSet('page', 'roster-viewed', 'roster-created', 'roster-updated')]
    [string]$Event,
    [switch]$Raw
)

$ErrorActionPreference = 'Stop'

$account = 'hudacineastussmser'
$table = 'visits'

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "The Azure CLI is required. See https://learn.microsoft.com/cli/azure/install-azure-cli"
}

Write-Verbose "Fetching a key for $account"
$key = az storage account keys list --account-name $account --query "[0].value" -o tsv 2>$null
if (-not $key) {
    throw "Could not read a key for '$account'. Run 'az login' and check you have access to the subscription."
}

$rows = [System.Collections.Generic.List[object]]::new()

# Walk back a day at a time. Today's partition usually answers the whole question, and
# stopping as soon as we have enough avoids scanning a fortnight to show ten rows.
for ($back = 0; $back -lt $Days -and $rows.Count -lt $Count; $back++) {
    $day = (Get-Date).ToUniversalTime().AddDays(-$back).ToString('yyyy-MM-dd')

    $filter = "PartitionKey eq '$day'"
    if ($Event) { $filter += " and Event eq '$Event'" }

    Write-Verbose "Querying $day"
    $json = az storage entity query `
        --table-name $table `
        --account-name $account `
        --account-key $key `
        --filter $filter `
        --num-results $Count `
        -o json 2>$null

    if (-not $json) { continue }

    $items = ($json | ConvertFrom-Json).items
    if (-not $items) { continue }

    foreach ($i in $items) {
        if ($rows.Count -ge $Count) { break }

        $rows.Add([pscustomobject]@{
            # Rendered in local time; the log itself is UTC.
            When    = if ($i.OccurredAt) { ([datetime]$i.OccurredAt).ToLocalTime() } else { $null }
            Event   = $i.Event
            Path    = $i.Path
            Roster  = $i.RosterId
            Numbers = $i.NumberCount
            Ip      = $i.Ip
            Country = $i.Country
            Browser = $i.UserAgent
            Referer = $i.Referer
        })
    }
}

if ($rows.Count -eq 0) {
    Write-Host "No visits found in the last $Days day(s)." -ForegroundColor Yellow
    return
}

# A partition is already newest-first, but a multi-day walk concatenates them, so sort.
$sorted = $rows | Sort-Object When -Descending

if ($Raw) { return $sorted }

$sorted |
    Select-Object `
        @{ n = 'When'; e = { $_.When.ToString('MM-dd HH:mm:ss') } },
        @{ n = 'Event'; e = { $_.Event } },
        @{ n = 'Roster'; e = { $_.Roster } },
        @{ n = 'IP'; e = { $_.Ip } },
        @{ n = 'Path'; e = { $_.Path } },
        @{ n = 'Browser'; e = {
            # Full user agents are unreadable in a console. Keep the recognisable part.
            switch -Regex ($_.Browser) {
                'iPhone|iPad'  { 'iOS'; break }
                'Android'      { 'Android'; break }
                'Edg/'         { 'Edge'; break }
                'Chrome/'      { 'Chrome'; break }
                'Firefox/'     { 'Firefox'; break }
                'Safari/'      { 'Safari'; break }
                'bot|crawl|spider|HeadlessChrome' { 'bot'; break }
                '^$'           { '' ; break }
                default        { if ($_) { ($_ -split '[/ ]')[0] } else { '' } }
            }
        } } |
    Format-Table -AutoSize

Write-Host ""
Write-Host ("{0} entr{1}. Totals by event:" -f $sorted.Count, $(if ($sorted.Count -eq 1) { 'y' } else { 'ies' })) -ForegroundColor Cyan
$sorted | Group-Object Event | Sort-Object Count -Descending | ForEach-Object {
    Write-Host ("  {0,-16} {1}" -f $_.Name, $_.Count)
}
Write-Host ("  {0,-16} {1}" -f 'unique IPs', ($sorted | Where-Object Ip | Select-Object -ExpandProperty Ip -Unique).Count)
