# Changelog

All notable changes to LinkForge are recorded here.

## 1.2.0 - 2026-10-24

### Fixed

- In-place installer upgrades now back up the existing program directory before
  changing files. If an upgrade fails, the installer restores the previous
  executable, DLLs, uninstaller and shortcuts before exiting with a failure
  code; an incomplete restore keeps the backup for manual recovery.
- Successful upgrades remove the temporary backup, and normal uninstall still
  preserves `%LOCALAPPDATA%\LinkForge` user data.

### Verified

- Debug and formal Release builds: 0 warnings, 0 errors.
- Full test suite: 1049/1049 passed.
- Windows CI passed:
  [Run 40032659744](https://github.com/CiallorZ/ChinaTower-Tool/actions/runs/40032659744).
- Clean checkout of commit `5486fbee8c52` rebuilt and passed all tests.
- Application startup and close smoke test: exit code 0.
- Controlled failure injection locked `LinkForge.App.dll` during an upgrade:
  the installer exited non-zero while the previous EXE/DLL hashes,
  install-local marker, uninstaller and Start Menu shortcut remained available;
  the recovered application started and closed with exit code 0.
- A subsequent normal in-place upgrade succeeded, removed the backup directory,
  and the installed application started and closed with exit code 0.
- The same failure recovery passed when upgrading from the published `v1.0.0`
  installer to `v1.2.0`. Both x64 and x86 portable payloads started and closed
  with exit code 0.
- Published as
  [LinkForge v1.2.0](https://github.com/CiallorZ/ChinaTower-Tool/releases/tag/v1.2.0)
  with eight uploaded assets and matching SHA-256 digests.

### Release boundaries

- The installer and portable packages remain **unsigned**.
- This release closes installer upgrade-failure recovery only. It does not add
  code signing, automatic updates, Windows 10 physical-device acceptance,
  vendor SDKs, packet capture, security scanning or offensive tooling.

## 1.1.0 - 2026-10-24

### Added

- MQTT 3.1.1 / 5.0 protocol selection in the MQTT workbench. The selected
  protocol is passed to the real MQTTnet transport and recorded in copied
  summaries; MQTT 3.1.1 remains the default.
- Versioned `.lfpkg` project packages that export and import projects,
  environments and assets without plaintext credentials. Imports use strict
  schema validation and a single SQLite transaction; conflicts or failures
  roll back the whole package.
- Windows CurrentUser DPAPI credential storage for asset passwords. Each entry
  is stored as a separate DPAPI-protected file under the local application data
  directory; project packages, logs, history, clipboard and reports contain
  reference keys only.
- Windows CI for Release build, full test gate and an unsigned `win-x64`
  diagnostic portable package.

### Verified

- Full solution build: 0 warnings, 0 errors.
- Full test suite: 1049/1049 passed.
- Application startup and close smoke test: exit code 0.
- Formal release gate passed for commit `64c5993`: unsigned `win-x64` and
  `win-x86` portable packages plus the per-user installer were generated with
  published SHA-256 checksums.
- Installer silent install, repeat-install upgrade, application startup/close,
  uninstall and user-data preservation passed. The standalone `win-x86`
  payload started and closed with exit code 0.
- Windows CI passed for the release commit; a clean checkout Release build and
  all 1049 tests also passed.

### Security and release boundaries

- The DPAPI credential store protects secrets for the current Windows user on
  the current machine. It does not provide cross-machine sync or protection
  from malware already running as the same user.
- The installer and portable packages remain **unsigned**. SmartScreen may
  warn; Authenticode status is reported truthfully.
- Vendor SDKs, packet capture, security scanning and offensive tooling remain
  outside this release.

## 1.0.0 - 2026-10-24

### Added

- Windows installer built with Inno Setup: one per-user package embeds
  self-contained `win-x64` and `win-x86` payloads and selects the matching
  architecture at install time.
- About/release page with product version, Git commit, runtime, OS, process
  architecture, runtime identifier, install shape, real local paths and
  Authenticode trust-chain status. It does not claim that update checks or
  supply-chain audits have been performed.
- `build-release.ps1`, installer build/verification scripts, a vendored MIT
  Simplified Chinese Inno Setup language pack, source provenance and notice.
- Portable self-contained packages for both `win-x64` and `win-x86`.
- Windows manifest declaring Windows 10/11 compatibility, `asInvoker`,
  PerMonitorV2 DPI behavior and long-path awareness.
- Standard ONVIF discovery, device information, capabilities, profiles and
  stream URI diagnostics, with TLS validation and credential redaction.

### Verified

- Full solution build: 0 warnings, 0 errors.
- Full test suite: 1003/1003 passed.
- Installer: silent install, repeat-install upgrade, application startup/close,
  uninstall and user-data preservation.
- `win-x86` self-contained application starts and closes with exit code `0`.
- Clean-checkout Release build and 1003/1003 tests passed for commit
  `879de49f4640`.

### Security and release boundaries

- The V1.0 installer and portable packages are **unsigned**. Authenticode
  status is reported truthfully and SmartScreen may warn.
- Uninstall removes application files and shortcuts; `%LOCALAPPDATA%\LinkForge`
  user data is intentionally retained.
- The installer is not MSIX/WiX and has no automatic updater or automatic
  rollback.
- Vendor SDKs (Hikvision/Dahua), packet capture, port/security scanning and
  offensive tooling are not included. Their licensing or authorization
  boundaries remain open.

## 0.5.0-preview.1 - 2026-10-22

### Added

- Project, environment and asset storage backed by SQLite + EF Core migrations.
- Structured JSONL logs with write-time redaction, level/search filtering,
  visible-row copy and export from the shell log panel.
- Real network diagnostics for Ping, TCP, DNS, local interfaces/routes and
  Windows `tracert`.
- HTTP/API workbench with request history, headers, body limits, cancellation,
  redacted copy and export.
- MQTT connect/subscribe/publish workbench with TLS validation and a local
  broker-backed integration test suite.
- RTSP signaling diagnostics for OPTIONS, DESCRIBE and SDP; media setup,
  decode and playback are explicitly not implemented.
- Offline self-contained HTML report generation with pre-write sensitive-data
  scanning.
- Developer tools for JSON, URL, Base64, Unix timestamps, UUID and JWT payload
  decoding.
- Out-of-process plugin hosting with strict manifest validation, parameter
  allowlists, crash isolation and host-side redaction.
- Deterministic release version metadata, portable win-x64 publish script,
  dependency inventory, CycloneDX SBOM and SHA-256 checksums.
- Clean-checkout verification script and an end-to-end component workflow test
  covering project, asset, diagnosis, log and report.

### Security

- Credentials are redacted before UI display, disk logging, history, clipboard,
  export and report generation.
- TLS certificate validation remains enabled for HTTP/API and MQTT.
- Vendor camera SDKs, packet capture, security scanning and offensive tooling
  are not included.

### Known limitations

- The portable package is not code-signed and is not an installer.
- Windows 10 hardware and high-DPI acceptance are still pending.
- x86 runtime validation is blocked; only x64 is published in this iteration.
- Vendor SDKs, ONVIF, RTP/media decoding, packet capture and authorized
  security scanning remain outside the delivered scope.
