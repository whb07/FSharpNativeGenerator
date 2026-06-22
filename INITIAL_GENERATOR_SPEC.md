Below is the spec direction I’d use.

# Native F# Source Generator Spec

## Core goal

Implement a **native F# source generation pipeline** inside `dotnet/fsharp`, modeled after Roslyn’s `GeneratorDriver`, but using F# compiler concepts: `FSharpProjectOptions`, parse/check phases, typed AST, generated `.fs` / `.fsi` files, and F# file-order semantics.

Roslyn’s driver is the right architectural model: it owns generator state, runs generators, collects generated syntax trees, adds them back to the compilation, and exposes run results/diagnostics. In Roslyn this is centered around `GeneratorDriver`, which is explicitly “responsible for orchestrating a source generation pass” and maintains immutable reusable state. 

## Key Roslyn behaviors to mirror

Roslyn supports both old and incremental generators, but the old `ISourceGenerator` interface is deprecated in favor of `IIncrementalGenerator`.  The F# implementation should therefore **start incremental-first**, not with a legacy execute-only API.

Roslyn’s generator flow has these important phases:

1. Initialize generator pipelines.
2. Run post-init generation.
3. Add constant generated sources into the compilation.
4. Run pre-compilation generation.
5. Add pre-compilation outputs.
6. Run standard generation against the updated compilation.
7. Parse generated sources and add them to the output compilation.   

For F#, this becomes:

```text
Project options
   ↓
Original source files in declared order
   ↓
Parse early source set
   ↓
Run F# generator driver
   ↓
Materialize generated .fs/.fsi files
   ↓
Rebuild ordered source list
   ↓
Parse/check full project
   ↓
Emit
```

## New public API

Create a new assembly/package surface, likely under `FSharp.Compiler.CodeAnalysis`:

```fsharp
namespace FSharp.Compiler.SourceGeneration

type FSharpGeneratedSource =
    {
        HintName: string
        FileName: string
        SourceText: string
        IsSignatureFile: bool
        Order: FSharpGeneratedSourceOrder
    }

and FSharpGeneratedSourceOrder =
    | BeforeFile of string
    | AfterFile of string
    | EndOfProject
    | BeforeImplementation of string

type FSharpGeneratorDiagnostic =
    {
        Id: string
        Message: string
        Severity: FSharpDiagnosticSeverity
        Range: range option
    }

type FSharpGeneratorContext =
    {
        ProjectOptions: FSharpProjectOptions
        ParseResults: FSharpParseFileResults list
        CheckResults: FSharpCheckProjectResults option
        AdditionalTexts: IReadOnlyDictionary<string, string>
        AnalyzerConfigOptions: IReadOnlyDictionary<string, string>
        CancellationToken: CancellationToken
    }

type IFSharpIncrementalGenerator =
    abstract Initialize: FSharpIncrementalGeneratorInitializationContext -> unit
```

Do **not** make generators depend on C# Roslyn `Compilation` or `SyntaxTree`. Use F# concepts: source text, parsed input, typed symbols, project options, and file order.

## Compiler integration points

The existing F# service already exposes parse/check operations through `IBackgroundCompiler`, including `ParseFile`, `ParseAndCheckFileInProject`, and `ParseAndCheckProject`.  This source-generation pipeline should plug in just before full project checking.

`FSharpProjectOptions` is the natural project-level carrier. The service already treats project options as the unit for checking/cache identity.  Add generator configuration into this option identity carefully, or generated outputs may be incorrectly cached.

## New files/modules to add

```text
src/Compiler/SourceGeneration/
  FSharpSourceGeneratorTypes.fs
  FSharpSourceGeneratorContext.fs
  FSharpSourceGeneratorDriver.fs
  FSharpIncrementalGraph.fs
  FSharpGeneratedSourceParser.fs
  FSharpGeneratorDiagnostics.fs
  FSharpGeneratorAssemblyLoader.fs
  FSharpGeneratorCache.fs
```

Also update:

```text
src/Compiler/Driver/CompilerOptions.fs
src/Compiler/Driver/CompilerConfig.fs
src/Compiler/Service/BackgroundCompiler.fs
src/Compiler/Service/FSharpCheckerResults.fs
src/Compiler/Facilities/BuildGraph.fs
src/Compiler/fsc/fsc.fs
```

