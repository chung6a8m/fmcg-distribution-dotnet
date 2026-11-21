# LAB-D01 operator guide

LAB-D01 measures the Windows laptop and its selected local Docker Desktop WSL2
Linux daemon. The result describes one observation window and records
`authorization: NOT_EVALUATED`. The Docker daemon, host and WSL2 remain a shared
privilege and failure domain. APP-02B retains `FOUNDATION_ONLY / NO_GO` and
the original physical gates retain `NOT_VERIFIED`.

## Plan and custody

1. Use a clean, reviewed checkout and record its full `git rev-parse HEAD` in
   `sourceHead`. The entry point reads the checkout state before and after
   collection.
2. Copy [the example](host-plan.example.json) to a private local plan directory
   outside Git. Set `evidenceRoot` to a separate, existing directory on fixed
   NTFS/ReFS storage with ACLs restricted to the operator and evidence custodians.
   Use a new child directory for each attempt. The operator controls ACLs and
   custody of the plan, output directory and verification receipts.
3. Set the proposed project namespace, exact volume and network names, private
   IPv4 subnets, host TCP/UDP ports and resource floors. Use the actual
   proposed D02 budget. The example reserves one high port and uses separate
   APP, DATA and VAULT subnets. This contract inventories existing binds and
   proposes named volumes.
4. For each image role, replace the zero digest with an exact publisher-verified
   OCI digest. Verify the publisher and applicable SQL license, then retain the
   verification proof under operator custody. Put the receipt defined below in
   a `receipts` directory beside the plan. Name the file
   `<sha256-of-receipt-file>.json` and set `publisherReceiptSha256` to the same hash.
   D01 verifies the receipt's pinned repository, digest and platform, then checks
   the matching cached Linux/amd64 image. The APP role covers the ASP.NET base
   image input. Later increments establish the identity of built APP service artifacts.
5. Set `hostRef` and `engineRef` to the references for the intended laptop and
   daemon. For first-time identity discovery, leave the example identities as
   zeros, run against the intended local Docker Desktop instance and expect
   `BLOCKED`. Confirm the observed references with the operator, then pin them
   in a new plan and use a fresh output directory. An engine reset or migration
   can require a new pin.

Use [the plan schema](../../../tools/app02b_lab/schemas/lab-docker-host-plan-v1.json)
and include only the prescribed fields. Keep credentials, tenant data and raw
identities out of the plan. The zero image placeholders deliberately fail
validation.

Each publisher receipt contains exactly these four fields. Use the image's
actual `reference` and put the hash of the retained publisher proof in
`proofSha256`. The signature and license decisions remain with the custodian.

```json
{
  "schema": "APP02B-LAB-DOCKER-PUBLISHER-RECEIPT-V1",
  "reference": "mcr.microsoft.com/mssql/server@sha256:<reviewed-digest>",
  "platform": "linux/amd64",
  "proofSha256": "<sha256-of-operator-held-publisher-proof>"
}
```

## Run on the intended laptop

Use 64-bit PowerShell 7 and a trusted local toolchain. The
[toolchain floor file](toolchain.lock.json) records the supported version
floors for this increment. Start with the desired daemon already running,
`desktop-linux` selected and Docker/Compose overrides clear in the current
shell. D01 resolves the CLI from the running Desktop installation and expects
Docker's packaged plugins or the system-managed plugin directory.

```powershell
pwsh -NoProfile -File .\scripts\lab\Test-DockerWslPrerequisites.ps1 -ConfigFile C:\PrivateLabPlan\docker-host.json -OutputDirectory C:\LabEvidence\Docker\d01-attempt-01
$LASTEXITCODE
```

| Exit | Status | Operator disposition |
| --- | --- | --- |
| 0 | `READY` | Retain the attempt for scoped D02 review. Obtain separate authorization for any later host Apply. |
| 1 | `COLLECTION_FAILED` | Correct the invalid plan or custody path, or resolve the collection/permission failure. A valid plan with a collection failure can produce a redacted receipt. |
| 2 | `BLOCKED` | Resolve every required `FAIL` or `UNKNOWN`, then rerun with a fresh attempt directory. |

