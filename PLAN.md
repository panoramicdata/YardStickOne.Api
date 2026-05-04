# PLAN.md — YardStickOne.Api

## Purpose

Provide a clean, testable .NET 10 library for recording and replaying RF signals using the Great Scott Gadgets YARD Stick One USB transceiver.

---

## Completed

- [x] Solution scaffolded (`YardStickOne.Api`, `YardStickOne.Demo`, `YardStickOne.Api.Tests`)
- [x] Case-correct rename: `YardstickOne` → `YardStickOne` (capital S) across all files
- [x] `IYardStickOneClient` public interface defined
- [x] `YardStickOneClient` implementation (partial class, LoggerMessage delegates)
- [x] `IUsbTransport` internal abstraction for testability
- [x] `LibUsbTransport` — LibUsbDotNet 2.2.75 implementation
- [x] `FakeUsbTransport` — in-memory fake for unit tests
- [x] `RfSignal`, `Modulation`, `YardStickOneClientOptions` models
- [x] Central Package Management (`Directory.Packages.props`)
- [x] `Directory.Build.props` — strict: `TreatWarningsAsErrors`, nullable, implicit usings, CA analysis
- [x] `nuget.config` — isolates from private upstream feeds
- [x] Nerdbank.GitVersioning 3.9.50 — versioned NuGet packages from git history
- [x] 18 unit tests — all passing
- [x] Full solution build — 0 warnings, 0 errors
- [x] `README.md` updated — Codacy badge, device table, Installation section
- [x] `PLAN.md` created (this file)
- [x] `DESIGN.md` created
- [x] Initial commit to GitHub (public repo)
- [x] Codacy badge updated with real slug

---

## Planned

### Near-term

- [ ] Publish to NuGet.org — set up GitHub Actions workflow with `NUGET_API_KEY` secret
- [ ] GitHub Actions CI — build + test on push/PR
- [ ] Hardware integration test — connect physical YARD Stick One and smoke-test `ConfigureAsync` + `ReceiveAsync`

### Somfy RTS protocol support

- [ ] Implement `SomfyRtsCodec` — encode/decode 433.92 MHz Somfy RTS frames
  - Frame: preamble (1 wakeup pulse), hardware sync (2-7 pulses), software sync, encrypted data, inter-frame gap
  - Rolling code: XOR + rolling-code counter per remote ID
- [ ] Add `ISomfyRtsCodec` to allow mocked codec in tests
- [ ] Demo: record a live Somfy remote and decode/display the frame

### Future

- [ ] Support 868 MHz sub-band (Somfy io homeserver)
- [ ] Key Management helper — persist captured rolling codes per remote
- [ ] Sample: home-automation integration (e.g. open/close blinds via HTTP)
