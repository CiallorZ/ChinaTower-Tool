# Changelog

All notable changes to LinkForge are recorded here.

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
