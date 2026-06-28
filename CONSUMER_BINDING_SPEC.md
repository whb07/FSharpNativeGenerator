# Consumer Binding Spec: `FSharpNativeGenerator` → forked `FSharp.Compiler.Service`

## 1. Goal

Bind the `FSharpNativeGenerator` library (this repo, `~/projects/FSharpNativeGenerator`)
onto the locally-forked F# compiler (`~/projects/fsharp`) so that:

- Generator **authors** keep the hardened, ergonomic author-facing contract this repo
  already provides: `[<FSharpGenerator>]` attribute + API versioning, `IFSharpIncrementalGenerator`
  with `RegisterSourceOutput` / `RegisterPostInitializationOutput`, incremental value
  providers, the full placement vocabulary (`Prelude`, `BeforeFile`, `AfterFile`,
  `BeforeLastSourceFile`, `EndOfProject`), the FSG0001–FSG0015 diagnostic set, the
  assembly loader, and the CLI/MSBuild/NuGet configuration surface.
- Generator **hosts** (CLI, MSBuild target, IDE/FCS) delegate actual compilation and
  type-checking to the fork's `FSharpChecker` via its public source-generator methods
  (`CompileWithSourceGenerators`, `RunSourceGeneratorsAndUpdateProject`,
  `ParseAndCheckProjectWithSourceGenerators`), inheriting the fork's in-memory store,
  `FileSystem` overlay, per-checker run cache, and generator-aware project `Stamp`.

The binding is the adapter between these two surfaces. It does **not** re-implement
compilation, checking, caching, or file-system overlay — the fork owns those. It
**does** own: assembly loading, attribute/version discovery, the incremental
authoring API, configuration parsing, placement resolution, and diagnostic IDs.

## 2. Non-Goals

- Re-implementing the fork's driver, store, overlay, run cache, or stamp.
- Exposing the fork's internal `FSharpSourceGeneratorDriver` (it stays `internal`).
- Node-level incremental caching inside a single `Generate` call. The incremental
  API is preserved as an **author-ergonomics layer**; real cross-run invalidation
  is delegated to the fork's run cache + project stamp. (See §11.)
- Semantic/typed-tree providers (same deferral as the original spec).
- Shipping a NuGet package of the binding in V1 (the fork is consumed via
  `<ProjectReference>`; packaging is a later step).

## 3. Current State Of The Two Codebases

### 3.1 Fork (`~/projects/fsharp`) — what it exposes publicly

Namespace `FSharp.Compiler.SourceGeneration` (all public):

- `IFSharpSourceGenerator` — single sync method
  `Generate : FSharpSourceGeneratorContext -> FSharpSourceGeneratorOutput`.
- `IFSharpSourceGeneratorWithId` — optional `GeneratorId : string` for deterministic
  path/identity when the type full name is not stable.
- `FSharpGeneratedSource` — `{ HintName; FileName; SourceText: string; Kind; Order }`.
- `FSharpGeneratedSourceKind` — `Implementation | Signature`.
- `FSharpGeneratedSourceOrder` — `BeforeFile | AfterFile | EndOfProject | BeforeImplementation`.
- `FSharpSourceGeneratorContext` — `{ ProjectFileName; ProjectDirectory; SourceFiles: string list;
  OtherOptions; References; DefineConstants; OutputFile; AssemblyName; AdditionalFiles: Map<string,string>;
  CancellationToken }`. **No per-file source text or checksums** — paths only.
- `FSharpSourceGeneratorOptions` — `{ OutputDirectory; EmitGeneratedFiles; AdditionalFiles;
  AnalyzerConfigFiles; MaxPasses }`.
- `FSharpSourceGeneratorOutput`, `FSharpSourceGeneratorDiagnostic`, `FSharpGeneratedSourceStore`,
  `FSharpSourceGeneratorRunResult` (with `Store`, `CacheHit`, `OrderedSourceFiles`).

`FSharpChecker` (public, in `FSharp.Compiler.CodeAnalysis`):

- `CompileWithSourceGenerators(argv, generators, opts) : Async<FSharpDiagnostic[] * FSharpSourceGeneratorRunResult * exn option>`
- `RunSourceGeneratorsAndUpdateProject(options, generators, opts) : Async<FSharpProjectOptions * FSharpSourceGeneratorRunResult>`
- `ParseAndCheckProjectWithSourceGenerators(options, generators, opts) : Async<FSharpCheckProjectResults * FSharpSourceGeneratorRunResult>`

