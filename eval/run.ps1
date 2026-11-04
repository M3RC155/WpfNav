<#
.SYNOPSIS
    Runs each eval task through Claude Code with built-in tools only and with wpfnav, then compares
    tokens, turns and how many expected facts each answer contains.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File eval\run.ps1 -Repo D:\work\WPF-Samples -Runs 3

.NOTES
    Windows PowerShell 5.1 compatible. Prompts must not contain double quotes (5.1 mangles them in native arguments).
#>
param(
    [Parameter(Mandatory = $true)] [string] $Repo,
    [int] $Runs = 3,
    [string] $Only = "",
    [string] $Exe = "$env:LOCALAPPDATA\WpfNav\WpfNav.Server.exe",
    [string] $Claude = ""
)

if (-not $Claude) {
    $onPath = Get-Command claude -ErrorAction SilentlyContinue
    if ($onPath) { $Claude = $onPath.Source } else { $Claude = Join-Path $env:USERPROFILE ".local\bin\claude.exe" }
}
if (-not (Test-Path $Claude)) { throw "claude CLI not found: $Claude" }
if (-not (Test-Path $Exe)) { throw "wpfnav server not found: $Exe (publish it first)" }
$Repo = (Resolve-Path $Repo).Path

# Windows PowerShell 5.1 emits a parsed JSON array as one object; unroll it so each task is its own item.
$parsed = Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot "tasks.json") | ConvertFrom-Json
$tasks = @($parsed | ForEach-Object { $_ })
if ($Only) { $tasks = @($tasks | Where-Object { $_.id -eq $Only }) }
if ($tasks.Count -eq 0) { throw "no tasks selected" }

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $PSScriptRoot "results\$stamp"
New-Item -ItemType Directory -Force $out | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding $false

function Write-Utf8([string] $path, [string] $text) {
    # No byte-order mark: Claude Code cannot parse an MCP config that starts with one.
    [System.IO.File]::WriteAllText($path, $text, $utf8)
}

function Get-Median([double[]] $values) {
    if ($values.Count -eq 0) { return 0 }
    $sorted = @($values | Sort-Object)
    $mid = [math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return [double]$sorted[$mid] }
    return ([double]$sorted[$mid - 1] + [double]$sorted[$mid]) / 2
}

$none = Join-Path $out "mcp-none.json"
Write-Utf8 $none '{"mcpServers":{}}'
$withServer = Join-Path $out "mcp-wpfnav.json"
Write-Utf8 $withServer (@{ mcpServers = @{ wpfnav = @{ command = $Exe; args = @("--root", $Repo) } } } | ConvertTo-Json -Depth 5 -Compress)
$configs = [ordered]@{ builtin = $none; wpfnav = $withServer }

