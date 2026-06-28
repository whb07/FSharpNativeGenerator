# MVP Binding Spec: FSharpNativeGenerator → forked FSharpChecker

## Recommendation

For a good MVP:

- Drop CLI from the MVP definition.
- Focus on:
  1. loader
  2. adapter
  3. placement
  4. host facade
  5. integration tests using `FSharpChecker`

The MVP is useful when a generator assembly can be loaded, adapted to the fork's public source-generator interface, run through `FSharpChecker`, and have generated source participate in parse/check/compile.

A standalone CLI can be added later as a thin smoke-test/debugging wrapper over the same host facade. It is not required to prove the core architecture.

## MVP Goal

Provide a small, correct, testable binding layer between the author-facing `FSharpNativeGenerator` API and the forked F# compiler's public source-generator API.

The MVP must prove this path:

```text
generator assembly path
  -> loader discovers IFSharpIncrementalGenerator
  -> adapter wraps it as IFSharpSourceGenerator
  -> placement resolves generated source order
  -> FSharpGeneratorHost delegates to FSharpChecker
  -> FSharpChecker parse/check/compile sees generated source
```

## Non-Goals For MVP

The MVP should explicitly defer:

- standalone CLI
- NuGet analyzer-folder discovery
- full MSBuild target/task implementation
- full analyzer-config parsing
- generated-source run reports
- semantic/typed-tree providers
- fixed-point generation
- node-level incremental caching inside one generator run
- full migration of the old 123-test suite
- packaging

## Public Namespace

All public author-facing and host-facing types remain under:

```fsharp
namespace FSharp.Compiler.SourceGeneration
```

Care must be taken not to redefine public types already provided by the forked `FSharp.Compiler.Service` unless the type is intentionally an author-facing binding abstraction.

In particular, prefer using the fork's real types for:

```fsharp
FSharp.Compiler.SourceGeneration.IFSharpSourceGenerator
FSharp.Compiler.SourceGeneration.IFSharpSourceGeneratorWithId
FSharp.Compiler.SourceGeneration.FSharpSourceGeneratorContext
FSharp.Compiler.SourceGeneration.FSharpSourceGeneratorOutput
FSharp.Compiler.SourceGeneration.FSharpSourceGeneratorOptions
FSharp.Compiler.SourceGeneration.FSharpGeneratedSource
FSharp.Compiler.SourceGeneration.FSharpGeneratedSourceKind
FSharp.Compiler.SourceGeneration.FSharpGeneratedSourceOrder
FSharp.Compiler.SourceGeneration.FSharpSourceGeneratorRunResult
FSharp.Compiler.SourceGeneration.FSharpSourceGeneratorDiagnostic
FSharp.Compiler.CodeAnalysis.FSharpChecker
FSharp.Compiler.CodeAnalysis.FSharpProjectOptions
```

## 1. Loader MVP

### Purpose

Load generator assemblies and discover valid author-facing generators.

### Required behavior

The loader must:

- accept one assembly path
- load the assembly in an isolated/loadable context suitable for analyzers
- inspect public, non-abstract, non-generic classes
- require `[<FSharpGenerator>]`
- require implementation of `IFSharpIncrementalGenerator`
- require supported API version
- require a public parameterless constructor for MVP
- instantiate all valid generators
- return diagnostics for invalid assemblies/types

### MVP signature

```fsharp
type FSharpGeneratorAssemblyLoadResult =
    { Generators: LoadedFSharpGenerator list
      Diagnostics: FSharpSourceGeneratorDiagnostic list }

and LoadedFSharpGenerator =
    { Generator: IFSharpIncrementalGenerator
      GeneratorId: string
      AssemblyPath: string
      TypeName: string }

module FSharpGeneratorAssemblyLoader =
    val loadFromPath : path: string -> FSharpGeneratorAssemblyLoadResult
```

### Generator ID

For MVP, `GeneratorId` should be deterministic:

```text
<AssemblyName>/<FullTypeName>
```