The fork **does not** expose: an assembly loader, a marker attribute, API versioning,
CLI/MSBuild configuration parsing, incremental providers, `Prelude`/`BeforeLastSourceFile`
placement, or the FSG0001–FSG0015 diagnostic set. Those are this repo's job.

### 3.2 This repo (`~/projects/FSharpNativeGenerator`) — what it has today

- `Types.fs`, `Incremental.fs`, `LoadingAndConfiguration.fs`, `GeneratedSource.fs`,
  `CacheIdentity.fs`, `Driver.fs` — the standalone library.
- References the **NuGet** `FSharp.Compiler.Service` package (43.11.301), not the fork.
- Carries **shadow types** (`FSharpSourceText`, `FSharpProjectOptions`, `FSharpDiagnosticSeverity`,
  `FSharpOutputKind`, `SourceRange`) so generators need not reference compiler internals.
- Has its **own** driver (`FSharpGeneratorDriver`), store, cache identity, placement engine,
  and a CLI host (`FSharpNativeGenerator.Cli`) that mimics the `fsc` surface.
- 123 passing tests.

### 3.3 The divergence to close

| Concern | This repo (standalone) | Fork |
|---|---|---|
| Generator contract | `IFSharpIncrementalGenerator.Initialize` + incremental providers | `IFSharpSourceGenerator.Generate` (sync) |
| Placement cases | 5 (incl. `Prelude`, `BeforeLastSourceFile`) | 4 (`BeforeFile`/`AfterFile`/`EndOfProject`/`BeforeImplementation`) |
| Final-file rule enforcement | Yes (FSG0012 rejects `EndOfProject` for apps) | No |
| Source text type | `FSharpSourceText` (wrapper) | `string` |
| Project options | shadow `FSharpProjectOptions` | real FCS `FSharpProjectOptions` |
| Driver / store / cache / stamp | own | fork-owned |
| Assembly loader + attribute + API version | yes | no |
| CLI / MSBuild / NuGet config | yes | no |
| Diagnostic IDs FSG0001–FSG0015 | yes | no (only `FSGEN_*` warnings) |

## 4. Target Architecture

Three layers, all inside this repo. The fork is referenced but not modified by the binding.

```
┌─────────────────────────────────────────────────────────────────┐
│ Layer A — Generator-author contract (preserve, lightly retarget) │
│   [<FSharpGenerator>], FSharpGeneratorApiVersion,                │
│   IFSharpIncrementalGenerator, FSharpIncrementalValueProvider,   │
│   FSharpIncrementalValuesProvider, FSharpPostInitializationContext,│
│   FSharpSourceProductionContext, FSharpGeneratedSourcePlacement, │
│   FSG0001–FSG0015 diagnostics, FSharpGeneratorAssemblyLoader,    │
│   FSharpSourceGeneratorConfiguration (CLI/MSBuild/NuGet).         │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼  (adapter wraps Layer-A generators)
┌─────────────────────────────────────────────────────────────────┐
│ Layer B — Binding adapter (new)                                  │
│   IncrementalGeneratorAdapter : IFSharpSourceGenerator           │
│     + IFSharpSourceGeneratorWithId                               │
│   - Runs the Layer-A incremental graph inside Generate.          │
│   - Translates placement cases → fork FSharpGeneratedSourceOrder.│
│   - Runs the hardened placement engine to compute Order          │
│     annotations that reproduce the resolved order under the      │
│     fork's ordering rules.                                       │
│   - Maps shadow diagnostics → fork FSharpSourceGeneratorDiagnostic│
│     (where needed; most are already shape-compatible).           │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼  (host facade delegates to FSharpChecker)
┌─────────────────────────────────────────────────────────────────┐
│ Layer C — Host facade (rewrite, thin)                            │
│   FSharpGeneratorHost                                           │
│     .LoadFromConfiguration(config) : IFSharpIncrementalGenerator │
│        list  (loader + adapter wrapping)                         │
│     .RunGenerators(options, gens, opts)                          │
│        : FSharpProjectOptions * FSharpSourceGeneratorRunResult   │
│     .Compile(argv, gens, opts)                                   │
│        : FSharpDiagnostic[] * FSharpSourceGeneratorRunResult *   │
│          exn option                                              │
│     .ParseAndCheck(options, gens, opts)                          │
│        : FSharpCheckProjectResults * FSharpSourceGeneratorRunResult│
│   Each delegates to the corresponding FSharpChecker method.      │
└─────────────────────────────────────────────────────────────────┘
```