## Command-line/MSBuild surface

Add compiler flags:

```text
--source-generator:<path>
--source-generator-data:<path>
--source-generator-output:<dir>
--emit-generated-files[+|-]
--generated-files-output:<dir>
--source-generator-report:<path>
--source-generator-analyzer-config:<path>
```

MSBuild items:

```xml
<ItemGroup>
  <FSharpSourceGenerator Include="path/to/MyGenerator.dll" />
  <FSharpGeneratorAdditionalFile Include="schema.json" />
</ItemGroup>

<PropertyGroup>
  <EmitFSharpGeneratedFiles>true</EmitFSharpGeneratedFiles>
  <FSharpGeneratedFilesOutputPath>obj/Generated/FSharp</FSharpGeneratedFilesOutputPath>
</PropertyGroup>
```

## File ordering rule

This is the hardest F#-specific part. Unlike C#, F# compilation is order-sensitive. Generated files must have explicit placement.

Default rule:

```text
Generated implementation files go at EndOfProject.
Generated signature files must appear before their matching implementation.
Generators may request BeforeFile/AfterFile placement.
Cycles or invalid placement produce diagnostics and fail generation.
```

## Driver behavior

Implement:

```fsharp
type FSharpGeneratorDriver =
    static member Create:
        generators: IFSharpIncrementalGenerator list *
        options: FSharpGeneratorDriverOptions ->
            FSharpGeneratorDriver

    member RunGenerators:
        projectOptions: FSharpProjectOptions *
        sourceFiles: FSharpSourceFile list *
        cancellationToken: CancellationToken ->
            FSharpGeneratorDriverRunResult

    member RunGeneratorsAndUpdateProject:
        projectOptions: FSharpProjectOptions *
        cancellationToken: CancellationToken ->
            FSharpProjectOptions * FSharpGeneratorDriverRunResult
```

The F# driver should mimic Roslyn’s immutable/reusable driver state. Roslyn’s `RunGeneratorsAndUpdateCompilation` collects generated trees and returns an updated compilation.  F# should return updated `FSharpProjectOptions` plus generated source metadata.

## Incremental cache keys

Cache by:

```text
generator assembly path + MVID/hash
generator type name
project options hash
source file path + content hash
additional file path + content hash
analyzer config hash
FSharp.Core version
language version
define constants
referenced assemblies
```

## Diagnostics

Add diagnostic IDs:

```text
FSG0001 generator assembly load failed
FSG0002 generator type does not implement IFSharpIncrementalGenerator
FSG0003 generator threw during initialization
FSG0004 generator threw during execution
FSG0005 generated source parse failed
FSG0006 duplicate generated hint name
FSG0007 invalid generated file order
FSG0008 generated signature/implementation mismatch
FSG0009 cyclic generated source dependency
FSG0010 generator output changed after fixed point limit
```

## Fixed-point generation

Because F# generated files can introduce symbols used by later generated files, support bounded fixed-point mode:

```text
Pass 0: parse/check original project enough for generator inputs
Pass 1: generate sources
Pass 2: parse/check with generated sources
Pass 3: rerun only generators whose inputs changed
Stop when generated output hashes stabilize.
Default max passes: 2
Hard max: 5
```

## Validation tests

Create tests for:

```text
simple generated .fs file compiles
generated .fsi + .fs compiles
generated file before target file
generated file after target file
duplicate hint names fail
generator exception becomes FSG0004
generated parse error reports correct generated file
incremental cache skips unchanged generator
additional file change reruns generator
source file change reruns dependent generator only
MSBuild item loads generator
emit generated files writes obj/Generated/FSharp
IDE parse/check sees generated files
command-line fsc works without MSBuild
```

## MVP implementation order

1. Add generator types and driver.
2. Load generator assemblies from compiler options.
3. Run non-incremental single-pass generation.
4. Materialize generated `.fs` files into temp/in-memory source list.
5. Re-run project parse/check with generated files.
6. Add diagnostics and generated-file emission.
7. Add MSBuild item plumbing.
8. Add incremental graph/caching.
9. Add IDE/FCS support.
10. Add fixed-point mode.

This gives F# a real native source generator system instead of a C# Roslyn wrapper.

