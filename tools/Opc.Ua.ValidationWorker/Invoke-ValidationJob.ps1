<#
.SYNOPSIS
    Runs one NodeSet Editor specification-validation job (Pop -> download -> validate -> Push) and exits.

.DESCRIPTION
    A self-contained PowerShell harness for the interactive validation worker - no pre-compiled
    worker required. It must run as an interactive Windows user on a VM with Microsoft Word
    installed, because Opc.Ua.SpecificationValidator automates Word via COM and cannot run in a
    service/session-0 context.

    The validator is a .NET global tool (OPCFoundation.Opc.Ua.SpecificationValidator). On first run
    this script checks whether it is installed and, if not, installs it via `dotnet tool install`
    (from -ToolPackageSource if given, else the configured/default NuGet feeds). Only the .NET SDK
    ('dotnet') is a prerequisite.

    It drives the Pop/Push queue contract:

        1. POST  /api/opcua/v1/validation/worker/jobs/pop            -> claim oldest queued job (-> Running)
        2. GET   /api/opcua/v1/validation/worker/jobs/{id}/document  -> the uploaded .docx
        3. GET   /api/opcua/v1/validation/worker/jobs/{id}/nodesets  -> ZIP of NodeSet + deps (primary = _primary.xml)
        4. RUN   Opc.Ua.SpecificationValidator convert-validate <doc> <out> --nodeset _primary.xml --dependency-dir <deps>
        5. POST  /api/opcua/v1/validation/worker/jobs/{id}/push      -> log + *-validation.json + counts + exit code

    A 410 (job deleted) or 409 (job cancelled / re-popped) at any download or on push aborts the
    run cleanly, discards the temp output, and returns to polling (or exits, in single-shot mode).

    Single-shot by default (ideal for a Scheduled Task fired on an interval). Pass -Loop to poll
    continuously in a single interactive session.

.PARAMETER ServerBaseUrl
    NodeSet Editor server base URL (no trailing /api). Falls back to Worker:ServerBaseUrl in appsettings.json.

.PARAMETER ApiKey
    Shared secret; must match the server's Validation:WorkerApiKey. Do NOT commit. Falls back to
    Worker:ApiKey in appsettings.json, then the WORKER_ApiKey environment variable.

.PARAMETER WorkerId
    Identifier stamped on claimed jobs. Defaults to the machine name.

.PARAMETER ValidatorCommand
    Validator command name or full path. Defaults to the global-tool command
    'Opc.Ua.SpecificationValidator'. Set to an explicit .exe/path to bypass tool provisioning.

.PARAMETER ToolPackageSource
    Optional NuGet source (feed URL or local folder) to install the validator tool from. When set,
    a transient nuget.config exposing this source plus nuget.org is used. Omit to use the machine's
    default NuGet feeds.

.PARAMETER ToolVersion
    Optional explicit tool version to install (e.g. 1.0.1-preview). Omit for the latest (prerelease).

.PARAMETER UpdateTool
    Update the validator tool to the latest version even if it is already installed.

.PARAMETER SkipInstall
    Do not attempt to install the validator; fail if it is not already available.

.PARAMETER WorkDir
    Scratch directory for per-job artifacts (auto-cleaned). Defaults to .\work next to this script.

.PARAMETER Loop
    Poll continuously instead of processing a single job and exiting.

.PARAMETER PollIntervalSeconds
    Idle poll interval when -Loop is set (default 15).

.EXAMPLE
    .\Invoke-ValidationJob.ps1 -ServerBaseUrl https://opcua-nodeset-editor-02.azurewebsites.net -ApiKey $env:WORKER_ApiKey

.EXAMPLE
    # Install the validator from a local package feed, then poll continuously:
    .\Invoke-ValidationJob.ps1 -ToolPackageSource \\build\packages -Loop -PollIntervalSeconds 20
#>
#requires -version 5.1
[CmdletBinding()]
param(
    [string]$ServerBaseUrl,
    [string]$ApiKey,
    [string]$WorkerId = $env:COMPUTERNAME,
    [string]$ValidatorCommand,
    [string]$ToolPackageSource,
    [string]$ToolVersion,
    [switch]$UpdateTool,
    [switch]$SkipInstall,
    [string]$WorkDir,
    [switch]$KeepWork,
    [switch]$Loop,
    [int]$PollIntervalSeconds = 15
)