The standalone `FSharpGeneratorDriver`, `FSharpGeneratedSourceStore`, and the
shadow `FSharpSourceText`/`FSharpProjectOptions`/`FSharpDiagnosticSeverity`/
`FSharpOutputKind`/`SourceRange` types are **retired**. `CacheIdentity.fs` is
mostly retired (the fork owns stamp + run cache); keep only the
`FSharpGeneratorDriverIdentity`-style assembly-content hashing if the loader
still needs it for its own AssemblyLoadContext keying.

## 5. Retarget Mechanics

### 5.1 Project reference

`src/FSharpNativeGenerator/FSharpNativeGenerator.fsproj`:

- Remove `<PackageReference Include="FSharp.Compiler.Service" ... />`.
- Add a `<ProjectReference>` to the fork, pinned to its `netstandard2.0` target
  so the public surface area is stable and matches the fork's own BSL baseline:

  ```xml
  <ProjectReference Include="$(FSharpRepoRoot)/src/Compiler/FSharp.Compiler.Service.fsproj">
    <SetTargetFramework>TargetFramework=netstandard2.0</SetTargetFramework>
  </ProjectReference>
  ```

- Introduce an MSBuild property `$(FSharpRepoRoot)` defaulting to
  `$(MSBuildThisFileDirectory)../../projects/fsharp` (or resolved via a
  `Directory.Build.props` at the repo root) so the path is not hard-coded per
  developer. Document the override in the README.
- Keep `<TargetFramework>net10.0</TargetFramework>` for this library — referencing
  a `netstandard2.0` project from `net10.0` is valid.
- The CLI and test projects keep their existing `<ProjectReference>` to this
  library; they transitively get the fork.

### 5.2 Shadow type removal

In `Types.fs`, delete:

- `FSharpSourceText` (replace all usages with `string`; the fork's
  `FSharpGeneratedSource.SourceText` is `string`).
- `FSharpProjectOptions`, `FSharpOutputKind` (use FCS's real
  `FSharpProjectOptions`; output kind is derived from `--target` in `OtherOptions`
  as the CLI already does).
- `FSharpDiagnosticSeverity`, `SourceRange` (use
  `FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity` and
  `FSharp.Compiler.Text.range`).
- `FSharpGeneratedSourceStore` (the fork owns the store).
- `FSharpGeneratorDriverOptions`, `FSharpGeneratorDriverRunResult`,
  `FSharpGeneratorRunReport`, etc. (the fork owns the run result).

Keep: `FSharpGeneratorAttribute`, `FSharpGeneratorApiVersion`,
`FSharpGeneratedSourcePlacement` (the 5-case author vocabulary),
`FSharpGeneratorHostKind`, the FSG diagnostic-id constants, the MSBuild item/property
record types, and the configuration types
(`FSharpSourceGeneratorConfiguration`, `FSharpMSBuildSourceGeneratorItem`, …).

### 5.3 Compile-order reshuffle

After shadow removal, the file list in `FSharpNativeGenerator.fsproj` becomes
roughly:

```
Types.fs              (author-facing types + diagnostic IDs + config records)
Incremental.fs        (IFSharpIncrementalGenerator + providers + contexts)
LoadingAndConfiguration.fs (loader + CLI/MSBuild/NuGet config)
Placement.fs          (split out of GeneratedSource.fs; the hardened engine, retargeted)
Adapter.fs            (NEW — Layer B)
Host.fs               (NEW — Layer C, replaces Driver.fs)
```

`Driver.fs`, `GeneratedSource.fs` (the parts that owned the store/run/result),
and `CacheIdentity.fs` (the project-stamp parts) are deleted.

## 6. Layer B — The Adapter

`IncrementalGeneratorAdapter` is the single bridge from the Layer-A incremental
contract to the fork's sync `IFSharpSourceGenerator`.

### 6.1 Shape

