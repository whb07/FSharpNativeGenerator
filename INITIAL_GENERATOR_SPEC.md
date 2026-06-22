# Native F# Source Generator Spec

This is the initial design direction for a native F# source-generation pipeline in
`dotnet/fsharp`.

The key correction from a Roslyn-first mental model is this: Roslyn's generator
driver is useful as an orchestration pattern, but F# source generation must be
designed around F#'s ordered compilation model, signature-file rules, FCS cache
identity, and IDE snapshot behavior.

## Core Goal

Implement a compiler-hosted, native F# source-generation pipeline that:

* Lets generators add `.fs` and generated `.fsi` source files to an F# project.
* Keeps generation additive and deterministic.
* Works from `fsc`, MSBuild, and FSharp.Compiler.Service/F# IDE hosts.
* Uses F# compiler concepts instead of Roslyn `Compilation` or `SyntaxTree`.
* Preserves F# source-file ordering semantics.
* Gives the compiler and IDE stable generated paths, diagnostics, and run results.

The implementation should model Roslyn's `GeneratorDriver` at the driver level:
immutable/reusable state, explicit initialization, generator run results, and
separate generated-source materialization. It should not copy Roslyn's unordered
language assumptions.

## Non-Goals For V1

These are intentionally out of scope for the first design:

* Rewriting, deleting, or replacing existing user source.
* Letting generators implement new F# language features or dialects.
* Letting one generator depend on another generator's outputs in the same run.
* A default fixed-point generation loop.
* Public exposure of internal typed-tree structures as a stable generator API.
* Generated `.fsi` files that act as signatures for existing user `.fs` files.
* Correctness that depends on generated files being emitted to disk.
* Reusing C# Roslyn `Compilation`, `SyntaxTree`, or `SemanticModel`.

Generated source is additive: it can introduce new modules, namespaces, types,
members via normal F# constructs, extension members, and generated helper code.
It cannot change the meaning of an existing user file except through normal F#
name resolution when the generated file is placed before a consuming file.

## F# Constraints

F# source generation has several constraints that must be first-class in the
design:

* `FSharpProjectOptions.SourceFiles` is a `string[]` and represents the ordered
  source list for a project.
* F# source order is semantic. A file can generally reference declarations from
  earlier files, not later files.
* Only the final implementation file of an application may omit an enclosing
  namespace or module. Generated files must not accidentally move that file out
  of the final position.
* Signature files must precede their matching implementation files.
* A generated signature file for a user implementation file is not additive,
  because it can hide members or change exported API. V1 rejects this.
* FCS and IDE hosts need in-memory generated source, stable paths, and cache
  identity independent of whether generated files are written to `obj`.
* All generator configuration must participate in project/check/cache identity.

## High-Level Pipeline

The V1 pipeline should be single-pass from the user's point of view:

```text
Project options + source snapshots + additional files + analyzer config
   |
   v
Load generator assemblies and discover generator types
   |
   v
Initialize incremental generator graphs
   |
   v
Run post-initialization outputs
   |
   v
Create pre-generation F# project snapshot
   |
   v
Parse original source + post-init generated source
   |
   v
Run generator source outputs against stable inputs
   |
   v
Validate and resolve generated file placement
   |
   v
Update ordered source list and generated source store
   |
   v
Parse/check full project
   |
   v
Emit
```

Generators do not see generated source from other generators as ordinary inputs
in V1. Post-initialization output is the only generated source visible before
normal generator execution, and it must be independent of project source.

## Generator Phases

### 1. Loading And Discovery

The compiler receives generator assembly paths from compiler options/MSBuild.
Each assembly is loaded by a generator assembly loader using isolation rules
compatible with analyzer loading.

Discovery should require both:

* A public, non-abstract type implementing `IFSharpIncrementalGenerator`.
* A marker attribute such as `[<FSharpGenerator>]`.

This avoids accidentally loading arbitrary helper types from a referenced
assembly.

Generators cannot be used to build the same assembly that defines them.

### 2. Initialization

`Initialize` is called once per driver instance. The generator registers
incremental pipelines and output actions. It should not read project state
directly during initialization.

### 3. Post-Initialization Output