$ToolId      = 'OPCFoundation.Opc.Ua.SpecificationValidator'
$ToolCommand = 'Opc.Ua.SpecificationValidator'

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# System.Net.Http is in the GAC on .NET Framework (PS 5.1) but not auto-loaded; on PS 7 it is built in.
if ($PSVersionTable.PSEdition -eq 'Desktop') { Add-Type -AssemblyName System.Net.Http }

# ---------------------------------------------------------------- Configuration

# Layer defaults from appsettings.json (Worker section), then explicit params / env win.
$appSettingsPath = Join-Path $PSScriptRoot 'appsettings.json'
$cfg = @{}
if (Test-Path $appSettingsPath) {
    try {
        $json = Get-Content $appSettingsPath -Raw | ConvertFrom-Json
        if ($json.PSObject.Properties.Name -contains 'Worker') { $cfg = $json.Worker }
    } catch { Write-Warning "Could not parse $appSettingsPath : $($_.Exception.Message)" }
}
function Get-Cfg($name, $fallback) {
    if ($cfg -and ($cfg.PSObject.Properties.Name -contains $name) -and $cfg.$name) { return $cfg.$name }
    return $fallback
}

if (-not $ServerBaseUrl)     { $ServerBaseUrl     = Get-Cfg 'ServerBaseUrl' $null }
if (-not $ApiKey)            { $ApiKey            = Get-Cfg 'ApiKey' $env:WORKER_ApiKey }
if (-not $ValidatorCommand)  { $ValidatorCommand  = Get-Cfg 'ValidatorCommand' (Get-Cfg 'ValidatorExePath' $ToolCommand) }
if (-not $ToolPackageSource) { $ToolPackageSource = Get-Cfg 'ToolPackageSource' $null }
if (-not $ToolVersion)       { $ToolVersion       = Get-Cfg 'ToolVersion' $null }
if (-not $WorkDir)           { $WorkDir           = Get-Cfg 'WorkDir' (Join-Path $PSScriptRoot 'work') }
if (-not $WorkerId)          { $WorkerId          = [System.Environment]::MachineName }

if (-not $ServerBaseUrl -or -not $ApiKey) {
    Write-Error "ServerBaseUrl and ApiKey are required (pass as parameters, set them in appsettings.json Worker section, or set `$env:WORKER_ApiKey)."
    exit 1
}

$WorkDir = [System.IO.Path]::GetFullPath($WorkDir)
[void][System.IO.Directory]::CreateDirectory($WorkDir)

function Write-Log($message) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $message) }

# ---------------------------------------------------------------- Validator provisioning

function Test-CommandExists([string]$name) { [bool](Get-Command $name -ErrorAction SilentlyContinue) }

# The .NET global-tool shim dir; a fresh 'dotnet tool install' may not be on this session's PATH yet.
$script:ToolsDir = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.dotnet\tools'
function Add-ToolsDirToPath {
    if ((Test-Path $script:ToolsDir) -and ($env:PATH -notlike "*$script:ToolsDir*")) {
        $env:PATH = "$script:ToolsDir;$env:PATH"
    }
}

function New-TransientNuGetConfig([string]$source) {
    $tmp = [System.IO.Path]::GetTempFileName() + '.config'
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="validator-source" value="$source" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
"@ | Set-Content -Path $tmp -Encoding utf8
    return $tmp
}

function Install-Validator {
    if (-not (Test-CommandExists 'dotnet')) {
        throw "The .NET SDK ('dotnet') is required to install the validator tool but was not found on PATH. Install the .NET 10 SDK (https://dotnet.microsoft.com/download), or supply -ValidatorCommand pointing at an existing Opc.Ua.SpecificationValidator.exe."
    }

    $alreadyInstalled = @(dotnet tool list --global) -match [regex]::Escape($ToolId.ToLower())
    if ($alreadyInstalled -and -not $UpdateTool) { return }   # present; not asked to update
    $verb = if ($alreadyInstalled) { 'update' } else { 'install' }

    $configFile = $null
    try {
        $dotnetArgs = @('tool', $verb, '--global', '--prerelease', $ToolId)
        if ($ToolVersion)       { $dotnetArgs += @('--version', $ToolVersion) }
        if ($ToolPackageSource) {
            $configFile = New-TransientNuGetConfig $ToolPackageSource
            $dotnetArgs += @('--configfile', $configFile)
        }
        Write-Log "installing validator tool: dotnet $($dotnetArgs -join ' ')"
        & dotnet @dotnetArgs
        if ($LASTEXITCODE -ne 0) { throw "dotnet tool $verb failed (exit $LASTEXITCODE)." }
    }
    finally {
        if ($configFile) { Remove-Item $configFile -ErrorAction SilentlyContinue }
    }
}

