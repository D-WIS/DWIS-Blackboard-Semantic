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
| `src/DWIS.Semantic.Validation` | pre-processing pass: term checks, domain and range, SHACL with dotNetRDF, report model |
| `src/DWIS.Semantic.Store` | store client (Graph Store Protocol, SPARQL, SHACL endpoint, health) behind a store-agnostic interface; Fuseki first |
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

The host alone answers `GET /api/v1/health`; the full service is started from the `compose.yaml` in `DWIS-Blackboard-Tests` together with Fuseki.

## How to work here

Trunk-based (specification 10.2 in the design repository): branch from `main` as `feature/…`, `fix/…` or `chore/…`, open a pull request early, one approval and green CI, squash merge. Releases are git tags `vX.Y.Z[-alpha.N]` on `main`. The Blackboard API stays at version 2; Rig Edition artefacts start at 1.0.0.

Work items live as issues with milestones M1 to M6 and are tracked on the organisation project [Rig Edition](https://github.com/orgs/D-WIS/projects/5).

## Licence

MIT, see [LICENSE](LICENSE).