```fsharp
type IncrementalGeneratorAdapter
    (inner: IFSharpIncrementalGenerator, generatorId: string)
    =
    interface IFSharpSourceGenerator with
        member _.Generate(ctx: FSharpSourceGeneratorContext) : FSharpSourceGeneratorOutput =
            // 1. Build a Layer-A initialization context (postInitOutputs, sourceOutputs).
            // 2. Call inner.Initialize(initContext).
            // 3. Run post-init outputs → pending prelude sources.
            // 4. Build a Layer-A FSharpSourceProductionContext-equivalent view of ctx
            //    and run each registered source output against it → pending sources.
            // 5. Materialize pending sources into fork FSharpGeneratedSource records
            //    with translated placement (see §8).
            // 6. Run the hardened placement engine to compute, for each generated
            //    source, a fork Order that reproduces the resolved order.
            // 7. Return { GeneratedSources; Diagnostics }.
            ...

    interface IFSharpSourceGeneratorWithId with
        member _.GeneratorId = generatorId
```

### 6.2 Mapping the fork context to the Layer-A view

The fork's `FSharpSourceGeneratorContext` is poorer than the standalone lib's
`FSharpGeneratorProjectSnapshot`. The adapter constructs a Layer-A view:

- `SourceFilesProvider` → emits one entry per `ctx.SourceFiles` path. **Content
  is not available** (the fork gives paths only), so `FSharpSourceFileSnapshot`
  entries carry no checksum. Document this: generators that need file *contents*
  must read them via `ctx.AdditionalFiles` (the fork loads additional-file
  contents into the `Map`) or via `System.IO.File` directly. This is an accepted
  V1 narrowing.
- `AdditionalTextsProvider` → emits one entry per `ctx.AdditionalFiles` kv, with
  full content (the fork populates this).
- `ProjectOptionsProvider` → a synthetic `FSharpProjectOptions` built from
  `ctx.OtherOptions` / `ctx.SourceFiles` / `ctx.ProjectFileName`. Used only for
  generators that branch on project options; not passed back to the fork.
- `AnalyzerConfigOptionsProvider` → empty for V1 (the fork's
  `FSharpSourceGeneratorOptions` carries `AnalyzerConfigFiles` paths but the
  fork does not parse them into a per-path options map; the adapter may parse
  them itself using the existing `analyzerConfigOptions` logic and feed the
  provider). Recommended: parse in the adapter so author-facing analyzer-config
  behavior is preserved.

### 6.3 Running the incremental graph

Each `Generate` call runs the graph fresh (post-init + all registered source
outputs). This is the accepted V1 tradeoff: the incremental API is an ergonomics
layer, not a caching layer. The fork's per-checker run cache is the cache. (§11.)

### 6.4 Diagnostics

Layer-A diagnostics (`FSharpGeneratorDiagnostic`) map 1:1 to the fork's
`FSharpSourceGeneratorDiagnostic` (`Id`, `Message`, `Severity`, `Range`). The
adapter performs the trivial record copy. The FSG0001–FSG0015 IDs are produced
by the loader/placement engine as today; the fork surfaces them unchanged
through its diagnostic stream (the fork's `main1` already forwards generator
diagnostics with an `[ID]` prefix).

## 7. Layer C — The Host Facade

`FSharpGeneratorHost` replaces the standalone `FSharpGeneratorDriver`. It is a
thin wrapper over a single `FSharpChecker` instance.

### 7.1 Shape

```fsharp
type FSharpGeneratorHost
    (?checker: FSharpChecker, ?legacyReferenceResolver: LegacyReferenceResolver)
    =
    member _.Checker = ...
    member _.LoadFromConfiguration
        (config: FSharpSourceGeneratorConfiguration)
        : IFSharpIncrementalGenerator list
        =
        // FSharpGeneratorAssemblyLoader.loadFromPath on each config.GeneratorPath,
        // then adapt each loaded IFSharpIncrementalGenerator with IncrementalGeneratorAdapter.
        // generatorId comes from the loader's type identity (or IFSharpSourceGeneratorWithId
        // if the inner generator implements it — recommended for stable paths).
        ...

    member _.RunGenerators
        (options, generators: IFSharpIncrementalGenerator list, generatorOptions)
        : Async<FSharpProjectOptions * FSharpSourceGeneratorRunResult>
        = async {
            let adapted = generators |> List.map adapt
            return! checker.RunSourceGeneratorsAndUpdateProject(options, adapted, generatorOptions)
          }

    member _.Compile
        (argv, generators: IFSharpIncrementalGenerator list, generatorOptions)
        : Async<FSharpDiagnostic[] * FSharpSourceGeneratorRunResult * exn option>
        = async {
            let adapted = generators |> List.map adapt
            return! checker.CompileWithSourceGenerators(argv, adapted, generatorOptions)
          }

    member _.ParseAndCheck
        (options, generators: IFSharpIncrementalGenerator list, generatorOptions)
        : Async<FSharpCheckProjectResults * FSharpSourceGeneratorRunResult>
        = async {
            let adapted = generators |> List.map adapt
            return! checker.ParseAndCheckProjectWithSourceGenerators(options, adapted, generatorOptions)
          }
```