Post-initialization output has no project inputs. It is useful for generated
marker attributes, helper modules, and generated support types.

For F#, post-init generated implementation files are placed in a generated
prelude region before original source files. They must contain an explicit
namespace or module declaration.

### 4. Input Snapshot

The driver builds a stable input snapshot from:

* `FSharpProjectOptions`
* ordered source file paths
* source text checksums
* parse options
* additional files
* analyzer config/global config options
* generator assembly identities
* host kind: command line, build, or IDE

The input snapshot is the unit used by the generator driver, while
`FSharpProjectOptions` remains the public FCS/compiler carrier for project
checking.

### 5. Generation

Generators produce source and diagnostics through `FSharpSourceProductionContext`.
Generated source must include an explicit placement. If a generator cannot know
the placement, it should choose a conservative placement helper such as
`BeforeLastSourceFile`; the driver should not silently append standard outputs
to the end of the project.

### 6. Placement Resolution

The driver assigns stable generated paths, resolves placement requests, checks
for cycles/conflicts, updates the source list, and registers generated text with
the compiler's generated-source store.

Disk emission is optional and for inspection/debugging only.

## Public API Shape

The public API should live under a new namespace:

```fsharp
namespace FSharp.Compiler.SourceGeneration
```

It can ship from `FSharp.Compiler.Service` or a small companion abstractions
assembly, but generators should not reference compiler-internal assemblies.

### Core Types

```fsharp
namespace FSharp.Compiler.SourceGeneration

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Threading
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text

[<AttributeUsage(AttributeTargets.Class, AllowMultiple = false)>]
type FSharpGeneratorAttribute() =
    inherit Attribute()

type FSharpGeneratedSourceKind =
    | Implementation
    | Signature

type FSharpGeneratedSourcePlacement =
    /// Before all original source files. Intended for post-init/prelude output.
    | Prelude

    /// Immediately before the specified original or generated source file.
    | BeforeFile of anchorPath: string

    /// Immediately after the specified original or generated source file.
    | AfterFile of anchorPath: string

    /// Immediately before the final original source file.
    /// This preserves the last-file application rule.
    | BeforeLastSourceFile

    /// After all original source files. Invalid when this would break
    /// the final-source-file rule for applications.
    | EndOfProject

type FSharpGeneratedSource =
    {
        GeneratorName: string
        HintName: string
        ResolvedPath: string
        Kind: FSharpGeneratedSourceKind
        SourceText: ISourceText
        Placement: FSharpGeneratedSourcePlacement
        Checksum: ImmutableArray<byte>
    }

type FSharpGeneratorDiagnostic =
    {
        Id: string
        Message: string
        Severity: FSharpDiagnosticSeverity
        Range: range option
        FilePath: string option
    }

type FSharpSourceFileSnapshot =
    {
        Path: string
        SourceText: ISourceText
        Checksum: ImmutableArray<byte>
        IsSignatureFile: bool
    }

type FSharpAdditionalText =
    {
        Path: string
        GetText: CancellationToken -> ISourceText option
        Checksum: ImmutableArray<byte> option
    }

type FSharpAnalyzerConfigOptions =
    {
        GlobalOptions: IReadOnlyDictionary<string, string>
        GetOptionsForPath: string -> IReadOnlyDictionary<string, string>
    }

type FSharpGeneratorProjectSnapshot =
    {
        ProjectOptions: FSharpProjectOptions
        SourceFiles: ImmutableArray<FSharpSourceFileSnapshot>
        AdditionalTexts: ImmutableArray<FSharpAdditionalText>
        AnalyzerConfigOptions: FSharpAnalyzerConfigOptions
    }
```

### Incremental Generator API

The public generator API should be incremental-first. A first implementation may
invalidate broadly internally, but it should not expose a legacy execute-only
API that becomes permanent surface area.

