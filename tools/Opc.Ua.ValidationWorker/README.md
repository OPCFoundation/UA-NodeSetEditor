# Validation Worker

Runs the OPC UA specification validator against jobs queued by the NodeSet Editor.

It **must run as an interactive Windows user** on a VM with Microsoft Word installed — the
validator (`Opc.Ua.SpecificationValidator`) automates Word via COM and cannot run in a
service / session-0 context. Install it as a scheduled task that runs **only when the user is
logged on**, or launch it from an interactive session.

## `Invoke-ValidationJob.ps1`

A self-contained PowerShell harness — no compiled worker. It provisions the validator itself, so
there is no pre-built worker and no publish step. The only prerequisite is the **.NET SDK**
(`dotnet`) plus Word.

On first run it checks whether the `OPCFoundation.Opc.Ua.SpecificationValidator` .NET global tool
is installed and, if not, installs it with `dotnet tool install --global --prerelease` (from
`-ToolPackageSource` if given, else the machine's default NuGet feeds). Then it runs the Pop/Push
loop:

1. `POST /api/opcua/v1/validation/worker/jobs/pop` → claims the oldest queued job (→ Running).
2. `GET  …/jobs/{id}/document?lockToken=…` → the uploaded `.docx`.
3. `GET  …/jobs/{id}/nodesets?lockToken=…` → a ZIP of the model's NodeSet + dependencies
   (the primary NodeSet is always `_primary.xml`).
4. Runs `Opc.Ua.SpecificationValidator convert-validate <doc.docx> <out> --nodeset _primary.xml
   --dependency-dir <nodesets>`.
5. `POST …/jobs/{id}/push` (multipart) → the `*-validation.log`, the structured
   `*-validation.json` findings, exit code, and severity counts.

If a download or the push returns **410** (job deleted) or **409** (job cancelled / re-popped),
the run is aborted, the temp files are cleaned up, and it returns to polling (or exits).

**Single-shot by default** — pops one job, runs it, pushes, and exits (ideal for a Scheduled Task
fired on an interval). Pass `-Loop` to poll continuously in one interactive session.

```powershell
# One job then exit:
.\Invoke-ValidationJob.ps1 -ServerBaseUrl https://opcua-nodeset-editor-02.azurewebsites.net -ApiKey $env:WORKER_ApiKey

# Continuous polling, installing the tool from a local package feed first:
.\Invoke-ValidationJob.ps1 -ToolPackageSource \\build\packages -Loop -PollIntervalSeconds 20
```

Runs on **Windows PowerShell 5.1 and PowerShell 7**.

### Parameters / configuration

Parameters override `appsettings.json` (the `Worker` section), which overrides built-in defaults.
`ApiKey` also falls back to the `WORKER_ApiKey` environment variable so the secret need not be
written to disk.

| Setting              | Meaning                                                                   |
|----------------------|---------------------------------------------------------------------------|
| `ServerBaseUrl`      | NodeSet Editor server base URL (no trailing `/api`).                      |
| `ApiKey`             | Shared secret; must match the server's `Validation:WorkerApiKey`. **Do not commit.** |
| `WorkerId`           | Identifier stamped on claimed jobs (defaults to the machine name).        |
| `ValidatorCommand`   | Validator command/path. Default: the global-tool command `Opc.Ua.SpecificationValidator`. Set to an explicit `.exe` to bypass provisioning. |
| `ToolPackageSource`  | Optional NuGet source (feed URL or local folder) to install the tool from. |
| `ToolVersion`        | Optional explicit tool version; omit for the latest prerelease.           |
| `WorkDir`            | Scratch directory for per-job artifacts (auto-cleaned).                    |

`-UpdateTool` forces an update of an already-installed tool; `-SkipInstall` fails instead of
installing if the tool is missing.

The server key is set as an Azure App Service application setting `Validation__WorkerApiKey`.
