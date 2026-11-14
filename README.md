# DWIS Blackboard Rig Edition: semantic service

Part of DWIS Blackboard Rig Edition (GitHub topic `dwis-blackboard-rig-edition`); design and decisions in [DWIS-Blackboard-Design](https://github.com/D-WIS/DWIS-Blackboard-Design).

**Holds:** the semantic service: the `DWIS.Semantic.*` libraries (manifest, validation, store, query registration), the host, unit and contract tests, the Dockerfile.

**Produces:** image `digiwells/dwis.blackboard.semantic` (`sha-<commit>` and `edge` from every merge to `main`, versioned from tags); NuGet packages for the libraries others reuse.

**Depends on:** `DWIS-Blackboard-Contracts`; Apache Jena Fuseki as the store (image built in `DWIS-Blackboard-Tests`).

## Layout

One solution, `DWIS.Blackboard.Semantic.slnx`, .NET 10, with the libraries of specification 3.2:

| Project | Responsibility |
|---|---|
| `src/DWIS.Semantic.Manifest` | manifest JSON to RDF, namespace translation, validity flag completion, core-versus-rest split |
| `src/DWIS.Semantic.Validation` | pre-processing pass: term checks, domain and range, SHACL with dotNetRDF, unit dimensions, the validation policy; the rule catalogue is its [README](src/DWIS.Semantic.Validation/README.md) |
| `src/DWIS.Semantic.Store` | store client (`ITripleStore`: named graphs, SPARQL query with time and row limits, update, SHACL endpoint, health) behind a store-agnostic interface; `FusekiStore` first |
| `src/DWIS.Semantic.QueryRegistration` | query registration: ownership, snapshot, diff, re-evaluation (milestone 3) |
| `src/DWIS.Semantic.Ingest` | the injection pipeline: prepare, stage, commit, abort, remove, re-derive |
| `src/DWIS.Semantic.Host` | the service: gRPC and HTTP endpoints under `/api/v1`, configuration, health, metrics |
| `tests/<project>.Tests` | one xUnit project per library; contract tests against a real Fuseki come with the store |

Shared settings live in `Directory.Build.props` (nullable, analyzers, warnings as errors, .NET 10) and `Directory.Packages.props` (central package versions). Tests use xUnit v3 on the Microsoft Testing Platform (opt-in in `global.json`) with FluentAssertions 7.

## Building and running

```bash
dotnet build DWIS.Blackboard.Semantic.slnx -c Release
```

```bash
dotnet test DWIS.Blackboard.Semantic.slnx -c Release --no-build --report-trx --results-directory TestResults
```

```bash
docker build -t digiwells/dwis.blackboard.semantic:dev .
```

### Contract tests

`tests/DWIS.Semantic.Store.Tests` runs against a real Fuseki with the Rig Edition datasets (Appendix G.1). The fixture looks, in this order, for:

1. `DWIS_FUSEKI_URL`: a running Fuseki, for example the compose stack of `DWIS-Blackboard-Tests` (`http://localhost:3030`). The tests write only graphs under `http://d-wis.org/test/` and delete them.
2. `DWIS_FUSEKI_IMAGE`: an image to start with Testcontainers. CI builds `digiwells/dwis.blackboard.fuseki` from `DWIS-Blackboard-Tests` and sets this, so the real image is what gets tested.
3. Otherwise a throwaway image built from `tests/DWIS.Semantic.Store.Tests/Fixtures/fuseki` (the Fuseki jar from Maven Central on the stock Temurin runtime, with a copy of the dataset configuration), started with Testcontainers. Needs Docker and network access.

Without any of these the contract tests are skipped with the reason in the test output; set `DWIS_FUSEKI_REQUIRED=true` to make them fail instead (CI does).

The host alone answers `GET /api/v1/health`; the full service is started from the `compose.yaml` in `DWIS-Blackboard-Tests` together with Fuseki.

## How to work here

Trunk-based (specification 10.2 in the design repository): branch from `main` as `feature/…`, `fix/…` or `chore/…`, open a pull request early, one approval and green CI, squash merge. Releases are git tags `vX.Y.Z[-alpha.N]` on `main`. The Blackboard API stays at version 2; Rig Edition artefacts start at 1.0.0.

Work items live as issues with milestones M1 to M6 and are tracked on the organisation project [Rig Edition](https://github.com/orgs/D-WIS/projects/5).

## Licence

MIT, see [LICENSE](LICENSE).