```fsharp
type FSharpIncrementalValueProvider<'T> = interface end

type FSharpIncrementalValuesProvider<'T> = interface end

type FSharpPostInitializationContext =
    abstract CancellationToken: CancellationToken
    abstract AddImplementationSource:
        hintName: string *
        sourceText: ISourceText ->
            unit

    abstract ReportDiagnostic:
        diagnostic: FSharpGeneratorDiagnostic ->
            unit

type FSharpSourceProductionContext =
    abstract CancellationToken: CancellationToken

    abstract AddImplementationSource:
        hintName: string *
        sourceText: ISourceText *
        placement: FSharpGeneratedSourcePlacement ->
            unit

    abstract AddSignatureSource:
        hintName: string *
        sourceText: ISourceText *
        companionImplementationHintName: string *
        placement: FSharpGeneratedSourcePlacement ->
            unit

    abstract ReportDiagnostic:
        diagnostic: FSharpGeneratorDiagnostic ->
            unit

type FSharpIncrementalGeneratorInitializationContext =
    abstract ProjectOptionsProvider:
        FSharpIncrementalValueProvider<FSharpProjectOptions>

    abstract SourceFilesProvider:
        FSharpIncrementalValuesProvider<FSharpSourceFileSnapshot>

    abstract AdditionalTextsProvider:
        FSharpIncrementalValuesProvider<FSharpAdditionalText>

    abstract AnalyzerConfigOptionsProvider:
        FSharpIncrementalValueProvider<FSharpAnalyzerConfigOptions>

    abstract RegisterPostInitializationOutput:
        action: Action<FSharpPostInitializationContext> ->
            unit

    abstract RegisterSourceOutput<'T>:
        source: FSharpIncrementalValueProvider<'T> *
        action: Action<FSharpSourceProductionContext, 'T> ->
            unit

    abstract RegisterSourceOutput<'T>:
        source: FSharpIncrementalValuesProvider<'T> *
        action: Action<FSharpSourceProductionContext, 'T> ->
            unit

type IFSharpIncrementalGenerator =
    abstract Initialize:
        context: FSharpIncrementalGeneratorInitializationContext ->
            unit
```

Typed/semantic providers should be added carefully after the parse-only and
additional-file scenarios are proven. If a semantic provider is exposed, it
should expose stable `FSharp.Compiler.Service` symbols/results rather than
compiler-internal typed tree nodes.

## Driver API

The driver must be immutable. Like Roslyn, running generators returns an updated
driver, because the driver carries initialized generator state and incremental
cache state.

```fsharp
type FSharpGeneratorDriverOptions =
    {
        EmitGeneratedFiles: bool
        GeneratedFilesOutputPath: string option
        ReportPath: string option
        MaxGenerationPasses: int
        HostKind: FSharpGeneratorHostKind
    }

and FSharpGeneratorHostKind =
    | CommandLine
    | MSBuild
    | IDE

type FSharpGeneratorDriverRunResult =
    {
        GeneratedSources: ImmutableArray<FSharpGeneratedSource>
        Diagnostics: ImmutableArray<FSharpGeneratorDiagnostic>
        UpdatedSourceFiles: ImmutableArray<string>
        ElapsedMilliseconds: int64
    }

type FSharpGeneratorDriver =
    static member Create:
        generators: ImmutableArray<IFSharpIncrementalGenerator> *
        options: FSharpGeneratorDriverOptions ->
            FSharpGeneratorDriver

    member RunGenerators:
        projectSnapshot: FSharpGeneratorProjectSnapshot *
        cancellationToken: CancellationToken ->
            FSharpGeneratorDriver * FSharpGeneratorDriverRunResult

    member RunGeneratorsAndUpdateProjectOptions:
        projectSnapshot: FSharpGeneratorProjectSnapshot *
        cancellationToken: CancellationToken ->
            FSharpGeneratorDriver * FSharpProjectOptions * FSharpGeneratorDriverRunResult
```

The updated `FSharpProjectOptions` must include generated source paths in
`SourceFiles` and must have cache identity that changes when generator
configuration or generated source changes.

## Generated Source Storage

Generated sources need stable paths and in-memory text.

Recommended model:

* The driver owns a generated-source store keyed by `ResolvedPath`.
* The compiler/FCS file system layer can resolve generated paths from this store.
* When `EmitGeneratedFiles` is true, the same contents are also written under
  the configured output directory.
* Disk output must never be required for correctness.

Generated paths should be deterministic:

```text
<generated-root>/<generator-assembly-or-id>/<sanitized-hint-name>.fs
<generated-root>/<generator-assembly-or-id>/<sanitized-hint-name>.fsi
```

If two generators use the same hint name, the path remains unique because the
generator identity is part of the path. If one generator emits the same hint name
twice in one run, the driver reports a duplicate-source diagnostic.

## File Ordering Rules

Placement resolution operates over original source files plus generated files.
Original source file order is fixed.

Rules:

* `Prelude` outputs are placed before all original source files.
* `BeforeFile` and `AfterFile` anchors must resolve to an original or generated
  source file in the same project.
* `BeforeLastSourceFile` is inserted immediately before the final original
  implementation source file.
* `EndOfProject` is inserted after all source files only when doing so does not
  break F#'s final-file rule.
* Generated `.fsi` files are only valid as companions for generated `.fs` files
  in V1.
* A generated `.fsi` companion must be immediately before its generated `.fs`
  implementation.
* Cycles, missing anchors, invalid companions, and duplicate generated hint
  names fail the generator run before full project checking.

Examples:

```text
Original:
  Domain.fs
  Program.fs

Generated helper from additional file:
  Prelude -> Generated/SchemaTypes.fs

Final order:
  Generated/SchemaTypes.fs
  Domain.fs
  Program.fs
```

```text
Original:
  Domain.fs
  Program.fs

Generated extension members for Domain:
  AfterFile "Domain.fs" -> Generated/Domain.Extensions.fs

Final order:
  Domain.fs
  Generated/Domain.Extensions.fs
  Program.fs
```

```text
Generated pair:
  Generated/Client.fsi
  Generated/Client.fs

Final order:
  Generated/Client.fsi
  Generated/Client.fs
  Domain.fs
  Program.fs
```

## Compiler Integration Points

Likely areas inside `dotnet/fsharp`:

```text
src/Compiler/SourceGeneration/
  FSharpSourceGeneratorTypes.fs
  FSharpSourceGeneratorContext.fs
  FSharpSourceGeneratorDriver.fs
  FSharpIncrementalGraph.fs
  FSharpGeneratedSourceStore.fs
  FSharpGeneratedSourceOrdering.fs
  FSharpGeneratorDiagnostics.fs
  FSharpGeneratorAssemblyLoader.fs
  FSharpGeneratorCache.fs
```

Likely integration points:

```text
src/Compiler/Driver/CompilerOptions.fs
src/Compiler/Driver/CompilerConfig.fs
src/Compiler/Service/BackgroundCompiler.fs
src/Compiler/Service/FSharpCheckerResults.fs
src/Compiler/Facilities/BuildGraph.fs
src/Compiler/fsc/fsc.fs
```

The most important correctness point: generated source identity must flow into
both command-line compilation and FCS project checking, or IDE/build behavior
will diverge.

## Command-Line And MSBuild Surface

Prefer reusing existing analyzer/additional-file/editorconfig plumbing where
possible. New names should be F#-specific only when the existing analyzer
surface cannot represent F# placement and generated-source output.

Proposed compiler options:

```text
--fsharp-source-generator:<path>
--fsharp-generator-additional-file:<path>
--emit-fsharp-generated-files[+|-]
--fsharp-generated-files-output:<dir>
--fsharp-source-generator-report:<path>
--fsharp-source-generator-analyzer-config:<path>
```

Potential MSBuild surface:

```xml
<ItemGroup>
  <FSharpSourceGenerator Include="path/to/MyGenerator.dll" />
  <FSharpGeneratorAdditionalFile Include="schema.json" />
</ItemGroup>

<PropertyGroup>
  <EmitFSharpGeneratedFiles>true</EmitFSharpGeneratedFiles>
  <FSharpGeneratedFilesOutputPath>$(IntermediateOutputPath)Generated/FSharp</FSharpGeneratedFilesOutputPath>
</PropertyGroup>
```

NuGet packaging should be specified explicitly before implementation. A likely
shape is a language-specific analyzer folder such as:

```text
analyzers/dotnet/fs/MyGenerator.dll
```

If the SDK already provides better analyzer-version selection by the time this
is implemented, use that instead of inventing a parallel package layout.

