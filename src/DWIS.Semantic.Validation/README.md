# DWIS.Semantic.Validation: the rules of the pre-processing pass

The pre-processing pass (Appendix C.4 of the design) checks a manifest before anything is written to the store. It runs in memory on the manifest's triples plus the vocabulary. Every problem it finds is a *finding* with a code, a level (`error`, `warning`, `info`), the node it is about and a message. One error is enough for the manifest to go live core-only (provider, variables, validity flags and data points) with the rest in quarantine; warnings and infos are reported and change nothing.

This file is the catalogue of the rules. A test checks that every finding code the pass can produce is listed here, so the catalogue cannot silently fall behind the code.

## Where a rule comes from

There are three kinds, and the kind tells you where to go to review or change it:

| Kind | Where it is defined | Who owns it | Change it by |
|---|---|---|---|
| **Vocabulary rules**: domains, ranges, functional properties, disjoint classes | the vocabulary files (`DWISVocabulary.ttl` and `advancedDefinitions/*.ttl`), as OWL and RDFS axioms | the vocabulary project | editing the Turtle; the pass reads the axioms, it does not know them |
| **SHACL shapes**: any constraint written as a shape, including SPARQL constraints | `advancedDefinitions/DWISVocabularyShapes.ttl` from the vocabulary project, plus any shape file of this project loaded next to it | the vocabulary project; this project for its own shapes | adding or editing a shape; no code change |
| **Built-in checks**: the things that need the manifest and the store together, or a computation (subclass closure, SI dimensions) | this project, one static method per check, listed below | this project | editing the named C# file and its test |

The **policy** (`validation-policy.json`, next to the vocabulary) sits over all three: it can raise, lower or switch off any rule by its code, and any shape by its IRI, without a code change. See "Changing a rule".

## The catalogue

Codes are stable; they appear in the ingestion report (Appendix C.9) and are the keys of the policy. "Level" is the level before the policy. Counts in the last column are what the SmartConnect fixture (254 variables, 391 nodes, 1,537 references) produces with the vocabulary of 14 October 2026.

### Built-in checks

