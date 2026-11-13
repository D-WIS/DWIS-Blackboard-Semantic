# DWIS Blackboard Rig Edition: semantic service

Part of DWIS Blackboard Rig Edition (GitHub topic `dwis-blackboard-rig-edition`); design and decisions in [DWIS-Blackboard-Design](https://github.com/D-WIS/DWIS-Blackboard-Design).

**Holds:** the semantic service: the `DWIS.Semantic.*` libraries (manifest, validation, store, query registration), the host, unit and contract tests, the Dockerfile.

**Produces:** image `digiwells/dwis.blackboard.semantic` (`sha-<commit>` and `edge` from every merge to `main`, versioned from tags); NuGet packages for the libraries others reuse.

**Depends on:** `DWIS-Blackboard-Contracts`; Apache Jena Fuseki as the store (image built in `DWIS-Blackboard-Tests`).

## Running

Not yet: the solution layout arrives with milestone 1 (issue M1.1). The service is started from the `compose.yaml` in `DWIS-Blackboard-Tests` together with Fuseki.

## How to work here

Trunk-based (specification 10.2 in the design repository): branch from `main` as `feature/…`, `fix/…` or `chore/…`, open a pull request early, one approval and green CI, squash merge. Releases are git tags `vX.Y.Z[-alpha.N]` on `main`. The Blackboard API stays at version 2; Rig Edition artefacts start at 1.0.0.

Work items live as issues with milestones M1 to M6 and are tracked on the organisation project [Rig Edition](https://github.com/orgs/D-WIS/projects/5).

## Licence

MIT, see [LICENSE](LICENSE).
