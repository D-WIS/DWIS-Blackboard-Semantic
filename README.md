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
| `src/DWIS.Semantic.QueryRegistration` | the ported `QueryManager` (one entry per query text, owners, the stored answer as the snapshot of a new subscriber, diffs by row value, one evaluation at a time), `StoreQueryEvaluator` (a SELECT on the live dataset under the query limits; a cut answer counts as a failure), `QueryRoundObserver` (a round after every commit, remove and re-derive, its diffs to the `IQueryDiffSink`s) and `QueryDiffBroadcaster` (the sink that fans the diffs out to the gRPC streams and the server-sent events, one bounded queue per subscriber) |
| `src/DWIS.Semantic.Ingest` | the injection pipeline (`IngestPipeline`): prepare (names, triples, pre-processing pass, core-versus-rest split) and commit (the asserted graph; then the inferred graph, derived by the SPARQL rules of `inference-rules.sparql` from the staging graph over the closed vocabulary and units (decisions D-47 and D-64, M5.6: the subclass, subproperty, domain, range, inverse and someValuesFrom consequences, without the `DWISVerb` values and the `DWISNoun` types) and reduced by `inference-answer.sparql` to what the live data lacks; the view `/dwis-inf` is no longer asked; then the provenance; quarantine when rejected); manifest ids `{provider}/{name}` and the graph names of Appendix C.8 |
| `src/DWIS.Semantic.Host` | the service: HTTP routes under `/api/v1` (below), the five configuration layers of specification section 8 through `DWIS.Blackboard.Configuration`, the vocabulary read from the store at start-up, health |
| `tests/<project>.Tests` | one xUnit project per library; the store's and the host's tests run against a real Fuseki |

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

### The host

Routes of milestone 1 (specification 3.4), all under `/api/v1`:

| Route | What it does |
|---|---|
| `POST /manifests` | takes a manifest JSON (the `DWIS.API.DTO` shape, either namespace), runs the pipeline and writes the asserted, inferred and provenance graphs; answers with the report of Appendix C.9 (`accepted`, findings, `live`, statistics). Same manifest again: the stored report, nothing written; a live id with other content: replaced |
| `PUT /manifests/{provider}/{name}` | the same for a manifest whose own id must match the route (C.7, update); the graphs are replaced in one update once the new ones are computed, so a reader sees the old manifest, the new one or none, never a mix |
| `DELETE /manifests/{provider}/{name}` | drops the manifest's graphs, quarantine included (C.7, remove); 204, or 404 when the id is not live |
| `GET /manifests`, `GET /manifests/{provider}/{name}` | the live manifests from their provenance, each with its `origin` (`local`, or `hub:{group}:{sender}` for a manifest a peer sent through the hub, as the real-time process says at prepare; M5.3); one stored report |
| `GET /sparql?query=`, `POST /sparql` | SPARQL 1.1 protocol, read-only, with the time and row limits of `Query:TimeoutSeconds` and `Query:RowLimit`; `http://ddhub.no/` IRIs in the query are read as `http://d-wis.org/`; results JSON, CSV or XML for tables, Turtle or N-Triples for graphs by `Accept`; `DWIS-Truncated: true` when a limit cut the answer |
| `GET /vocabulary` | the version in force, the two graph IRIs, sizes, the policy |
| `PUT /vocabulary` | `{"version": "…"}`: switch to a version whose graphs the loader has put in the store (404 when they are not there), drop the previous version's graphs so that the inference rules read one vocabulary (a version loaded without its closure markers is refused, 404 with the reason), re-derive every live manifest from the manifest JSON kept with its provenance (prepare and commit again); answers with one line per manifest and the list of those that became core-only, the signal to load the previous version again and put it back (C.7) |
| `POST /queries` | `{"query": "SELECT …"}`: registers the query for the owner `http` (the same text is the same query) and answers with its id and full answer (`variables`, `rows`); 400 when it cannot be answered |
| `DELETE /queries/{id}` | releases the `http` owner; the query lives on while a stream owns it; 404 when the owner did not hold it |
| `GET /queries/{id}/events` | server-sent events: `snapshot` (the full answer) then one `diff` (`added`, `removed` rows) after every commit, remove or re-derive; the stream owns the query while it is open; 404 for an unknown id |
| `GET /health` | 200 when the store answers and the vocabulary is loaded, else 503 with the reason per check |

