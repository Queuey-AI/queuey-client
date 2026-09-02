# Queuey.Client — agent instructions

The official .NET SDK for [Queuey](https://queuey.ai), shipped as four packages:
`Queuey.Client` (core: auth + ingress publish), `Queuey.Client.Waas` (producer
control plane — streams, packages, integrations), `Queuey.Cli` (the `queuey`
tool), and `Queuey.Edge` (durable local publishing on unreliable networks).

This repo is publishable — everything tracked here is public-facing. Internal
notes, design logs and this repo's agent memory live in the git-ignored
`.internal/`; start at `.internal/agent/INDEX.md`.

General house rules (git discipline, merge mechanics, note keeping) are in
`~/.claude/CLAUDE.md` and are not repeated here.

## Branching — the one deviation from the global rule

**There is no `dev` branch here.** `main` is the integration branch, and feature
branches (`edge/**`, `feat/**`, `fix/**`) open PRs straight into `main`. The rest
of the global flow is unchanged: build → PR → **STOP**.

Edge work is currently a stack (#14 → #15 → #16, plus #17 on `main`). Stacked PRs
carry the global trap: retarget the child before merging and deleting the base,
or GitHub closes the child silently.

## Verify before delivering

```bash
dotnet build Queuey.Client.sln -c Release
dotnet test Queuey.Client.sln -c Release
```

CI (`ci.yml`) builds and tests the **whole solution** on every PR, using the .NET 9
SDK. Libraries target `netstandard2.0;net8.0` (`Queuey.Edge` and the CLI are net8
only); test projects are net9.0. A new project is covered by the gate only once it
joins `Queuey.Client.sln` — adding it there is part of the change, not a follow-up.

## Invariants

- **Dependency-light is the product, not a preference.** The core client is
  `System.Text.Json` + `HttpClient` — no Newtonsoft. `Queuey.Edge` earns
  `Microsoft.Data.Sqlite` because the spool is the feature. Any other new package
  reference is a design decision that belongs in the PR text.
- **Public ids are opaque.** Prefix-check (`ten_`, `que_`, `qak_`, `evt_`, …) and
  never split on `_` — the nanoid body can itself contain `_` and `-`.
- **Enums are integers on the wire, permanently**, and the SDK parses them
  leniently per field. This is not a transitional state; see
  `.internal/agent/locked-decisions.md` for why a global string converter was
  rejected.
- **The spool never drops an event.** A payload Edge cannot decrypt is quarantined
  (`PayloadUnreadable`) — never sent, never deleted — and its lane keeps draining.
  Settling blanks the payload while the row stays for correlation until
  `SettledRetention` removes it.
- **`netstandard2.0` is a contract with .NET Framework consumers.** No net8-only
  API in the core or Waas libraries without a `#if`.

## Release

Tag-driven: `git tag v0.1.0-preview.6 && git push --tags` packs every packable
project to NuGet and attaches self-contained `queuey` CLI binaries to a GitHub
Release. The tag overrides `<Version>` in `Directory.Build.props`. No tag has been
pushed yet — `0.1.0-preview.5` exists only as local packages in `artifacts/`.

## Across repos

| What | Where |
| --- | --- |
| Backend contracts the SDK mirrors | `../Queuey` (`src/Queuey.Api`, `Queuey.Ingress`) |
| Product-wide skills (hygiene, lenses, release) | `../Queuey/.claude/skills/`, also global |
| Edge operations runbook | `docs/edge-operations.md`, `src/Queuey.Edge/README.md` |
