# Changelog

All notable changes to LinkForge are recorded here.

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
- Full test suite: see `docs/BUILD_LOG.md` for the final V1.0 count.
- Installer: silent install, repeat-install upgrade, application startup/close,
  uninstall and user-data preservation.
- `win-x86` self-contained application starts and closes with exit code `0`.
- Clean-checkout build/test verification is recorded in `docs/BUILD_LOG.md`.

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