If an author-facing stable-id interface is added later, it can override this.

```fsharp
type IFSharpIncrementalGeneratorWithId =
    abstract GeneratorId: string
```

### Minimum diagnostics

The loader should produce stable FSG diagnostics:

```text
FSG0001 generator assembly load failed
FSG0002 generator type is missing FSharpGeneratorAttribute or IFSharpIncrementalGenerator
FSG0015 generator references an unsupported source-generation API version
```

## 2. Adapter MVP

### Purpose

Adapt the author-facing incremental generator API to the fork's public synchronous generator API.

The adapter implements:

```fsharp
IFSharpSourceGenerator
IFSharpSourceGeneratorWithId
```

### Required behavior

The adapter must:

- call `Initialize` once per adapter instance
- collect post-initialization outputs
- collect registered source outputs
- create an MVP project snapshot from `FSharpSourceGeneratorContext`
- run post-init outputs first
- run source outputs second
- run placement validation/resolution
- map generated source to fork `FSharpGeneratedSource`
- map diagnostics to fork `FSharpSourceGeneratorDiagnostic`

### MVP signature

```fsharp
[<Sealed>]
type IncrementalGeneratorAdapter =
    new:
        inner: IFSharpIncrementalGenerator *
        generatorId: string -> IncrementalGeneratorAdapter

    interface IFSharpSourceGenerator
    interface IFSharpSourceGeneratorWithId
```

### Author-facing generator interface

```fsharp
type IFSharpIncrementalGenerator =
    abstract Initialize: FSharpIncrementalGeneratorInitializationContext -> unit
```

### Initialization context MVP

```fsharp
type FSharpIncrementalGeneratorInitializationContext =
    member ProjectOptionsProvider:
        FSharpIncrementalValueProvider<FSharpGeneratorProjectSnapshot>

    member SourceFilesProvider:
        FSharpIncrementalValuesProvider<FSharpSourceFileInput>

    member AdditionalTextsProvider:
        FSharpIncrementalValuesProvider<FSharpAdditionalTextInput>

    member AnalyzerConfigOptionsProvider:
        FSharpIncrementalValueProvider<FSharpAnalyzerConfigOptions>

    member RegisterPostInitializationOutput:
        Action<FSharpPostInitializationContext> -> unit

    member RegisterSourceOutput<'T>:
        source: FSharpIncrementalValueProvider<'T> *
        action: Action<FSharpSourceProductionContext, 'T> -> unit

    member RegisterSourceOutput<'T>:
        source: FSharpIncrementalValuesProvider<'T> *
        action: Action<FSharpSourceProductionContext, 'T> -> unit
```

### Snapshot/input MVP types

The fork context gives source paths, not source text. The MVP should make that explicit.

```fsharp
type FSharpGeneratorProjectSnapshot =
    { ProjectFileName: string option
      ProjectDirectory: string
      SourceFiles: string list
      OtherOptions: string list
      References: string list
      DefineConstants: string list
      OutputFile: string option
      AssemblyName: string option }

type FSharpSourceFileInput =
    { Path: string
      IsSignatureFile: bool }

type FSharpAdditionalTextInput =
    { Path: string
      Text: string }

type FSharpAnalyzerConfigOptions =
    { GlobalOptions: IReadOnlyDictionary<string, string>
      GetOptionsForPath: string -> IReadOnlyDictionary<string, string> }
```

### Production contexts

```fsharp
type FSharpPostInitializationContext =
    member CancellationToken: CancellationToken

    member AddImplementationSource:
        hintName: string * sourceText: string -> unit

    member ReportDiagnostic:
        FSharpGeneratorDiagnostic -> unit

type FSharpSourceProductionContext =
    member CancellationToken: CancellationToken

    member AddImplementationSource:
        hintName: string *
        sourceText: string *
        placement: FSharpGeneratedSourcePlacement -> unit

    member AddSignatureSource:
        hintName: string *
        sourceText: string *
        companionImplementationHintName: string *
        placement: FSharpGeneratedSourcePlacement -> unit

    member ReportDiagnostic:
        FSharpGeneratorDiagnostic -> unit
```

