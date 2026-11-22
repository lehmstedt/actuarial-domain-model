# Actuarial domain model

Read `docs/HANDOFF.md` first. It holds the requirements, the design decisions with reasons, known gaps and the next steps.

## Ground rules
- C# 12, .NET 8, nullable enabled, implicit usings on. Domain types are immutable records unless there is a reason (`Analysis` is the one stateful class).
- Never mutate a published `TableDefinition` or a signed-off `Analysis`. Changes are new versions or new revisions.
- Compatibility between tables is checked at publish (`Library.Publish`), not at analysis creation or compute time.
- User state is keyed by period identity (`ExposureKey`, `DevKey`), never by index.
- State that no longer maps is made dormant, never deleted.
- Every mutation from the UI goes through `ActuarialService` (application layer), which validates via the domain and logs an audit event.
- The web app is static files (Blazor WebAssembly). Avoid APIs the browser cannot run (e.g. MD5, file system, threads).

## Commands
- `dotnet build`
- `dotnet test`
- `./build-site.sh` → static site in `./site`
- `dotnet run --project src/Actuarial.Web` → dev server