| Check or issue code | What to verify |
| --- | --- |
| `SOURCE`, `HOST_IDENTITY`, `ENGINE_IDENTITY` | The clean exact checkout, intended laptop and pinned daemon. |
| `DOCKER_ENV_OVERRIDE`, `UNSAFE_CONTEXT`, `LOCAL_CONTEXT` | The invoking shell, selected `desktop-linux` context and local Docker Desktop named pipe. |
| `UNSUPPORTED_DESKTOP_LAYOUT`, `UNTRUSTED_PLUGIN_CONFIGURATION` | A running Desktop installation with its packaged CLI and supported plugin locations. |
| `DAEMON_UNAVAILABLE`, `LINUX_ENGINE`, `WSL2_BACKEND` | Running Docker Desktop Linux engine with the running `docker-desktop` distro in WSL2 mode and a WSL2 engine kernel. |
| `SQL_X64`, `IMAGE_*` | Native x64 Windows and x86-64 Linux engine, cached Linux/amd64 images, exact publisher digest and external provenance record. |
| `HOST_MEMORY`, `WSL_MEMORY`, `DATA_STORAGE` | Free Windows RAM, daemon-visible WSL2 RAM/CPU, recognized Docker root/driver, attached VHD capacity and free bytes on the backing Windows disk. |
| `HOST_DNS`, `HOST_CLOCK` | Windows DNS answers for MCR and Docker Hub, and Windows-to-daemon UTC agreement within 30 seconds. |
| `PROJECT`, `NETWORKS`, `PORTS`, `VOLUMES` | Distinct names and subnets across Windows IPv4 routes and Docker networks, unused ports including Windows excluded ranges and existing Docker bindings, and a fresh volume namespace. |
| `HOST_ACCESS_DENIED`, `COLLECTION_ERROR` | Read access to Windows inventory, disk metadata and Docker read APIs. Inspect diagnostics locally under operator custody. |

The Windows disk check supports the current Docker-managed `docker_data.vhdx`
layout and the conventional `/var/lib/docker` root with overlay storage. The
Windows Storage provider must confirm the configured VHD is attached.
Unrecognized layouts, including older `docker-desktop-data` storage, report
`UNKNOWN`. D01 measures the Windows volume and the virtual disk's capacity.
Linux filesystem free space and quotas need separate scoped measurement before
using the rehearsal storage. Port and route observations are snapshots.
The tool performs DNS lookups and local read probes. Its operation set covers
inventory and publication of the protected reports.

Each published attempt contains [versioned redacted JSON](../../../tools/app02b_lab/schemas/lab-docker-host-preflight-v1.json),
readable Markdown and `SHA256SUMS` for both files. Verify the hashes against the
protected copies before handing off the source SHA, attempt reference, digest
and disposition. Host, engine and storage references are opaque SHA-256
identifiers. Raw endpoint, identifiers, network addresses, names, paths and
native output stay out of the reports. A D01 receipt travels separately from
LAB-E05 admission and B.9 evidence.

## References

- [Docker Desktop WSL2 backend](https://docs.docker.com/desktop/features/wsl/)
- [Docker Desktop data disk](https://docs.docker.com/desktop/settings-and-maintenance/backup-and-restore/)
- [Microsoft Windows disk image inventory](https://learn.microsoft.com/en-us/powershell/module/storage/get-diskimage)
- [Docker context inspection](https://docs.docker.com/reference/cli/docker/context/inspect/)
- [Microsoft WSL commands](https://learn.microsoft.com/en-us/windows/wsl/basic-commands)
- [Microsoft SQL Server container architecture](https://learn.microsoft.com/en-us/sql/linux/containers/deploy)