# Returns a runnable command (path or shim name), installing the tool on demand.
function Resolve-Validator {
    # 1) Explicit path or resolvable command wins - no provisioning.
    if ($ValidatorCommand -and $ValidatorCommand -ne $ToolCommand) {
        if (Test-Path $ValidatorCommand)          { return (Resolve-Path $ValidatorCommand).Path }
        if (Test-CommandExists $ValidatorCommand) { return $ValidatorCommand }
        throw "ValidatorCommand '$ValidatorCommand' was not found (as a file or on PATH)."
    }

    # 2) Global-tool shim already available (possibly just needs the tools dir on PATH).
    Add-ToolsDirToPath
    if (Test-CommandExists $ToolCommand) { return $ToolCommand }

    # 3) Install on demand.
    if ($SkipInstall) {
        throw "Validator '$ToolCommand' is not installed and -SkipInstall was set. Install it with: dotnet tool install --global --prerelease $ToolId"
    }
    Install-Validator
    Add-ToolsDirToPath
    if (Test-CommandExists $ToolCommand) { return $ToolCommand }
    throw "Validator '$ToolCommand' still not found after install. Check that '$script:ToolsDir' is on PATH."
}

# ---------------------------------------------------------------- HTTP client

$http = [System.Net.Http.HttpClient]::new()
$http.BaseAddress = [Uri]$ServerBaseUrl
$http.Timeout = [TimeSpan]::FromMinutes(30)
$http.DefaultRequestHeaders.Add('X-Api-Key', $ApiKey)

# Await helper (PS 5.1 has no await keyword).
function Wait-Task($task) { return $task.GetAwaiter().GetResult() }

$Gone     = 410
$Conflict = 409

# ---------------------------------------------------------------- Queue operations

function Pop-Job {
    $payload = @{ workerId = $WorkerId } | ConvertTo-Json -Compress
    $body = [System.Net.Http.StringContent]::new($payload, [System.Text.Encoding]::UTF8, 'application/json')
    $resp = Wait-Task $http.PostAsync('/api/opcua/v1/validation/worker/jobs/pop', $body)
    if ([int]$resp.StatusCode -eq 204) { return $null }      # queue empty
    if (-not $resp.IsSuccessStatusCode) {
        throw "pop failed: $([int]$resp.StatusCode) $($resp.ReasonPhrase)"
    }
    $text = Wait-Task $resp.Content.ReadAsStringAsync()
    return $text | ConvertFrom-Json
}

# Downloads $url to $dest. Returns $true on success, $false on 410 gone / 409 cancelled (abort cleanly).
function Get-Artifact($url, $dest) {
    $resp = Wait-Task $http.GetAsync($url)
    $code = [int]$resp.StatusCode
    if ($code -eq $Gone -or $code -eq $Conflict) {
        Write-Log "  $code $($resp.ReasonPhrase) - aborting run."
        return $false
    }
    if (-not $resp.IsSuccessStatusCode) { throw "download failed: $code $($resp.ReasonPhrase)" }
    $bytes = Wait-Task $resp.Content.ReadAsByteArrayAsync()
    [System.IO.File]::WriteAllBytes($dest, $bytes)
    return $true
}