`adapt` is `IncrementalGeneratorAdapter` construction with a `generatorId`
derived from the loaded type (or `IFSharpSourceGeneratorWithId.GeneratorId`).

### 7.2 CLI

`FSharpNativeGenerator.Cli/Program.fs` is simplified to:

1. Parse args via `FSharpSourceGeneratorConfiguration.parseCommandLineArguments`.
2. `host.LoadFromConfiguration(config)`.
3. Build `FSharpProjectOptions` from remaining args (as today).
4. `host.Compile(argv, generators, opts)` and print diagnostics + updated
   source list.

The CLI no longer owns a driver, store, or placement engine. It becomes a
~50-line wrapper.

## 8. Placement Translation (Layer B → Fork)

The binding keeps its hardened placement engine (cycle detection, duplicate-hint
detection, signature/impl companion rules, final-file enforcement via FSG0012)
as the source of truth for ordering, then emits fork `FSharpGeneratedSource`
records whose `Order` reproduces that exact order under the fork's (weaker)
ordering rules.

### 8.1 Strategy: anchor against original files only

The fork's `FSharpGeneratedSourceOrdering.orderSources`:

- processes `anchored` sources (non-`EndOfProject`) in declaration order,
  inserting relative to the current list;
- warns (not errors) on missing anchors;
- warns when an anchor targets a generated file;
- appends `EndOfProject` sources after originals, signatures before
  implementations (pairing by `Path.ChangeExtension(sig, ".fs") == impl`).

To make the fork's ordering a **deterministic pass-through** of the binding's
resolved order, the adapter anchors every generated source against an
**original** source file (never a generated one), so no fork warnings fire and
no reordering occurs:

1. Run the hardened engine → get the final `OrderedSourceFiles` (originals +
   generated, in resolved order).
2. For each generated source `g` at resolved index `i`:
   - Find the nearest **original** file `o` that follows `g` in the resolved
     list. If one exists, emit `g.Order = BeforeFile o`.
   - Else (the generated source is after all originals), emit
     `g.Order = EndOfProject`.
3. Emit generated sources to the fork in the resolved order, so even the
   `EndOfProject` batch (for trailing sources) preserves declaration order.

This avoids generated-file anchors entirely, sidesteps the fork's
signature-before-implementation reordering (because each source is anchored
relative to originals, not bundled at `EndOfProject`), and reproduces the
hardened engine's order exactly.

### 8.2 Placement-case mapping (author → resolved position)

The author-facing `FSharpGeneratedSourcePlacement` (5 cases) is resolved by the
hardened engine into a position, then encoded per §8.1. The mapping the engine
already implements:

| Author placement | Hardened-engine resolution |
|---|---|
| `Prelude` | before all originals |
| `BeforeFile anchor` | immediately before `anchor` |
| `AfterFile anchor` | immediately after `anchor` |
| `BeforeLastSourceFile` | immediately before the last original `.fs` |
| `EndOfProject` | after all originals (rejected for apps via FSG0012) |

`BeforeImplementation` (the fork's 4th case) is not authored directly; it is an
encoding option the adapter may use internally, but §8.1's
"`BeforeFile` against the next original" is preferred for unambiguity.

### 8.3 Final-file rule

The fork does not enforce the final-file rule. The binding **must** enforce
FSG0012 itself (reject `EndOfProject` when `OutputKind = Application`), before
emitting to the fork. The adapter determines app-vs-library from
`ctx.OtherOptions` (`--target:exe`/`--target:winexe` → Application), reusing the
existing `outputKindFromArgs` logic from the CLI.

## 9. Diagnostic & Cache Identity Strategy

### 9.1 Diagnostics

- FSG0001–FSG0015 are produced by the loader, adapter, and placement engine as
  today. They flow through the fork's diagnostic stream unchanged.
- The fork's own `FSGEN_*` warnings (duplicate hint, missing anchor, disk-write
  failure) should not normally fire if the binding's hardened engine runs first
  (it detects duplicates, cycles, and missing anchors as errors before
  emitting). The adapter should assert this invariant in tests: if the hardened
  engine passes, the fork's ordering produces no warnings.

