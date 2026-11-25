# Manifest library (copy)

A copy of `fixtures/manifests/library/` of the Tests repository (`D-WIS/DWIS-Blackboard-Tests`, commit a96dfae of 21 November 2026): five real manifests, one folder each, with the stable part of the validation report the semantic service is expected to return for it on the vocabulary of 14 October 2026. The origin, licence and shape of every manifest are in that repository's `fixtures/manifests/library/README.md`.

The host test `ManifestLibraryTests` of `tests/DWIS.Semantic.Host.Tests` injects every manifest next to the SmartConnect manifest and compares the report the way `tools/library/check-library.py` of the Tests repository does in the smoke (M5.F, Semantic #41). The same expected reports are pinned on both sides: a change of the service or the vocabulary that changes a report is recorded again, on purpose, in both repositories in the same step (`check-library.py --record` against the compose stack, then copy the files here).