function Push-Result($job, [int]$exitCode, [string]$entriesJson, [byte[]]$logBytes, [string]$workerMessage) {
    $counts = Measure-Severities $entriesJson
    $summary = "$($counts.Errors) error(s), $($counts.Warnings) warning(s), $($counts.Infos) info"

    $form = [System.Net.Http.MultipartFormDataContent]::new()
    $form.Add([System.Net.Http.StringContent]::new($job.lockToken),          'lockToken')
    $form.Add([System.Net.Http.StringContent]::new([string]$exitCode),        'exitCode')
    $form.Add([System.Net.Http.StringContent]::new([string]$counts.Errors),   'errorCount')
    $form.Add([System.Net.Http.StringContent]::new([string]$counts.Warnings), 'warningCount')
    $form.Add([System.Net.Http.StringContent]::new([string]$counts.Infos),    'infoCount')
    $form.Add([System.Net.Http.StringContent]::new($summary),                 'summary')
    $form.Add([System.Net.Http.StringContent]::new($entriesJson),             'entries')
    if ($workerMessage) { $form.Add([System.Net.Http.StringContent]::new($workerMessage), 'workerMessage') }
    if ($logBytes)      {
        $logContent = [System.Net.Http.ByteArrayContent]::new($logBytes)
        $form.Add($logContent, 'log', 'validation.log')
    }

    $resp = Wait-Task $http.PostAsync("/api/opcua/v1/validation/worker/jobs/$($job.jobId)/push", $form)
    $code = [int]$resp.StatusCode
    if ($code -eq $Gone -or $code -eq $Conflict) {
        Write-Log "  push $code $($resp.ReasonPhrase) - results discarded (job deleted/cancelled)."
        return
    }
    if (-not $resp.IsSuccessStatusCode) { throw "push failed: $code $($resp.ReasonPhrase)" }
    Write-Log "  pushed ($summary, exit $exitCode)."
}

# ---------------------------------------------------------------- Validator

# Safe property read: returns $null for a missing property (StrictMode would otherwise throw),
# so the worker tolerates a server that predates these fields.
function Get-JobProp($job, [string]$name) {
    $p = $job.PSObject.Properties[$name]
    if ($p) { return $p.Value }
    return $null
}

function Invoke-Validator([string]$docPath, [string]$outDir, [string]$primary, [string]$depsDir, $job) {
    $cliArgs = @('convert-validate', $docPath, $outDir, '--nodeset', $primary, '--dependency-dir', $depsDir)
    # Per-document validator options snapshotted onto the job (see server StartJobAsync):
    #   profileGroupName -> --profile-group <name>, verbose -> --verbose, ignoreCodes -> --ignore "<c1;c2>"
    $profileGroup = Get-JobProp $job 'profileGroupName'
    $verbose      = Get-JobProp $job 'verbose'
    $ignoreCodes  = Get-JobProp $job 'ignoreCodes'
    if ($profileGroup) { $cliArgs += @('--profile-group', $profileGroup) }
    if ($verbose)      { $cliArgs += '--verbose' }
    if ($ignoreCodes)  { $cliArgs += @('--ignore', $ignoreCodes) }
    Write-Log "  running: $script:Validator $($cliArgs -join ' ')"
    # Out-Host streams the validator's output to the console for the operator while keeping it OUT
    # of this function's return value, so only the exit code below is returned to the caller.
    & $script:Validator @cliArgs | Out-Host
    return $LASTEXITCODE
}

function Measure-Severities([string]$entriesJson) {
    $result = [pscustomobject]@{ Errors = 0; Warnings = 0; Infos = 0 }
    try {
        $entries = $entriesJson | ConvertFrom-Json
        if ($null -eq $entries) { return $result }
        foreach ($e in @($entries)) {
            switch (("" + $e.severity).ToLowerInvariant()) {
                'error'   { $result.Errors++ }
                'warning' { $result.Warnings++ }
                'info'    { $result.Infos++ }
            }
        }
    } catch { }  # malformed JSON -> leave zeros
    return $result
}

function Get-SafeFileName([string]$raw, [string]$fallback) {
    if (-not $raw) { return $fallback }
    $name = [System.IO.Path]::GetFileName($raw)
    foreach ($c in [System.IO.Path]::GetInvalidFileNameChars()) { $name = $name.Replace($c, '_') }
    if ([string]::IsNullOrWhiteSpace($name)) { return $fallback }
    return $name
}

# ---------------------------------------------------------------- Job processing

