# Contributing

Build on Windows with the .NET 9 SDK using `./Build.ps1`. The build must complete and the offline self-tests must pass before submitting a pull request. The build and tests do not require a headset, change audio defaults, or register a startup task.

`src/InzoneMicSwitch/ProtocolTests.cs` contains synthetic report vectors covering framing, checksum validation, chunk boundaries, fragmentation, malformed input, and repeated transaction IDs. `GestureDetector.RunSelfTests()` checks volume/mute mapping, baseline behavior, timing, cooldown, and resets. Add tests for new parser or gesture behavior. Tests use an explicit `--self-test` entry point rather than a separate test framework.

Keep live hardware checks separate from offline tests. Report Windows version, Buds firmware if known, connection mode, steps performed, and observed results. Live checks should cover both switch directions, all three capture roles, disconnect/reconnect, and behavior with INZONE Hub closed. Changing capture defaults affects other applications, so restore your preferred input afterward.

Do not add Sony installers, DLLs, decompiled source, firmware, or unreviewed capture logs. Use small synthetic protocol fixtures. Remove local usernames, paths, machine names, and device endpoint identifiers from issue reports where unnecessary. New third-party code or dependencies must retain their license and attribution.

The project uses MIT; contributions are submitted under that license. Keep changes focused and explain the behavior and validation in the pull request.
