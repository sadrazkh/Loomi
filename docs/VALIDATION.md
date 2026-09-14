# Validation — 2026-09-14

Verified locally on Windows with .NET SDK 10.0.401, .NET runtime 10.0.12 and Node 22.13.0:

- `dotnet test --no-restore`: **5 passed, 0 failed**.
  - Anonymous API, image and noVNC access denied; missing antiforgery rejected.
  - Generation admission stores parent lineage and operation; active projects cannot be deleted; pending images cannot be parents.
  - Authenticated image delivery, safe path resolution, invalid image rejection, private response DTOs, no-store and logout enforcement.
  - Real Chromium against an intercepted local fixture: Generate, Edit, Branch, reference-file upload, conversation selection, image extraction and reset.
  - Timeout preserves the observed conversation URL without retrying prompt submission.
- `npm run build`: production Vue bundle built successfully.
- `npm audit --omit=dev`: no known production dependency vulnerabilities reported.
- `dotnet ef migrations has-pending-model-changes`: no model drift.
- `dotnet publish -c Release`: succeeded; the output contains bundled UI and excludes Client/node_modules and frontend source.
- `node scripts/ui-smoke.cjs`: passed against the real local ASP.NET host. Checked cookie login, SignalR connection and negotiation, English/Persian, RTL, dark/light, 390px mobile width, modal Escape behavior and preference persistence. No JavaScript runtime errors.
- Visually inspected the desktop workspace and Persian mobile settings screenshots in `test-results`.

Not validated here:

- Live ChatGPT login or real account image generation. No user session was provided. The defaults for DOM selectors need acceptance testing against the user's account.
- Docker image build, Linux desktop services and end-to-end noVNC login. Docker is not installed on this host. The repository CI workflow includes a Linux Docker build, but that workflow has not been run here.
- Multi-instance operation, S3/R2 and additional users are outside this first version.

The smoke-test access key is only an ephemeral local test value. No production key or ChatGPT credentials are included in the source.