### 9.2 Cache identity

- **Project `Stamp`**: owned by the fork. `RunSourceGeneratorsAndUpdateProject`
  sets `Stamp = Some(computeStamp(...))` covering options identity + generator
  set + generator options + additional-file contents + generated-source content.
  The binding does **not** set `Stamp` itself.
- **Run cache**: owned by the fork (per-checker `MruCache` keyed by
  `computeRunKey`, which excludes generated content). The binding does not add a
  second cache layer.
- The binding's `FSharpGeneratorDriverIdentity` (assembly MVID + content hash)
  is retained only if the `AssemblyLoadContext` loader needs it for its own
  keying; otherwise retire it.

## 10. Test Migration Plan

The existing 123 tests in `tests/FSharpNativeGenerator.Tests/Tests.fs` split
into categories with clear migration paths:

| Test category | Count (approx) | Migration |
|---|---|---|
| Placement/ordering (Prelude, BeforeFile, AfterFile, BeforeLastSourceFile, EndOfProject, cycles, duplicates, sig/impl pairs, final-file) | ~30 | Keep; re-target assertions from standalone `FSharpGeneratorDriverRunResult` to fork `FSharpSourceGeneratorRunResult`. The placement engine is still this repo's, so these test it directly via the adapter. |
| Loader (attribute, API version, abstract/generic/private, partial-load salvage, NuGet folder) | ~20 | Keep; loader is unchanged. |
| Config (CLI parser, response files, MSBuild items, analyzer config) | ~25 | Keep; config parsing is unchanged. |
| Cache identity / stamp / invalidation | ~15 | **Rewrite**: these tested the standalone driver's stamp/cache. Replace with tests over `FSharpGeneratorHost.RunGenerators` / `.ParseAndCheck` asserting the fork's stamp/cache behavior (second-call cache hit, additional-file content change invalidates, generator-set change invalidates, `ClearCaches` flushes). Mirror the fork's `RunCache_*` tests. |
| Compile/emit (in-memory store, emit-to-disk, read-only dir) | ~10 | **Rewrite** over `FSharpGeneratorHost.Compile` / `.ParseAndCheck`; assert `EmitGeneratedFiles=false` compiles from memory, read-only output emits warning but compiles. Mirror the fork's `*_EmitFalseCompilesFromMemory` / `*_ReadOnlyOutputDirectoryEmitsWarningButCompiles`. |
| CLI host | ~5 | Keep; simplified CLI still parses and delegates. |
| `RunGeneratorsAndUpdateProjectOptions` shape | ~3 | Rewrite over `FSharpGeneratorHost.RunGenerators` (returns updated `FSharpProjectOptions` with `Stamp`). |

Target: keep ~75 tests as-is (loader/config/placement), rewrite ~33 to exercise
the fork-backed host. The rewritten tests are the binding's integration
contract with the fork.

### 10.1 New binding-specific tests

- `Adapter_TranslatesPreludeToBeforeFirstOriginal`
- `Adapter_TranslatesBeforeLastSourceFileToBeforeLastOriginalImpl`
- `Adapter_RejectsEndOfProjectForApplications` (FSG0012 fires before fork sees it)
- `Adapter_HardenedEnginePassingProducesNoForkOrderingWarnings`
- `Host_LoadFromConfiguration_AdaptsAllLoadedGenerators`
- `Host_ParseAndCheck_ResolvesGeneratedSymbolWithEmitFalse`
- `Host_Compile_WritesDllAndReturnsDiagnostics`
- `Host_NonDeterministicGenerator_StickyOnRunCache` (documents §11)

## 11. Non-Determinism & Invalidation Contract

Inherited from the fork, restated for binding consumers:

- The fork's run-cache key (`computeRunKey`) excludes generated-source content.
  For a **non-deterministic** generator whose inputs are stable but whose output
  varies, the run cache returns the **first** observed output for the lifetime
  of the cache entry. Typecheck correctness still holds: the project `Stamp`
  (`computeStamp`) includes generated content, so the incremental builder is
  always consistent with the cached output.
- **To invalidate cleanly**, drive content changes through `AdditionalFiles`
  (whose contents are in both `computeRunKey` and `computeStamp`). Do not rely
  on `SourceFiles` content changes to invalidate the run cache — the fork's
  context gives paths only, and source-content invalidation flows through the
  project `Stamp` / builder timestamp, not the run cache.
- The binding documents this in its public doc comments on
  `FSharpGeneratorHost` and on `IFSharpIncrementalGenerator` (recommendation:
  "generators should be deterministic functions of their inputs; if not, bump
  `AdditionalFiles` content to force re-evaluation").

## 12. Implementation Phases

Each phase is independently buildable and testable.

### Phase 1 — Retarget (mechanical, no behavior change yet)

1. Add `Directory.Build.props` with `$(FSharpRepoRoot)`.
2. Swap `FSharpNativeGenerator.fsproj`'s FCS package ref for the fork
   `<ProjectReference>` (netstandard2.0).
3. Build; expect errors from shadow-type collisions. Do not delete types yet —
   this phase only proves the reference resolves.

### Phase 2 — Shadow type removal

4. Delete `FSharpSourceText`, shadow `FSharpProjectOptions`, `FSharpOutputKind`,
   `FSharpDiagnosticSeverity`, `SourceRange`, `FSharpGeneratedSourceStore`,
   `FSharpGeneratorDriverOptions`/`RunResult`/`Report` from `Types.fs`.
5. Update `Incremental.fs`, `LoadingAndConfiguration.fs`, `GeneratedSource.fs`
   to use FCS real types and `string` source text.
6. Build the library green (tests will be broken; that's fine for this phase).

### Phase 3 — Placement engine retarget

7. Split the hardened placement engine out of `GeneratedSource.fs` into
   `Placement.fs`, retargeted to emit fork `FSharpGeneratedSource` records with
   `Order` computed per §8.
8. Add the FSG0012 final-file enforcement in the adapter path (the engine
   already has it; ensure it fires before fork emission).
9. Unit-test the placement engine directly (the ~30 placement tests, retargeted).

### Phase 4 — Adapter (Layer B)

10. Write `IncrementalGeneratorAdapter` per §6.
11. Wire `IFSharpSourceGeneratorWithId` through.
12. Test the adapter in isolation (the §10.1 adapter tests).

### Phase 5 — Host facade (Layer C)

13. Write `FSharpGeneratorHost` per §7.
14. Delete `Driver.fs`, the standalone store/run-result types, and the stale
    `CacheIdentity.fs` parts.
15. Rewrite the cache/compile/emit tests over the host (§10, rewritten set).

### Phase 6 — CLI

16. Simplify `Program.fs` to delegate to `FSharpGeneratorHost.Compile`.
17. Re-run CLI tests.

### Phase 7 — Full green + doc

18. `dotnet test` green for the whole repo.
19. Update README with the `$(FSharpRepoRoot)` override, the
    `AdditionalFiles`-driven invalidation contract (§11), and a minimal
    end-to-end generator example using `FSharpGeneratorHost.ParseAndCheck`.

## 13. Open Questions (decide before Phase 4)

- **Analyzer-config parsing location**: parse `AnalyzerConfigFiles` in the
  adapter (preserves the standalone lib's per-path options behavior) vs. leave
  to the host. Recommendation: parse in the adapter, feed
  `AnalyzerConfigOptionsProvider`, so author-facing behavior is unchanged.
- **`IFSharpSourceGeneratorWithId` exposure**: should the binding expose a
  Layer-A `IFSharpIncrementalGeneratorWithId` that authors implement, mapped to
  the fork's `IFSharpSourceGeneratorWithId`? Recommendation: yes, for stable
  generated paths across renames/refactors.
- **Keep the incremental API at all?** This spec recommends **yes** (Option A:
  ergonomics layer, fresh per call). The alternative (Option B: collapse to the
  fork's sync `Generate`) is a smaller surface but breaks every existing
  generator that uses `RegisterSourceOutput` / providers. Defer Option B until
  the fork itself exposes an incremental context.
