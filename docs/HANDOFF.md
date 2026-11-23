# Actuarial application: domain model handoff

Status: **builds, tested, and running as an application.** The domain compiles on .NET 8, has an xUnit suite,
and powers a Blazor WebAssembly app that runs from static files (see `README.md`). Section 5 records which gaps were
closed and how; section 8 lists what is still open.

## 1. What we are building

A domain model for an actuarial reserving application.

### Original requirements (from the user)

Foundational analysis bricks are **tables**:
- **Input table**: an entry point (e.g. a paid-loss triangle) or a triangle computed by a formula.
- **Exhibit table**: precomputes input tables, offers statistics, lets the user make selections.
  - **Development exhibit** (special kind): computes age-to-age factors, lets the user select statistics, performs a chain-ladder analysis.
- **Method table**: groups columns driven by user input or formulas. It designates an **ultimate column** that other tables can reference (notably for Bornhuetter-Ferguson).
- **Selection table**: takes several columns as input, provides weighted and arithmetic averages, and lets the user select among those averages or a specific method **per exposure period**.

Tables live in a **library**. Relationships between tables come from formula or method references.

An **analysis template** presents the dependency graph as a tree: folders contain folders or collections; collections contain only objects; an object is a reference to a library table. The same reference can appear several times.

Raw claim data is split into **segments**. For a segment at a valuation date, with a **triangle structure** (number of development and exposure periods, period length) and a template, we compute and visualise the objects.

**Edits to tables or templates must never change existing segments directly. Signed-off or past analyses must stay auditable.**

### Operations on an analysis
- Apply a different template. No selection or user-entered value may be lost.
- Roll forward to a later valuation date. The triangle gets one more diagonal. Selections and inputs carry as much as possible. Produces a new analysis.
- Reshape the triangle in place. Period lengths and counts can change. Carry as much selection as makes sense.
- Duplicate. The user chose: **duplicate onto a segment (same or different), same valuation date**.

## 2. Architecture in one paragraph

Two halves. The **definition side** (library, tables, templates) is immutable and versioned: editing means publishing a new version. The **instance side** (`Analysis`) pins exact definition versions through a `ResolvedManifest`, owns all user state (selections and manual inputs), and references a frozen `DataSnapshot`. Sign-off freezes manifest, data hash, state, engine version and results. Upgrading never mutates: it creates a new revision.

## 3. Decisions and why

1. **Immutable versioned definitions.** `(TableId, Version)` is the identity. Audit requires that nothing a signed-off analysis used can change.
2. **The template is presentation only.** The computation graph comes from table references. The manifest covers the full dependency closure, including tables not shown in the template.
3. **One version per table per analysis.** `ManifestBuilder.Build` throws if the closure needs one table at two versions.
4. **Compatibility is enforced at publish** (user preference). Each definition declares `Output` (a `TableSchema` of columns with measure, basis, grain) and `Requirements` (`InputRequirement`: source ref, column, grain, optional measure and basis). `Library.Publish(batch)` is atomic and checks:
   - self-consistency (`SelfCheck`)
   - reference resolution
   - schema satisfaction
   - DAG (no cycles)
   - floating refs against the new latest versions (a breaking change must be published together with its dependents)
5. **Definitions are independent of triangle size.** They declare grain (development cell / development period / exposure period / scalar), never counts.
6. **State is owned by the analysis** and keyed by *what it means*: `StateKey(Table, ColumnKey, ExposureKey?, DevKey?)`. `ExposureKey` is start date plus length in months; `DevKey` is the start age plus length. Position indices were rejected because they break under roll-forward and reshape.
7. **Dormant, not deleted.** State that doesn't map goes to `Analysis.Dormant` with a reason, and is retried on every migration. Reshaping back or re-adding a table restores it.
8. **Provenance on every state entry** (`Entered`, `CarriedForward`, `Remapped`) so the actuary can review what came along.
9. **Operation semantics**
   - `ApplyTemplate` and `Reshape`: in place, drafts only. A signed-off analysis needs `NewRevision` first.
   - `RollForward` and `Duplicate`: create a new analysis (`Revision = 1`) with a `Lineage` pointer. Duplicate is always a Draft.
   - `ApplyTemplate` keeps existing pins (`preferred` manifest) so changing a template never silently upgrades a table. Only newly needed tables resolve to latest.
   - `RollForward` keeps template version and pins.
