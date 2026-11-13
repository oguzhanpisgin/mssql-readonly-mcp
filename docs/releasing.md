# Releasing

For the maintainer. The workflow is `.github/workflows/release.yml`; it builds the archives and bundles with
`packaging/build-release.ps1` and writes `SHA256SUMS` itself.

## What the workflow does

Triggers: a pushed tag `vX.Y.Z` (three numbers, nothing after them) and a run started by hand
(`workflow_dispatch`). A push to a branch or a pull request starts nothing.

Job `build` (read-only token, no npm code), both triggers:

1. Tag runs: stops with an error if the tagged commit is not on `main`, or if `docs/releases/vX.Y.Z.md` is
   missing on that commit.
2. Builds the solution with warnings as errors and runs the Gate and Audit unit tests. The integration tests are not
   run here; they run in `ci.yml`.
3. Publishes the server self-contained (the .NET runtime is included; not trimmed, not Native AOT) for `win-x64`,
   `linux-x64` and `osx-arm64`, with the version from the tag (`0.0.0-dev` for a run by hand), and packs one
   archive per platform: one folder with the build, `LICENSE` and `README.txt`.
4. Uploads the archives (artifact `release-archives`), the publish output (artifact `publish-output`) and, on tag
   runs, the release notes (artifact `release-notes`). The SHA-256 values of the archives and of the release notes
   leave the job only as its outputs `archives` and `notes`, not as artifacts, so the `bundle` job cannot replace
   them; a replaced archive or notes artifact makes the `release` job stop.