The gRPC service of specification 3.3 (`proto/semantic.proto` in DWIS-Blackboard-Contracts, decision D-10) is served on port 8081, HTTP/2 without TLS inside the Docker network (a `Grpc.Net.Client` channel on `http://semantic:8081`; TLS comes with milestone 5). It needs a port of its own: without TLS there is no protocol negotiation, so Kestrel serves HTTP/2 only on an endpoint that is HTTP/2 only (the `Kestrel` section of `appsettings.json`; the HTTP API stays on 8080). Since M3.3: `PrepareManifest` (validate, pre-reason, stage in memory; the variables to create with their validity flags; identical live content answers `already_live`), `CommitManifest` (the staged manifest goes live with the OPC UA endpoints the real-time process reports, kept in the graph `http://d-wis.org/endpoints`), `AbortManifest`, `RemoveManifest` (the endpoints go with the manifest), `ListManifests` (with the variables and the endpoints, for the rebuild after a restart), `Query` (a SELECT under the query limits), `GetResources` (individuals and sentences of the asserted graphs), `GetVocabulary`. A staged manifest waits `Staging:TimeLimitSeconds` (60) for its commit, then its entry is dropped and a commit answers NOT_FOUND. Since M3.4: `RegisterQuery` (a SELECT for an owner, the session id the real-time process reports, `real-time` when empty; the answer is the full snapshot: the stored answer of the query, evaluated only when the query has none), `UnregisterQuery`, `ReleaseOwner` (a session closed), `WatchQueries` (one stream per real-time process with every diff of every registered query after each change; a stream that falls 10,000 diffs behind is ended with RESOURCE_EXHAUSTED and the watcher registers again; since M5.1 a registration names its watcher and a stream opened with `fresh` first releases every owner that watcher registered before, as `ReleaseOwner` would, decision D-61). Nothing is persisted: after a restart of the service the registrations are gone and the streams end; the real-time process reconnects and registers again (R-SEM-7).

Settings (defaults in code, then `appsettings.json`, the shared file `DWIS_CONFIG_FILE`, environment variables `DWIS__Section__Key`, command line `--Section:Key=`): `Store:BaseUrl` (`http://localhost:3030/`), `Store:LiveDataset`, `Store:QuarantineDataset`, `Store:InferenceDataset`, `Vocabulary:Version` (`2026-10-14`: the graphs `http://d-wis.org/vocabulary/{version}` and `http://d-wis.org/units/{version}`, put there by the loader of the compose stack in `DWIS-Blackboard-Tests`), `Vocabulary:RetrySeconds`, `Validation:PolicyFile` (the policy of decisions D-45 and D-46; empty means every rule at its designed level), `Query:TimeoutSeconds`, `Query:RowLimit`, `Staging:TimeLimitSeconds`, `Inference:RulesFile` and `Inference:AnswerFile` (a SPARQL update file and a SPARQL CONSTRUCT file in place of the embedded `inference-rules.sparql` and `inference-answer.sparql` of `DWIS.Semantic.Ingest`, decision D-64; both name the staging graph as `{source}` and the scratch graph as `{scratch}`; empty means the embedded ones). The effective configuration with the source of each value is logged at start-up.

Run it against the compose stack of `DWIS-Blackboard-Tests` (Fuseki on `localhost:3030`, vocabulary loaded):

```bash
dotnet run --project src/DWIS.Semantic.Host -- --Validation:PolicyFile=../DWIS-Blackboard-Tests/fixtures/vocabulary/validation-policy.json
```

### Contract tests

`tests/DWIS.Semantic.Store.Tests` and `tests/DWIS.Semantic.Host.Tests` run against a real Fuseki with the Rig Edition datasets (Appendix G.1). The fixture looks, in this order, for:

1. `DWIS_FUSEKI_URL`: a running Fuseki, for example the compose stack of `DWIS-Blackboard-Tests` (`http://localhost:3030`). The tests write only graphs under `http://d-wis.org/test/` and delete them.
2. `DWIS_FUSEKI_IMAGE`: an image to start with Testcontainers. CI builds `digiwells/dwis.blackboard.fuseki` from `DWIS-Blackboard-Tests` and sets this, so the real image is what gets tested.
3. Otherwise a throwaway image built from `tests/DWIS.Semantic.Store.Tests/Fixtures/fuseki` (the Fuseki jar from Maven Central on the stock Temurin runtime, with a copy of the dataset configuration), started with Testcontainers. Needs Docker and network access.

Without any of these the contract tests are skipped with the reason in the test output; set `DWIS_FUSEKI_REQUIRED=true` to make them fail instead (CI does). The host tests load the vocabulary fixtures under a version of their own, start the host through the test server and run the milestone 1 acceptance of roadmap F.3 (the SmartConnect manifest accepted, the two queries with either prefix returning the same signal, the kilogram copy core-only with one error). They also inject the manifest library (`tests/DWIS.Semantic.Validation.Tests/Fixtures/manifests/library`, a copy of the one in `DWIS-Blackboard-Tests`) next to the SmartConnect manifest and compare every report with the expected one pinned there, as the smoke of the Tests repository does.

## How to work here

Trunk-based (specification 10.2 in the design repository): branch from `main` as `feature/…`, `fix/…` or `chore/…`, open a pull request early, one approval and green CI, squash merge. Releases are git tags `vX.Y.Z[-alpha.N]` on `main`. The Blackboard API stays at version 2; Rig Edition artefacts start at 1.0.0.

Work items live as issues with milestones M1 to M6 and are tracked on the organisation project [Rig Edition](https://github.com/orgs/D-WIS/projects/5).

## Licence

MIT, see [LICENSE](LICENSE).
