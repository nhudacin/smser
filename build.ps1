[CmdletBinding()]
param(
    [parameter(position = 0)]
    [string[]]$Task = 'default'
)

$ErrorActionPreference = 'Stop'

Invoke-Psake -BuildFile "$PSScriptRoot/build/psake.ps1" -taskList $Task 
exit ([int](-not $psake.build_success))