### Adapter output mapping

```fsharp
// Author pending output
{ HintName = "Generated.fs"
  SourceText = "module Generated"
  Kind = FSharpGeneratedSourceKind.Implementation
  Placement = BeforeLastSourceFile }

// Fork output
{ HintName = "Generated.fs"
  FileName = stableGeneratedPath
  SourceText = "module Generated"
  Kind = FSharpGeneratedSourceKind.Implementation
  Order = FSharpGeneratedSourceOrder.BeforeFile lastOriginalFile }
```

For MVP, `FileName` should be stable and deterministic:

```text
<project-dir>/obj/Generated/FSharp/<generator-id>/<hint-name>
```

When `ProjectDirectory` is unavailable, use `Directory.GetCurrentDirectory()`.

## 3. Placement MVP

### Purpose

Validate author placement requests and translate them to fork `FSharpGeneratedSourceOrder` values.

### Author-facing placement vocabulary

```fsharp
type FSharpGeneratedSourcePlacement =
    | Prelude
    | BeforeFile of anchorPath: string
    | AfterFile of anchorPath: string
    | BeforeLastSourceFile
    | EndOfProject
```

### MVP signature

```fsharp
type PendingGeneratedSource =
    { GeneratorId: string
      HintName: string
      FileName: string
      SourceText: string
      Kind: FSharpGeneratedSourceKind
      Placement: FSharpGeneratedSourcePlacement
      CompanionImplementationHintName: string option }

module FSharpGeneratedSourcePlacementResolver =
    val resolve:
        originalFiles: string list ->
        otherOptions: string list ->
        generated: PendingGeneratedSource list ->
            Result<FSharpGeneratedSource list * FSharpSourceGeneratorDiagnostic list,
                   FSharpSourceGeneratorDiagnostic list>
```

### Required MVP validation

The placement resolver must:

- reject duplicate generated hint names within the same generator/run
- reject missing anchors
- reject anchors that target generated files
- reject `EndOfProject` for applications
- handle an empty original source list without throwing
- map all generated sources to fork orders using only original-file anchors

### Required MVP placement mapping

```text
Prelude              -> BeforeFile firstOriginal, or EndOfProject if no originals
BeforeFile anchor    -> BeforeFile anchor
AfterFile anchor     -> AfterFile anchor
BeforeLastSourceFile -> BeforeFile lastOriginalImplementation, or EndOfProject if none
EndOfProject         -> EndOfProject
```

### Application detection

MVP should detect applications from `OtherOptions`:

```text
--target:exe
--target=exe
--target exe
--target:winexe
--target=winexe
--target winexe
```

### Minimum diagnostics

```text
FSG0006 duplicate generated hint name within one generator
FSG0007 invalid generated file order or missing placement anchor
FSG0012 generated source placement would break F# final-file rules
FSG0013 generated source kind does not match file extension
FSG0014 generated signature for user implementation is not supported in V1
```

For MVP, cycle detection is mostly avoided by forbidding generated-file anchors.

## 4. Host Facade MVP

### Purpose

Expose a small reusable API that hosts can call. This is the MVP replacement for a CLI.

### Signature

```fsharp
[<Sealed>]
type FSharpGeneratorHost =
    new: ?checker: FSharpChecker -> FSharpGeneratorHost

    member Checker: FSharpChecker

    member LoadFromConfiguration:
        config: FSharpSourceGeneratorConfiguration -> LoadedFSharpGenerator list * FSharpSourceGeneratorDiagnostic list

    member RunGenerators:
        options: FSharpProjectOptions *
        generators: LoadedFSharpGenerator list *
        generatorOptions: FSharpSourceGeneratorOptions ->
            Async<FSharpProjectOptions * FSharpSourceGeneratorRunResult>

    member ParseAndCheck:
        options: FSharpProjectOptions *
        generators: LoadedFSharpGenerator list *
        generatorOptions: FSharpSourceGeneratorOptions ->
            Async<FSharpCheckProjectResults * FSharpSourceGeneratorRunResult>

    member Compile:
        argv: string array *
        generators: LoadedFSharpGenerator list *
        generatorOptions: FSharpSourceGeneratorOptions ->
            Async<FSharpDiagnostic array * FSharpSourceGeneratorRunResult * exn option>
```