| Code | Checks that | Level | Defined in | On the fixture |
|---|---|---|---|---|
| `unknown-class` | every class a node is typed with exists in the vocabulary (the provider's own injected terms are allowed) | error | `PreprocessingPass.cs`, `CheckTermsDomainsRangesAndReferences` | 0; 1 on the `-unknown-class` copy |
| `unknown-verb` | every verb (object property) of a reference exists in the vocabulary | error | same | 0 |
| `unknown-attribute` | every attribute (datatype property) of a field exists in the vocabulary | error | same | 0 |
| `domain-mismatch` | the subject of a verb or attribute is of the declared domain (after subclass closure; a union domain is satisfied by any member) | error | same | 4 |
| `range-mismatch` | the object of a verb is of the declared range, when its types are known (in the manifest, in the reference individuals, or in the store) | error | same | 22 |
| `dangling-reference` | the object of a verb exists in the manifest, in the reference individuals (units, quantities), as a class, or in the store | error | same | 0; 1 on the `-dangling` copy |
| `functional-conflict` | a functional verb or attribute has at most one value per subject | error | `PreprocessingPass.cs`, `CheckFunctional` | 0 |
| `disjoint-classes` | no node is typed with two classes that the axioms declare disjoint (`owl:disjointWith`, `owl:AllDisjointClasses`) | error | `PreprocessingPass.cs`, `CheckDisjointness` | 254 |
| `shacl` | every SHACL shape holds; one finding per shape result, the shape IRI in `source` (see below) | the shape's `sh:severity`; error when it declares none | `ShaclCheck.cs` | 303 |
| `unit-quantity-dimension` | the unit of a signal has the SI dimension of the quantity of its data point (dimensions from the `L`, `M`, `T`, ... exponents of the base quantities in the reference individuals; nothing is reported when either side has no exponents) | error | `UnitQuantityCheck.cs` | 0; 1 on the `-kilogram` copy |
| `belongs-to-class-used` | the manifest does not use `BelongsToClass`, the deprecated way to say `rdf:type` | info | `PreprocessingPass.cs`, `Run` | 1 (a count) |

Subclass and sub-property closure, domains and ranges (own and inherited), unions, disjoint pairs and the types of the reference individuals are computed once per vocabulary version by `VocabularyModel.cs`.

### Notes from the manifest step

The manifest step (`DWIS.Semantic.Manifest`) reports what it changed or could not read while turning the JSON into triples. Its notes become findings of the same report and the policy applies to them too.

| Code | Means | Level | Defined in |
|---|---|---|---|
| `validity-flag-added` | a provided variable had no validity flag, so one was added (`{id}_validitySignal`; decision D-23) | info | `ManifestPreparation.cs` |
| `injected-variable-dropped` | the manifest declares injected variables; they are dropped (decision D-26) | warning | `ManifestPreparation.cs` |
| `reference-to-injected-variable` | a reference names a dropped injected variable; the reference is dropped | warning | `ManifestGraphBuilder.cs` |
| `incomplete-reference` | a reference has an empty subject, verb or object | error | `ManifestGraphBuilder.cs` |
| `unknown-namespace-alias` | a reference uses a namespace alias the manifest does not declare | error | `ManifestGraphBuilder.cs` |

### SHACL shapes in force

The shapes file of the vocabulary project (`DWISVocabularyShapes.ttl`, 2026) has 16 node shapes: `DrillingDataPointShape`, `MeasurableQuantityShape`, `MeasurableQuantityUnitAssociationShape`, `UnitSystemShape`, `DrillingSignalShape`, and the eleven "motive" shapes (`BasicSignalMotive`, `HydraulicSignalMotive`, `MechanicalSignalMotive`, `DownholeSignalMotive`, `ProcessedSignalMotive`, `DerivedSignalMotive`, `TransformedSignalMotive`, `ReferenceFrameMotive`, `LocationMotive`, `LocatedSignalMotive`), all under `http://d-wis.org/shapes/` once translated. None declares `sh:severity`, so each reports as error unless the policy says otherwise.

A shape result becomes one finding with code `shacl`, the focus node, the path and value when the shape gives them, the shape's `sh:message`, and the IRI of the *node shape* in `source` (a property shape is usually a blank node, so the pass names the node shape that owns it; that is the key to use in the policy).

The shapes run against the manifest triples merged with the vocabulary and the reference individuals, so `sh:class` and `sh:targetClass` see subclasses and the units and quantities have types. Only results about the manifest's own nodes are reported.

## Changing a rule

**To soften, harden or switch off a rule**, edit the policy file. One entry per rule, keyed by finding code or by shape IRI; a rule that is not listed keeps the level above. The level `off` drops the findings; for `shacl` and `unit-quantity-dimension` it also skips the check. An entry for a shape wins over the entry for `shacl`. Say why in `reason`, and what would let the entry go: the report reader and the next reviewer see it.

```json
{
  "rules": {
    "domain-mismatch": { "level": "warning", "reason": "In RDFS a domain is an entailment, not a constraint." },
    "http://d-wis.org/shapes/DrillingSignalShape": { "level": "warning", "reason": "Validity flags are booleans without a unit." },
    "belongs-to-class-used": { "level": "off", "reason": "Noise while every client still uses it." }
  }
}
```

The file is read with `ValidationPolicy.Load`. A key that is neither a code of this catalogue nor an IRI, an unknown level, or an unknown property is an error when the file is read, so a typo cannot pass unnoticed. Comments (`//`) and trailing commas are allowed. The milestone 1 policy, with the reason for each of its four entries, is `fixtures/vocabulary/validation-policy.json` in `DWIS-Blackboard-Tests` (decision D-45).

**To add a constraint**, write a SHACL shape. Anything that can be said about one node and its neighbours fits SHACL Core; anything that can be asked in SPARQL fits a `sh:sparql` constraint (the engine is dotNetRDF, decision D-29; both kinds are checked by the tests). Put the project's own shapes in a Turtle file next to the vocabulary project's and load both (`VocabularyModel.Load` takes a list of shape files); keep the vocabulary project's file unchanged so that it can be replaced. Give the shape an IRI under `http://d-wis.org/shapes/` and an `sh:message`; set `sh:severity sh:Warning` if a violation should not block the manifest. Example, a data point must have at most one unit:

```turtle
@prefix sh:   <http://www.w3.org/ns/shacl#> .
@prefix dwis: <http://d-wis.org/> .
@prefix dwissh: <http://d-wis.org/shapes/> .

dwissh:OneUnitPerSignalShape a sh:NodeShape ;
    sh:targetClass dwis:DrillingSignal ;
    sh:severity sh:Warning ;
    sh:property [
        sh:path dwis:HasUnitOfMeasure ;
        sh:maxCount 1 ;
        sh:message "A signal has at most one unit of measure."@en
    ] .
```

**To change a vocabulary rule** (a domain, a range, a functional or disjoint declaration), change the vocabulary; that is the vocabulary project's work (Appendix D of the design lists what this project found).

**To change a built-in check**, edit the file named in the catalogue and the test that covers it in `tests/DWIS.Semantic.Validation.Tests/PreprocessingPassTests.cs`. Add the code to `FindingCodes.cs` and a row to this file if the check is new; the test `Every_finding_code_is_in_the_catalogue` fails otherwise.

## Trying a change

`dotnet test` runs the pass on the SmartConnect fixture and its edited copies (Appendix C.10: kilogram unit, dangling reference, unknown class, injected variables) with the milestone 1 policy, and prints a summary per code and shape in the test output. The test `The_SmartConnect_manifest_is_accepted_under_the_M1_policy_and_its_warnings_are_known` pins the exact warning counts, so a change to a rule, a shape or the policy shows up as a changed count.
