[CmdletBinding()]
param(
    [string]$RootPath = $PSScriptRoot,
    [string]$OutputPath,
    [switch]$FailOnCheckFailure
)

$ErrorActionPreference = "Stop"

function Invoke-WorkerCheck {
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorkerPath,
        [Parameter(Mandatory = $true)]
        [string]$Argument,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $result = [ordered]@{
        name = $Name
        command = "$WorkerPath $Argument"
    }

    try {
        $output = & $WorkerPath $Argument 2>&1 | Out-String
        $result.output = $output.Trim()
        $result.exit_code = $LASTEXITCODE
        $result.status = if ($LASTEXITCODE -eq 0) { "passed" } else { "failed" }
    }
    catch {
        $result.status = "error"
        $result.error = $_.Exception.Message
    }

    return [pscustomobject]$result
}

function Invoke-ProtocolCheck {
    param(
        [Parameter(Mandatory = $true)]
        [string]$WorkerPath,
        [Parameter(Mandatory = $true)]
        [string]$Request,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedStatus,
        [Parameter(Mandatory = $true)]
        [int]$ExpectedSolutionCount,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $result = [ordered]@{
        name = $Name
        expected_status = $ExpectedStatus
        expected_solution_count = $ExpectedSolutionCount
        timeout_milliseconds = 15000
    }
    $requestText = $Request.Trim()
    $result.request = $requestText
    if (-not $requestText.StartsWith("{", [System.StringComparison]::Ordinal)) {
        throw "Diagnostic protocol request does not start with a JSON object."
    }
    $transportId = [guid]::NewGuid().ToString("N")
    $requestFile = Join-Path ([System.IO.Path]::GetTempPath()) "solver-request-$transportId.json"
    $commandFile = Join-Path ([System.IO.Path]::GetTempPath()) "solver-command-$transportId.cmd"
    $stdoutFile = Join-Path ([System.IO.Path]::GetTempPath()) "solver-stdout-$transportId.txt"
    $stderrFile = Join-Path ([System.IO.Path]::GetTempPath()) "solver-stderr-$transportId.txt"
    $requestBytes = [System.Text.UTF8Encoding]::new($false).GetBytes($requestText + "`n")
    [System.IO.File]::WriteAllBytes($requestFile, $requestBytes)
    $commandText = "@echo off`r`ntype `"$requestFile`" | `"$WorkerPath`" > `"$stdoutFile`" 2> `"$stderrFile`"`r`nexit /b %ERRORLEVEL%`r`n"
    [System.IO.File]::WriteAllText($commandFile, $commandText, [System.Text.ASCIIEncoding]::new())
    $result.transport = "cmd_file_redirect"
    $result.request_byte_length = $requestBytes.Length
    $result.request_byte_prefix = [System.BitConverter]::ToString(
        $requestBytes,
        0,
        [Math]::Min(8, $requestBytes.Length))
    $process = $null

    try {
        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $env:ComSpec
        $startInfo.Arguments = '/d /s /c ""' + $commandFile + '""'
        $startInfo.WorkingDirectory = $resolvedRoot
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        if (-not $process.Start()) {
            throw "Solver worker process did not start."
        }

        if (-not $process.WaitForExit($result.timeout_milliseconds)) {
            $result.timed_out = $true
            try {
                & taskkill.exe /PID $process.Id /T /F *> $null
                if (-not $process.WaitForExit(5000)) {
                    $result.kill_timeout = $true
                }
            }
            catch {
                $result.kill_error = $_.Exception.Message
            }

            if (-not $process.HasExited) {
                $result.status = "failed"
                $result.error = "Solver worker did not exit after the diagnostic timeout."
                return [pscustomobject]$result
            }
        }
        $stdout = if (Test-Path $stdoutFile -PathType Leaf) {
            [System.IO.File]::ReadAllText($stdoutFile)
        }
        else {
            ""
        }
        $stderr = if (Test-Path $stderrFile -PathType Leaf) {
            [System.IO.File]::ReadAllText($stderrFile)
        }
        else {
            ""
        }

        $process.Refresh()
        $result.exit_code = $process.ExitCode
        $result.stdout = $stdout.Trim()
        $result.stderr = $stderr.Trim()
        if ($result.timed_out) {
            $result.status = "failed"
            $result.error = "Solver worker did not finish within the diagnostic timeout."
            return [pscustomobject]$result
        }

        $response = $stdout.Trim() | ConvertFrom-Json
        $solutions = @($response.solutions)
        $result.response_status = $response.status
        $result.solution_count = $solutions.Count
        $result.status = if ($process.ExitCode -eq 0 -and
            $response.status -eq $ExpectedStatus -and
            $solutions.Count -eq $ExpectedSolutionCount) { "passed" } else { "failed" }
    }
    catch {
        $result.status = "error"
        $result.error = $_.Exception.Message
    }
    finally {
        if ($null -ne $process) {
            $process.Dispose()
        }
        Remove-Item -Force $requestFile, $commandFile, $stdoutFile, $stderrFile -ErrorAction SilentlyContinue
    }

    return [pscustomobject]$result
}

$resolvedRoot = (Resolve-Path $RootPath).Path
$workerPath = Join-Path $resolvedRoot "SolverWorker.exe"
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $resolvedRoot "solver-diagnostics.json"
}

$diagnostics = [ordered]@{
    generated_at_utc = [DateTimeOffset]::UtcNow
    computer = [ordered]@{
        machine_name = $env:COMPUTERNAME
        user_name = $env:USERNAME
        os_architecture = $env:PROCESSOR_ARCHITECTURE
        os_description = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        process_architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    }
    application_root = $resolvedRoot
    worker = [ordered]@{
        path = $workerPath
        exists = Test-Path $workerPath -PathType Leaf
    }
    environment = [ordered]@{
        scheduler_solver_worker = $env:SCHEDULER_SOLVER_WORKER
        scheduler_allow_external_solver = $env:SCHEDULER_ALLOW_EXTERNAL_SOLVER
        cadical_options = @(Get-ChildItem Env:CADICAL_* -ErrorAction SilentlyContinue |
            Sort-Object Name |
            ForEach-Object { @{ name = $_.Name; value = $_.Value } })
    }
}

if ($diagnostics.worker.exists) {
    $diagnostics.worker.sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $workerPath).Hash.ToLowerInvariant()
    $signature = Get-AuthenticodeSignature -FilePath $workerPath
    $diagnostics.worker.signature_status = $signature.Status.ToString()
    $diagnostics.worker.signature_status_message = $signature.StatusMessage
    $diagnostics.worker.signer_subject = if ($null -eq $signature.SignerCertificate) {
        $null
    }
    else {
        $signature.SignerCertificate.Subject
    }
    $versionCheck = Invoke-WorkerCheck `
        -WorkerPath $workerPath `
        -Argument "--version" `
        -Name "version"
    $selfTestCheck = Invoke-WorkerCheck `
        -WorkerPath $workerPath `
        -Argument "--self-test" `
        -Name "self_test"
    $protocolSelfTestCheck = Invoke-WorkerCheck `
        -WorkerPath $workerPath `
        -Argument "--protocol-self-test" `
        -Name "protocol_self_test"
    $diagnostics.worker.checks = @($versionCheck, $selfTestCheck, $protocolSelfTestCheck)
    $satRequest = @'
{"protocol_version":2,"request_id":"debug-sat","variable_count":1,"clauses":[[1]],"exactly_one_groups":[],"max_solutions":1,"timeout_milliseconds":5000}
'@
    $unsatRequest = @'
{"protocol_version":2,"request_id":"debug-unsat","variable_count":1,"clauses":[[1],[-1]],"exactly_one_groups":[],"max_solutions":1,"timeout_milliseconds":5000}
'@
    $satCheck = Invoke-ProtocolCheck `
        -WorkerPath $workerPath `
        -Request $satRequest `
        -ExpectedStatus "feasible" `
        -ExpectedSolutionCount 1 `
        -Name "sat"
    $unsatCheck = Invoke-ProtocolCheck `
        -WorkerPath $workerPath `
        -Request $unsatRequest `
        -ExpectedStatus "infeasible" `
        -ExpectedSolutionCount 0 `
        -Name "unsat"
    $diagnostics.worker.protocol_checks = @($satCheck, $unsatCheck)
}
else {
    $diagnostics.worker.error = "SolverWorker.exe was not found beside the desktop executable."
}

$checkStatuses = @(
    @($diagnostics.worker.checks) | ForEach-Object { $_.status }
    @($diagnostics.worker.protocol_checks) | ForEach-Object { $_.status }
)
$diagnostics.status = if ($diagnostics.worker.exists -and
    $checkStatuses.Count -gt 0 -and
    ($checkStatuses | Where-Object { $_ -ne "passed" }).Count -eq 0) { "passed" } else { "failed" }

$json = $diagnostics | ConvertTo-Json -Depth 8
$outputDirectory = Split-Path -Parent ([System.IO.Path]::GetFullPath($OutputPath))
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
[System.IO.File]::WriteAllText(
    [System.IO.Path]::GetFullPath($OutputPath),
    $json,
    [System.Text.UTF8Encoding]::new($false))

Write-Host "Solver diagnostics written to $([System.IO.Path]::GetFullPath($OutputPath))"
if ($FailOnCheckFailure -and $diagnostics.status -ne "passed") {
    throw "One or more solver diagnostics failed."
}