### Required behavior

The host must:

- call the loader for all configured generator paths
- wrap every loaded generator with `IncrementalGeneratorAdapter`
- pass fork `FSharpSourceGeneratorOptions` through unchanged
- delegate to the fork's public `FSharpChecker` methods
- not implement its own cache, store, stamp, or compilation driver

## 5. Configuration MVP

The MVP configuration parser can be small. It only needs to create the data needed by tests and host consumers.

### Signature

```fsharp
type FSharpSourceGeneratorConfiguration =
    { GeneratorPaths: string list
      AdditionalFilePaths: string list
      AnalyzerConfigPaths: string list }

module FSharpSourceGeneratorConfiguration =
    val empty: FSharpSourceGeneratorConfiguration

    val parseCommandLineLikeArguments:
        args: string list -> FSharpSourceGeneratorConfiguration * string list * FSharpSourceGeneratorDiagnostic list
```

### Supported switches for MVP

```text
--fsharp-source-generator:<path>
--fsharp-generator-additional-file:<path>
--fsharp-source-generator-analyzer-config:<path>
```

Generated-file emission options can be parsed later or passed directly as fork `FSharpSourceGeneratorOptions`.

## 6. Integration Tests MVP

The MVP is not complete until these tests pass.

### Loader tests

```text
Loader_LoadsPublicAttributedGenerator
Loader_RejectsMissingAttribute
Loader_RejectsUnsupportedApiVersion
Loader_ReportsAssemblyLoadFailure
```

### Adapter tests

```text
Adapter_RunsPostInitializationOutput
Adapter_RunsSourceOutputForAdditionalFile
Adapter_MapsDiagnosticsToForkDiagnostics
Adapter_ProducesStableGeneratedFileName
```

### Placement tests

```text
Placement_PreludeBeforeFirstOriginal
Placement_BeforeLastSourceFileBeforeLastImplementation
Placement_EndOfProjectRejectedForApplication
Placement_DuplicateHintRejected
Placement_MissingAnchorRejected
Placement_EmptyOriginalsDoesNotThrow
```

### Host/FSharpChecker integration tests

```text
Host_LoadFromConfigurationLoadsGenerators
Host_RunGeneratorsUpdatesProjectSourceFiles
Host_ParseAndCheckSeesGeneratedSymbol
Host_CompileSucceedsWithGeneratedSymbol
Host_AdditionalFileChangeInvalidatesForkRunCache
```

### Explicitly deferred tests

```text
StandaloneCli_SmokeTest
CommandLineFsc_WorksWithoutMSBuild
NuGetAnalyzerFolder_LoadsGenerator
FullMSBuildTarget_LoadsGenerator
```

## MVP Done Definition

The MVP is done when:

- the main library builds with zero warnings and zero errors
- test generators build against the new author-facing API
- MVP tests pass
- no standalone CLI is required
- no old standalone driver/store/cache is used
- generated files are visible to `FSharpChecker.ParseAndCheckProjectWithSourceGenerators`
- generated files are visible to `FSharpChecker.CompileWithSourceGenerators`
- loader/config/adapter/placement/host are real implementations, not stubs

## Deferred CLI Design

When added later, the CLI should be a thin wrapper:

```text
parse args
  -> build FSharpSourceGeneratorConfiguration
  -> build FSharpProjectOptions or argv
  -> host.LoadFromConfiguration
  -> host.Compile or host.ParseAndCheck
  -> print diagnostics
```

It must not contain a separate driver, cache, placement engine, source store, or assembly loader.