## Incremental Caching

There are two cache layers:

* Generator-driver incremental graph cache.
* FCS/compiler parse/check cache.

Both must observe generator inputs.

Driver cache inputs include:

```text
generator assembly path
generator assembly MVID/content hash
generator type name
generator API version
project file path and project id
source file path, order, and content checksum
additional file path and content checksum
analyzer/global config checksum
parse options
language version
define constants
referenced assembly identities
FSharp.Core identity
host kind
```

FCS/compiler project cache identity must also change when:

```text
generator set changes
generator options change
generated source path list changes
generated source content checksum changes
generated source ordering changes
```

If `FSharpProjectOptions.Stamp` is present, the implementation must either
update it or use a richer project snapshot identity so stale generated outputs
cannot be reused.

## Diagnostics

Initial diagnostic IDs:

```text
FSG0001 generator assembly load failed
FSG0002 generator type is missing FSharpGeneratorAttribute or IFSharpIncrementalGenerator
FSG0003 generator threw during initialization
FSG0004 generator threw during execution
FSG0005 generated source parse failed
FSG0006 duplicate generated hint name within one generator
FSG0007 invalid generated file order
FSG0008 generated signature/implementation mismatch
FSG0009 cyclic generated source dependency
FSG0010 fixed-point generation is not supported in V1
FSG0011 invalid generated hint name or generated path
FSG0012 generated source placement would break F# final-file rules
FSG0013 generated source kind does not match file extension
FSG0014 generated signature for user implementation is not supported in V1
FSG0015 generator references an unsupported source-generation API version
```

Diagnostic locations:

* Generator loading diagnostics usually have no source range.
* Additional-file diagnostics should point at the additional file when possible.
* Generated parse diagnostics should use the generated source path.
* Ordering diagnostics should mention the generated hint name and anchor path.

## Fixed-Point Generation

V1 should not implement fixed-point generation.

Reasoning:

* It makes generator ordering observable.
* It risks nondeterminism when generators produce different outputs across
  passes.
* It makes IDE invalidation harder.
* It departs from the most important Roslyn safety property: generators see the
  same stable input set.

If a future version wants fixed-point generation, it should be opt-in,
diagnostic-heavy, and specified as a separate feature with deterministic pass
ordering and compatibility rules.

## Validation Tests

Minimum tests:

```text
simple generated .fs file compiles
post-init generated attribute can be referenced from user source
generated file before all original files
generated file before target file
generated file after target file
generated file before final Program.fs preserves app last-file rule
EndOfProject rejects placement that would break app last-file rule
generated .fsi + generated .fs pair compiles
generated .fsi for user .fs is rejected
generated file extension must match source kind
missing placement anchor fails
cyclic generated ordering fails
duplicate hint names within one generator fail
same hint name across two generators remains path-unique
generator initialization exception becomes FSG0003
generator execution exception becomes FSG0004
generated parse error reports generated file path
additional file change reruns affected pipeline
source file content change invalidates affected generator input
source file order change invalidates generator input
generator assembly content change invalidates generator input
updated generated source changes FCS project cache identity
MSBuild item loads generator
NuGet analyzer-folder generator loads
emit generated files writes configured obj path
command-line fsc works without MSBuild
IDE/FCS parse/check sees generated files
cancellation is observed during generator execution
```

## MVP Implementation Order

1. Add public abstractions and internal driver skeleton.
2. Add generator assembly loading/discovery.
3. Implement immutable driver initialization and broad-invalidation incremental
   graph execution.
4. Implement post-init/prelude output.
5. Implement explicit source output placement and ordering validation.
6. Add generated-source store and update `FSharpProjectOptions.SourceFiles`.
7. Integrate with command-line `fsc`.
8. Add diagnostics and optional generated-file emission.
9. Add MSBuild item/property plumbing.
10. Add FCS/IDE integration and project cache identity tests.
11. Replace broad invalidation with real incremental caching.
12. Consider semantic providers only after parse/additional-file scenarios are
    stable.

This gives F# a native source-generation design that can plausibly work with the
compiler's ordered pipeline instead of wrapping C# Roslyn concepts in F# names.