10. **Carry rules when period lengths change** (`StateMigrator`)
    - Same period identity: carried as is.
    - **Amounts** (`ManualValue`): exact key match only (can't split or merge without weights).
    - **Categorical** (`SelectedStatistic`, `SelectedMethod`): may split (fan out) or merge (only if every constituent period is present and holds the same value). Otherwise dormant.
    - An exact match always beats a remap.
11. **Roll-forward growth** is a parameter: `Grow` (keep first exposure period, add periods) or `Slide` (keep counts, drop oldest).
12. **Drafts compute on read; results are materialised at sign-off** (`ResultSnapshot`). The engine must be a pure function of manifest, data, state and engine version.

## 4. Repository layout

```
Actuarial.sln
README.md                                    how to run, use cases, engine summary
CLAUDE.md                                    conventions for Claude Code
build-site.sh                                publish the static site to ./site
docs/HANDOFF.md                              this file
docs/entity-diagram.mmd                      Mermaid ER diagram of the model
src/Actuarial.Domain/ActuarialDomain.cs      value objects, schemas, definitions, libraries, template, analysis, validation
src/Actuarial.Domain/ActuarialOperations.cs  the operations + StateMigrator
src/Actuarial.Domain/Formulas.cs             formula grammar, parser, grain inference
src/Actuarial.Application/                   engine, claim data + snapshots, persistence, use cases, demo seed
src/Actuarial.Web/                           Blazor WebAssembly UI
tests/Actuarial.Domain.Tests/                xUnit tests
```

## 5. Gaps from the first pass, and how they were closed

- **Compile.** The sketch compiled as written. One genuine design problem was fixed: `DevelopmentExhibitDefinition`
  inherited the positional `Inputs`/`Schema` of `ExhibitTableDefinition`, so `def with { Statistics = … }` left a stale
  schema. Both now derive from an abstract `ExhibitDefinition` and the development exhibit computes its schema.
- **Value validation.** `StateValidator.Problem` = schema problem + value problem: the statistic must still be offered,
  the method still selectable, the average still offered, and a `ManualValue` only goes where the definition accepts
  entered values (`TableDefinition.AcceptsManualValue`). Used on user entry and on every migration.
- **Fan-out collisions** are all-or-nothing per source. An exact carry that finds its key already taken parks too.
- **Merges** require the old periods to tile the whole new period, not just "all that existed".
- **Period lengths.** Lengths are 1, 3, 6 or 12 months; the development length must divide the exposure length (annual
  years with quarterly development is legal, the reverse is not); exposure periods start on their calendar boundary;
  the valuation date is a month end inside the last exposure period. `TriangleStructure.Validate`, enforced by
  reshape and roll-forward. The app adds that the (quarterly) data needs multiples of 3.
- **Templates** have a `TemplateLibrary`: versioned, published with tree checks, object resolution and a closure build.
- **Version assignment.** The library allocates: a new table is v1, the next version is always latest + 1
  (`NextVersion`); publish refuses anything else. One version per table per batch.
- **Sign-off** computes results and a SHA-256 hash over engine version, valuation date, structure, data hash, manifest,
  active state and results. **Dormant state is stored but not hashed** (it cannot change a number). `Sign` refuses
  results from another engine version; `Verify` recomputes and compares.
- **Engine and formulas.** A grammar exists (`Formulas.cs`) with grain inference checked at publish. The engine is in
  `Actuarial.Application/Calculation`. `ICalculationEngine.Compute` no longer takes the template: it is presentation only.
- **Persistence.** JSON documents (`WorkspaceDocument`), with ids as strings and polymorphic definitions tagged by
  `$kind`. Definitions restore without re-validation (they were valid when published). In the app, localStorage.
- **Audit trail.** Operations log an `AuditEvent` carrying the `MigrationReport`.
- **Editors.** Tables and templates are created and edited through forms, not JSON. `Actuarial.Application/Editing`
  holds mutable drafts (`TableDraft`, `TemplateDraft`) that build immutable definitions; `TableBatch` holds several
  drafts that publish atomically and may reference each other (a floating ref to a batch member means the draft).
  The forms derive what the domain derives: a reference's grain comes from the chosen column, exhibit schemas from
  the statistics, a selectable method's ultimate key from the method table. Live checks run `SelfCheck`; cross-table
  checks stay at publish. A refused publish offers to add the dependents to the batch.
- **Semantics decided along the way:**
  - Development period i is `DevKey(i·L, L)` and holds the cumulative at age (i+1)·L. The factor keyed by period
    i ≥ 1 develops age i·L → (i+1)·L ("12-24 ⇒ 12"). Period 0 has no factor and is not listed as needed.
    The tail is a scalar `tail` column (a `ManualValue`, default 1).
  - `SelectedAverage(AverageKind)` was added: the selection table offers averages as well as methods.
    Weights are scalar `weight:<method id>` columns, keyed by method so they survive reordering.
  - Generic exhibits: `Inputs[0]` is analysed, `Inputs[1]` (optional) weights the volume-weighted average.
  - `NewRevision` keeps the analysis id and increments the revision, with `Lineage(NewRevision, …)`. With an upgrade,
    state is migrated against the new manifest so values that no longer fit go dormant.
  - Only the latest revision of an analysis can be edited.

## 6. Open questions for the product owner

- Should manual values carried by roll-forward be flagged stale when the underlying data for those periods changed?
- Can a signed-off analysis be duplicated across valuation dates, or only same date? (Currently only the same date.)
- Should default selections exist on definitions and be copied in at analysis creation?
- Do we need several concurrent drafts per (segment, valuation date, structure)?
- What happens to selections when a table's new version is compatible but changes a statistic list?

## 7. What is tested

`tests/Actuarial.Domain.Tests`: roll-forward grow and slide, reshape quarterly ↔ annual (fan-out, unanimous and
conflicting merges, amounts), round trips restoring everything, template removal and re-adding, pins kept on
`ApplyTemplate`, upgrade parking dropped statistics, all-or-nothing fan-out, publish (missing column, grain / measure
/ basis, cycles, version allocation, atomic batches, formulas, selection targets), sign-off immutability, revisions,
value validation, hash verification after a JSON round trip, structure rules, duplicate, and engine arithmetic.

## 8. Still open

- The open questions in section 6 are unanswered; current behaviour: manual values are not flagged stale on
  roll-forward; duplicate is same-date only; there are no default selections; several drafts per (segment, date) are
  allowed; a compatible version that drops a statistic parks the selections that used it (upgrade only).
- Persistence is a single JSON document in the browser. A server store would keep the same documents as immutable rows.
- The engine has no tail-fitting, no Mack/bootstrap variability, no incremental-basis development, and no
  per-exposure statistics exclusions. Formulas have no conditionals.
- No access control or sign-off roles: the user name is free text.