$rows = New-Object System.Collections.Generic.List[object]
foreach ($task in $tasks) {
    foreach ($config in $configs.Keys) {
        for ($run = 1; $run -le $Runs; $run++) {
            Write-Host "[$($task.id)] $config, run $run of $Runs"
            Push-Location $Repo
            try {
                $lines = & $Claude -p $task.prompt --output-format stream-json --verbose --strict-mcp-config --mcp-config $configs[$config] --allowedTools mcp__wpfnav
            }
            finally {
                Pop-Location
            }

            $events = @(foreach ($line in $lines) { try { $line | ConvertFrom-Json } catch { } })
            $result = @($events | Where-Object { $_.type -eq "result" }) | Select-Object -Last 1
            $calls = 0
            foreach ($e in $events) {
                if ($e.type -ne "assistant" -or -not $e.message.content) { continue }
                foreach ($block in $e.message.content) {
                    if ($block.type -eq "tool_use" -and "$($block.name)".StartsWith("mcp__wpfnav__")) { $calls++ }
                }
            }

            $answer = ""; $tokens = 0; $turns = 0; $cost = 0; $failed = $true
            if ($result) {
                $answer = "$($result.result)"
                $failed = [bool]$result.is_error -or ("$($result.subtype)" -ne "success")
                if ($result.modelUsage) {
                    # modelUsage covers every model the session used (side calls, subagents); usage covers only the main one.
                    foreach ($model in $result.modelUsage.PSObject.Properties) {
                        $m = $model.Value
                        $tokens += [int64]$m.inputTokens + [int64]$m.outputTokens + [int64]$m.cacheReadInputTokens + [int64]$m.cacheCreationInputTokens
                    }
                }
                else {
                    $u = $result.usage
                    $tokens = [int64]$u.input_tokens + [int64]$u.cache_creation_input_tokens + [int64]$u.cache_read_input_tokens + [int64]$u.output_tokens
                }
                $turns = [int]$result.num_turns
                $cost = [double]$result.total_cost_usd
            }
            if ($failed) { Write-Warning "[$($task.id)] $config run $run failed; it is excluded from the medians" }
            $facts = @($task.expected | Where-Object { $answer.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count
            Write-Utf8 (Join-Path $out "$($task.id)-$config-$run.md") $answer
            $rows.Add([pscustomobject]@{
                task = $task.id; config = $config; run = $run; failed = $failed; tokens = $tokens; turns = $turns; cost = $cost
                wpfnav_calls = $calls; facts = $facts; facts_total = @($task.expected).Count
            })
        }
    }
}
$rows | Export-Csv -NoTypeInformation -Encoding UTF8 (Join-Path $out "runs.csv")

$summary = New-Object System.Text.StringBuilder
[void]$summary.AppendLine("# wpfnav eval $stamp")
[void]$summary.AppendLine("")
[void]$summary.AppendLine("Repo: $Repo. Runs per configuration: $Runs. Values are medians.")
[void]$summary.AppendLine("")
[void]$summary.AppendLine("| task | tokens built-in | tokens wpfnav | token reduction | turns built-in | turns wpfnav | facts built-in | facts wpfnav | wpfnav calls | pass |")
[void]$summary.AppendLine("|---|---|---|---|---|---|---|---|---|---|")
$reductions = New-Object System.Collections.Generic.List[double]
$passed = 0
foreach ($task in $tasks) {
    $builtin = @($rows | Where-Object { $_.task -eq $task.id -and $_.config -eq "builtin" -and -not $_.failed })
    $server = @($rows | Where-Object { $_.task -eq $task.id -and $_.config -eq "wpfnav" -and -not $_.failed })
    $tokensB = Get-Median @($builtin | ForEach-Object { $_.tokens })
    $tokensW = Get-Median @($server | ForEach-Object { $_.tokens })
    $turnsB = Get-Median @($builtin | ForEach-Object { $_.turns })
    $turnsW = Get-Median @($server | ForEach-Object { $_.turns })
    $factsB = Get-Median @($builtin | ForEach-Object { $_.facts })
    $factsW = Get-Median @($server | ForEach-Object { $_.facts })
    $callsW = Get-Median @($server | ForEach-Object { $_.wpfnav_calls })
    $reduction = 0
    if ($tokensB -gt 0) { $reduction = 1 - $tokensW / $tokensB }
    $reductions.Add($reduction)
    $pass = ($reduction -ge 0.4) -and ($turnsW -lt $turnsB) -and ($factsW -ge $factsB)
    if ($pass) { $passed++ }
    $total = @($task.expected).Count
    [void]$summary.AppendLine(("| {0} | {1:N0} | {2:N0} | {3:P0} | {4} | {5} | {6}/{7} | {8}/{7} | {9} | {10} |" -f
        $task.id, $tokensB, $tokensW, $reduction, $turnsB, $turnsW, $factsB, $total, $factsW, $callsW, $(if ($pass) { "yes" } else { "no" })))
}
[void]$summary.AppendLine("")
[void]$summary.AppendLine(("Overall median token reduction: {0:P0}. Tasks passing: {1} of {2}." -f (Get-Median $reductions.ToArray()), $passed, $tasks.Count))
$failedRuns = @($rows | Where-Object { $_.failed }).Count
if ($failedRuns -gt 0) { [void]$summary.AppendLine("Failed runs (excluded from the medians; see runs.csv): $failedRuns") }
[void]$summary.AppendLine("")
[void]$summary.AppendLine("A task passes when tokens drop by at least 40%, turns drop, and the answer contains at least as many expected facts.")
[void]$summary.AppendLine("Answers for manual review: $out\<task>-<config>-<run>.md")
Write-Utf8 (Join-Path $out "summary.md") $summary.ToString()
Write-Host ""
Write-Host $summary.ToString()
