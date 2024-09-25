properties {
    $repoRoot = Split-Path -Parent $PSScriptRoot
}

task Build {
    Write-Output "Building..."
}

task RunDev {
    Write-Output "Running Dev..."

    # ensure azure is running
    try {
        Invoke-RestMethod "http://127.0.0.1:10000" -Method Get
    }
    catch {
        if ( $_.ErrorDetails.Message -match "InvalidQueryParameterValue") {
            Write-Output "Azurite Already Running!"
        }
        else {
            Write-Output "Starting Azurite..."
            $null = azurite --silent --location c:\azurite --debug c:\azurite\debug.log &
        }
    }

    # cd to src dir
    Set-Location "$repoRoot/src"
    npm run dev
}