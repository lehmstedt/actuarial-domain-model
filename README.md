# Reserving Workbench

An actuarial reserving application built on the domain model described in [`docs/HANDOFF.md`](docs/HANDOFF.md).
The C# domain model runs **in the browser** (Blazor WebAssembly), so the whole application is a folder of static
files: no server, no database. Work is saved in the browser's localStorage and can be exported or imported as JSON.

**Live app: https://lehmstedt.github.io/actuarial-domain-model/** (published from the default branch by GitHub Actions).

## Run it

```bash
./build-site.sh                      # dotnet publish -> ./site
python3 -m http.server -d site 8080  # or any static file server, from any sub-folder
```

Then open http://localhost:8080. The first load builds a demo workspace. **Reset demo** on the overview rebuilds it.

Development: `dotnet build`, `dotnet test`, `dotnet run --project src/Actuarial.Web` (dev server with hot reload).
`.github/workflows/pages.yml` tests every push and publishes `./site` to GitHub Pages on pushes to the default branch.
If the first deployment fails because Pages is off, set *Settings → Pages → Source* to **GitHub Actions** and re-run it.

## Mobile

On a phone (a viewport up to 760px wide, or a touch-first mobile device) the app switches to a mobile layout:
an app bar and bottom tab bar instead of the sidebar, pickers instead of the long template and library lists,
an *Actions…* menu for analysis operations, cards for the analyses list, 16px inputs (no iOS zoom on focus) and
tables that scroll inside their panel with the row labels pinned. Detection runs before the first paint and follows
resizes and rotation.

## Layout

| Project | What it holds |
|---|---|
| `src/Actuarial.Domain` | The domain model: versioned definitions, library and template publishing, analyses, the four operations, the state migrator, state validation, and the formula language (`Formulas.cs`). No I/O. |
| `src/Actuarial.Application` | The calculation engine, claim data and snapshots, JSON persistence, the use cases (`ActuarialService`) and the demo seed. |
| `src/Actuarial.Web` | The Blazor WebAssembly UI. Every mutation goes through `ActuarialService`. |
| `tests/Actuarial.Domain.Tests` | xUnit tests for the migrator, publishing, analyses and the engine. |

## Use cases

| Area | Use case | Where in the UI |
|---|---|---|
| Library | Browse tables, versions, schema, dependencies and dependents | Table library |
| | Publish a new version or a new table with a form per table kind; publish several atomically (*Publish another table alongside…*) | Table library → *Edit → new version* / *New table…* |
| | See a breaking change refused, with the dependents that break | (same) |
| Templates | Browse the tree and its computed dependency closure; edit it in a form (add, reorder, move, duplicate, pin versions) with a live closure check; publish a version | Templates → *Edit → new version* / *New template…* |
| Data | Create segments by attribute filter; take content-hashed snapshots | Segments & data |
| Analysis | Create an analysis (segment, valuation date, structure, template) | Analyses → *New analysis* |
| | Select factors per age, statistics, methods or averages per exposure period; enter values and overrides | Analysis → tables |
| | See what still needs input, all state with provenance, dormant state with reasons, the manifest | Analysis tabs |
| | Apply a template (keeps pins, parks hidden tables' state) | *Apply template* |
| | Reshape the triangle in place (fan-out / merge / dormant) | *Reshape* |
| | Roll forward to a later valuation date (grow or slide) | *Roll forward* |
| | Duplicate onto a segment at the same valuation date | *Duplicate* |
| | Sign off (materialise + hash), open a new revision, upgrade to the latest definitions | *Sign off…*, *New revision*, *Upgrade* |
| | Verify a signed-off analysis reproduces its hash | *Verify* |
| Audit | Every publish, snapshot and operation, with its migration report | Audit trail |

## The calculation engine

- **Input tables** read the snapshot (`paid`, `incurred`, `reported_count`, `premium`), cumulative or incremental, or
  evaluate a formula. Any cell can be overridden by a value stored in the analysis.
- **Development exhibits** compute age-to-age factors and the statistics they declare (volume-weighted, simple,
  medial, latest, custom; optionally windowed), the selected factor per age, the tail and the CDF.
- **Exhibits** reduce an exposure-period column to a scalar (or a triangle to a per-age row) with the same statistics.
- **Method tables** evaluate formula columns in dependency order; user-input columns take values from the analysis.
- **Selection tables** offer each method's ultimate plus arithmetic and weighted averages per exposure period.

The formula language: `+ - * /`, parentheses, numbers, `$n` (the n-th declared reference), other column keys of the
same method table, and `latest(x)`, `current(x)`, `min`, `max`, `abs`, `coalesce`. Grain is inferred and checked at
publish, so a published formula always fits its column. Division by zero and missing values give empty cells.

The engine is a pure function of manifest, structure, valuation date, data snapshot, active state and engine version.
Drafts compute on read; sign-off stores the results and a SHA-256 hash of those inputs plus the results.

## Demo data

The claim data is synthetic and deterministic: three lines (Motor, Property, Liability) in two regions, quarterly from
2012 Q1 to 2026 Q3. Because the data is quarterly, period lengths must be 3, 6 or 12 months and valuation dates quarter
ends. The seeded story: a signed-off Motor analysis at 2024-12-31, its roll-forward draft at 2025-12-31, a duplicate
onto Property North, and Paid development v2 published afterwards (so an upgrade is available).
