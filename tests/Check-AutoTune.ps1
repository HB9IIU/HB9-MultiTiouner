$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$headers = "using System;`nusing System.Collections.Generic;`nusing System.Linq;`nusing System.Net.WebSockets;`nusing System.Threading;`nusing System.Threading.Tasks;`n"
$sources = @('BatcMonitor.cs', 'AutoTunePolicy.cs') | ForEach-Object {
    (Get-Content (Join-Path $projectRoot "Receiver/$_") | Where-Object { $_ -notmatch '^using ' }) -join "`n"
}
$stub = 'namespace Serilog { public static class Log { public static void Debug(Exception ex,string text) {} } }'
$checks = Get-Content (Join-Path $PSScriptRoot 'AutoTuneChecks.cs.txt') -Raw
Add-Type -TypeDefinition ($headers + ($sources -join "`n") + $stub + $checks)
[AutoTuneChecks]::Run()
