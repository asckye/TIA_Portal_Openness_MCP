# Offline parity fixture

This non-test library supplies the existing Foundation parity oracle. It does not build or reference `src/Engine` or `TiaMcp.Engine.Tests`.
`Oracle/` freezes eight dispatcher, service-registration, release-selection, verification-DTO and software-lookup files from the P8-04k1 launch source. They are fixture infrastructure, never production sources or successor subjects. The original engine tests still compile and test the original implementations unchanged. `oracle-sources.json` records their byte identities.

The shared/host tools under observation continue to compile from their retained production source paths. The old `enginefixture` alias is preserved to avoid edits to Foundation assertions. The parity scenarios keep their explicit catalogs and service registration. The reset catalog contains the four infrastructure probe types; the unused HMI probe targets are omitted. No parity scenario calls those reset targets.

The `*Data.cs` and `*Fixture.cs` files contain the unchanged nested fakes, data sources and response shapes needed by more than one test assembly. Test entry methods execute in the registered test projects. `OfflineFixtureSources.props` supplies the shared production-source inventory and rejects a production-engine compile or assembly reference.

Do not add new tests of the frozen dispatcher here. D5 rewrites target FoundationHost, EngineHost and PlcWorker; see the per-test mapping in the P8-04k1 result.