function Invoke-Job($job) {
    # Print the job parameters clearly before doing any work.
    $modelUri = Get-JobProp $job 'modelUri'
    $profileGroup = Get-JobProp $job 'profileGroupName'
    $verbose = [bool](Get-JobProp $job 'verbose')
    $ignoreCodes = Get-JobProp $job 'ignoreCodes'
    Write-Log "popped job $($job.jobId)"
    Write-Log "  model URI:       $(if ($modelUri)    { $modelUri }    else { '(none)' })"
    Write-Log "  document:        $($job.fileName)"
    Write-Log "  profile group:   $(if ($profileGroup) { $profileGroup } else { '(none)' })"
    Write-Log "  verbose:         $verbose"
    Write-Log "  suppress errors: $(if ($ignoreCodes) { $ignoreCodes } else { '(none)' })"

    $jobDir = Join-Path $WorkDir $job.jobId
    if (Test-Path $jobDir) { Remove-Item $jobDir -Recurse -Force }
    $nodesetsDir = Join-Path $jobDir 'nodesets'
    $outDir      = Join-Path $jobDir 'out'
    [void][System.IO.Directory]::CreateDirectory($nodesetsDir)
    [void][System.IO.Directory]::CreateDirectory($outDir)

    try {
        $lt = [Uri]::EscapeDataString($job.lockToken)

        # 1) Word document.
        $docPath = Join-Path $jobDir (Get-SafeFileName $job.fileName 'document.docx')
        if (-not (Get-Artifact "/api/opcua/v1/validation/worker/jobs/$($job.jobId)/document?lockToken=$lt" $docPath)) { return }

        # 2) NodeSet bundle (primary = _primary.xml).
        $zipPath = Join-Path $jobDir 'nodesets.zip'
        if (-not (Get-Artifact "/api/opcua/v1/validation/worker/jobs/$($job.jobId)/nodesets?lockToken=$lt" $zipPath)) { return }
        Expand-Archive -Path $zipPath -DestinationPath $nodesetsDir -Force
        $primary = Join-Path $nodesetsDir '_primary.xml'
        if (-not (Test-Path $primary)) {
            Write-Log "  [error] bundle had no _primary.xml"
            Push-Result $job 2 '[]' $null 'NodeSet bundle missing primary.'
            return
        }

        # 3) Run the validator (docx -> preprocess -> convert -> validate).
        $exitCode = Invoke-Validator $docPath $outDir $primary $nodesetsDir $job
        Write-Log "  validator exit code: $exitCode"

        # 4) Collect outputs (recurse - convert-validate may nest a _work dir).
        $jsonFile = Get-ChildItem $outDir -Recurse -Filter '*-validation.json' -File -ErrorAction SilentlyContinue | Select-Object -First 1
        $logFile  = Get-ChildItem $outDir -Recurse -Filter '*-validation.log'  -File -ErrorAction SilentlyContinue | Select-Object -First 1

        if ($jsonFile) { Write-Log "  found results: $($jsonFile.FullName) ($($jsonFile.Length) bytes)" }
        else {
            Write-Log "  [warn] no *-validation.json found under $outDir. Files present:"
            $present = Get-ChildItem $outDir -Recurse -File -ErrorAction SilentlyContinue
            if ($present) { $present | ForEach-Object { Write-Log "    - $($_.FullName) ($($_.Length) bytes)" } }
            else { Write-Log "    (none)" }
        }

        $entriesJson = if ($jsonFile) { Get-Content $jsonFile.FullName -Raw } else { '[]' }
        $logBytes    = if ($logFile)  { [System.IO.File]::ReadAllBytes($logFile.FullName) } else { $null }
        $msg         = if ($exitCode -eq 0) { $null } else { "Validator exited with code $exitCode." }

        # 5) Push.
        Push-Result $job $exitCode $entriesJson $logBytes $msg
    }
    finally {
        if ($KeepWork) { Write-Log "  keeping work dir: $jobDir" }
        else { try { Remove-Item $jobDir -Recurse -Force -ErrorAction SilentlyContinue } catch { } }
    }
}

# ---------------------------------------------------------------- Entry point

Write-Log "worker '$WorkerId' -> $ServerBaseUrl"
$script:Validator = Resolve-Validator
Write-Log "validator ready: $script:Validator"

try {
    if ($Loop) {
        Write-Log "polling every ${PollIntervalSeconds}s (Ctrl+C to stop)"
        while ($true) {
            try {
                $job = Pop-Job
                if ($null -eq $job) { Start-Sleep -Seconds $PollIntervalSeconds; continue }
                Invoke-Job $job
            }
            catch {
                Write-Log "[error] $($_.Exception.Message)"
                Start-Sleep -Seconds $PollIntervalSeconds
            }
        }
    }
    else {
        $job = Pop-Job
        if ($null -eq $job) { Write-Log "queue empty - nothing to do."; exit 0 }
        Invoke-Job $job
    }
}
finally {
    $http.Dispose()
}