Job `bundle` (read-only token), both triggers: packs one MCPB bundle per platform from the publish output of
`build` with the MCPB CLI pinned in `packaging/mcpb/package-lock.json` (`npm ci --ignore-scripts`), and uploads only
the `.mcpb` files (artifact `release-bundles`). It is the only job that runs npm code. The bundles are therefore
only as trustworthy as that pinned CLI and its packages; their attestation shows that this workflow built them, not
that the CLI left the server files unchanged. `packaging/mcpb/package.json` overrides two packages under the CLI, so
the CLI runs with dependency versions its author did not pin: `tmp` 0.2.7 where its tree asks for 0.0.33, and `zod`
4.6.7 where it asks for 3.x, because no version in the ranges it asks for has the fix for their known
vulnerabilities. With them, `npm audit --package-lock-only` on 2026-11-13 reported 0 vulnerabilities, and bundles
packed on Windows with the old and the new lock file held the same files with the same content
([Security testing](security-testing.md#dependency-alerts)).

Job `release`, tag runs only (the one job with write permissions: `contents`, `id-token`, `attestations`):

1. Stops unless the download holds exactly six files: the three archives and the three bundles.
2. Checks the archives against the `build` job's output `archives` and the release notes against its output `notes`.
3. Stops if a release for the tag already exists.
4. Writes `SHA256SUMS` over the six files and creates a build provenance attestation for each of them.
5. Creates a **draft** release for the tag with seven files and the notes from `docs/releases/vX.Y.Z.md`:

   | File | Content |
   | --- | --- |
   | `mssql-readonly-mcp-X.Y.Z-win-x64.zip` | One folder: the build, `LICENSE`, `README.txt` |
   | `mssql-readonly-mcp-X.Y.Z-linux-x64.tar.gz` | The same for Linux x64 |
   | `mssql-readonly-mcp-X.Y.Z-osx-arm64.tar.gz` | The same for macOS on Apple silicon |
   | `mssql-readonly-mcp-X.Y.Z-<rid>.mcpb` | MCPB bundle per platform: `manifest.json`, `LICENSE`, the build under `server/` |
   | `SHA256SUMS` | SHA-256 of the six files above |

The workflow never publishes a release.

## Steps

1. **Write the release notes** in `docs/releases/vX.Y.Z.md` on `main`, and set the version in the download and
   login blocks of the README (`$v = 'X.Y.Z'`) and of [Installing](install.md) (`v=X.Y.Z`): the blocks name the
   release's files and the tag of the login script.
2. **CI green on the commit that holds the notes.** `ci.yml` (build, unit tests, integration tests on three SQL
   Server versions) has passed on that commit.
3. **Run the workflow by hand once** on `main` at that commit (Actions, `release`, "Run workflow"). Download the
   artifacts `release-archives` and `release-bundles` and look at them: three archives and three bundles, version
   `0.0.0-dev` in the names. Unpack the archive for your platform and start `mssql-readonly-mcp` once without
   configuration: it should exit with code 2 and the line
   `mssql-readonly-mcp: configuration error: MSSQL_CONNECTION_STRING is required.`
4. **Tag and push the tag**:

   ```powershell
   git tag -a vX.Y.Z -m "vX.Y.Z" <commit on main>
   git push origin vX.Y.Z
   ```

5. **Read the draft** on the Releases page: seven assets (plus GitHub's own two source code archives), the notes,
   the tag.
6. **Publish** the draft by hand on the Releases page.

If the `build` job stops at step 1, nothing was created. Fix the cause on `main`, delete the failed tag
(`git push origin --delete vX.Y.Z`, `git tag -d vX.Y.Z`) and tag the fixed commit, or use the next version.
If a run stopped after the draft was created, delete that partial draft on the Releases page before running again.

Protect `v*` tags with a repository ruleset (creation by the maintainer only); the main check runs from the workflow
file of the tagged commit. This ruleset is not set up yet.

## Checked by hand, not by the workflow

- That `ci.yml` is green on the tagged commit.
- The release notes and the draft before it is published.
- That the archives start on Windows, Linux and macOS.

## Not done by this workflow

- No code signing: the Windows executable is not Authenticode-signed; the macOS executable is not notarized (a
  local build showed only the ad-hoc signature the .NET SDK writes; see "Not yet verified"); the bundles are not
  signed with `mcpb sign`.
- No builds for other platforms (for example Linux arm64, macOS x64, Windows arm64).
- No entry in the MCP registry or any other listing.

`.github/dependabot.yml` is configured to propose updates of the pinned MCPB CLI in `packaging/mcpb`. Dependabot
does not edit the `overrides` there: a later fix in `tmp` or `zod`, and any update of the CLI, need the overrides
checked by hand, because npm applies an override silently, even where the CLI asks for another version.

## Not yet verified

Checked only on a Windows machine with the .NET SDK installed; not yet shown:

- The first run of this workflow on GitHub, by hand and for a tag: the `main` check against `origin/main`, the
  attestation step and the draft release.
- That the executable bit is kept in the `.tar.gz` archives and `.mcpb` bundles built on the Linux runner.
- `gh attestation verify` on a downloaded file.
- Starting the Linux build on Linux and the macOS build on a Mac, including whether Gatekeeper starts a downloaded
  executable that is not notarized. The ad-hoc signature was checked on a build made on Windows only.
- Starting a build on a machine with no .NET installed at all.
- Installing a `.mcpb` bundle in any MCPB host other than the Microsoft Store build of Claude Desktop on Windows,
  or on any other platform. On
  Windows 11 on 2026-11-13 the win-x64 bundle was installed in the Microsoft Store build 5.11492.2.0 of Claude
  Desktop; a tool call typed in its chat was not shown (MANIFEST.md says hosts add `.exe` on Windows themselves; the
  manifest names `mssql-readonly-mcp.exe` in full, see `packaging/build-release.ps1`).
- That Dependabot opens update pull requests for `packaging/mcpb`.

## How a user checks a download

Compare the file's SHA-256 with its line in `SHA256SUMS`:

```powershell
(Get-FileHash .\mssql-readonly-mcp-X.Y.Z-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant()
```

On Linux: `sha256sum --check --ignore-missing SHA256SUMS` in the download folder.

The release job attests every file in `SHA256SUMS`. The check below is meant for that attestation; it has not been
run against a real release yet (see "Not yet verified"). It needs the GitHub CLI:

```powershell
gh attestation verify .\mssql-readonly-mcp-X.Y.Z-win-x64.zip --repo oguzhanpisgin/mssql-readonly-mcp
```
