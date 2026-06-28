# Scenario Generator Spec

This spec extends the current binding-layer MVP so real generators can target
four representative scenarios:

1. Typed JSON/config access from an additional file.
2. OpenAPI client generation.
3. Native binding generation from a simple C header.
4. Typed SQL access from `.sql` files.

The common theme is not that the host should understand JSON, OpenAPI, C, or
SQL. The host should provide a correct, deterministic additional-file and
configuration contract so generator assemblies can implement those domains.

## Current State

The repo already has a useful source-generator core:

- Generator discovery through `[<FSharpGenerator>]` and
  `IFSharpIncrementalGenerator`.
- A small incremental-provider facade with project, source-file,
  additional-file, and analyzer-config providers.
- Source production APIs for generated `.fs` and generated `.fsi` files.
- Placement validation for F# source-order rules.
- A host facade over the forked `FSharpChecker` source-generator APIs.

The current surface is enough for simple tests that map an additional file's
text to one generated module. It is not yet enough for production scenario
generators because additional files do not carry metadata, analyzer config is
empty, diagnostics lose file-path information when mapped to the fork diagnostic
type, and provider combinators are too small for multi-file workflows.

## Design Goals

- Keep generation additive: generators only add `.fs` or `.fsi` files.
- Keep the host domain-neutral: JSON, OpenAPI, C, and SQL parsing live in
  generator assemblies.
- Make additional files first-class inputs with stable identity, text,
  metadata, and per-file options.
- Make outputs deterministic from input path, input content, generator version,
  and analyzer/MSBuild metadata.
- Support good diagnostics against additional files, including file path and
  ranges when available.
- Keep V1 broad-invalidation semantics acceptable. Provider APIs should express
  dependencies clearly even if the adapter recomputes them every run.

## Non-Goals For This Spec

- Semantic providers over checked F# symbols.
- A fixed-point loop where one generator consumes another generator's output.
- Runtime database introspection during source generation.
- A complete C parser, OpenAPI validator, SQL parser, or JSON schema engine in
  this repo.
- Generated code that requires generated files to be physically written to disk.

## Required Host/API Additions

### 1. Rich Additional File Inputs

Replace or extend `FSharpAdditionalTextInput` with:

```fsharp
type FSharpAdditionalFileInput =
    { Path: string
      Text: string
      LogicalName: string option
      Kind: string option
      Metadata: IReadOnlyDictionary<string, string>
      Options: IReadOnlyDictionary<string, string> }
```

Rules:

- `Path` is normalized to a full path where possible.
- `Text` is the exact additional-file content supplied by the compiler host.
- `LogicalName` defaults to the file name without extension.
- `Kind` is a conventional discriminator, for example `json`, `openapi`,
  `c-header`, or `sql`.
- `Metadata` comes from MSBuild item metadata or command-line metadata.
- `Options` comes from `.editorconfig` / analyzer config for this file.

The existing `AdditionalTextsProvider` can remain as a compatibility alias, but
new generators should use `AdditionalFilesProvider`.

### 2. Analyzer Config Parsing

`AnalyzerConfigOptionsProvider` must stop returning an empty map. The adapter or
host must parse configured analyzer config files and expose:

- global options
- per-path options
- `build_metadata.AdditionalFiles.*` entries projected into additional-file
  metadata

At minimum, these keys are standardized:

```text
build_metadata.AdditionalFiles.FSharpGeneratorKind
build_metadata.AdditionalFiles.FSharpGeneratorNamespace
build_metadata.AdditionalFiles.FSharpGeneratorModule
build_metadata.AdditionalFiles.FSharpGeneratorPlacement
```

Domain generators may define additional keys under their own prefixes, for
example:

```text
build_metadata.AdditionalFiles.JsonRootType
build_metadata.AdditionalFiles.OpenApiClientName
build_metadata.AdditionalFiles.NativeLibraryName
build_metadata.AdditionalFiles.SqlConnectionName
```

### 3. Provider Combinators

Add enough provider operations to express common workflows without forcing
authors into ad hoc state:

```fsharp
module FSharpIncrementalValueProvider =
    val map2 :
        ('T -> 'U -> 'V) ->
        FSharpIncrementalValueProvider<'T> ->
        FSharpIncrementalValueProvider<'U> ->
        FSharpIncrementalValueProvider<'V>

module FSharpIncrementalValuesProvider =
    val wherePathExtension :
        extension: string ->
        FSharpIncrementalValuesProvider<FSharpAdditionalFileInput> ->
        FSharpIncrementalValuesProvider<FSharpAdditionalFileInput>

    val whereKind :
        kind: string ->
        FSharpIncrementalValuesProvider<FSharpAdditionalFileInput> ->
        FSharpIncrementalValuesProvider<FSharpAdditionalFileInput>

    val collectToValue :
        FSharpIncrementalValuesProvider<'T> ->
        FSharpIncrementalValueProvider<'T list>
```

### 4. File-Scoped Diagnostics

Diagnostics must support additional-file locations:

```fsharp
type FSharpGeneratorDiagnostic =
    { Id: string
      Message: string
      Severity: FSharpDiagnosticSeverity
      Range: range option
      FilePath: string option }
```

The adapter must preserve `FilePath` if the fork diagnostic type supports it. If
the current fork diagnostic type cannot carry it, the message should be prefixed
with the additional-file path until the fork type is extended.

### 5. Generation Helpers

Add a small escaping/name helper module so all generators use the same F#
identifier rules:

```fsharp
module FSharpGeneratedNames =
    val sanitizeIdentifier : string -> string
    val sanitizeModuleName : string -> string
    val stableHintName : generatorId: string -> inputPath: string -> suffix: string -> string
```

This prevents each domain generator from inventing incompatible naming rules.

## Additional File Selection

Generators should support both extension-based and metadata-based discovery.
Metadata wins when both are present.

Recommended defaults:

| Scenario | Extension Defaults | Kind |
|---|---|---|
| JSON/config | `.json`, `.config.json` | `json` |
| OpenAPI | `.openapi.json`, `.openapi.yaml`, `.openapi.yml` | `openapi` |
| C header | `.h` | `c-header` |
| SQL | `.sql` | `sql` |

Example MSBuild shape:

```xml
<ItemGroup>
  <AdditionalFiles Include="appsettings.json"
                   FSharpGeneratorKind="json"
                   FSharpGeneratorNamespace="MyApp.Config"
                   JsonRootType="AppSettings" />

  <AdditionalFiles Include="petstore.openapi.json"
                   FSharpGeneratorKind="openapi"
                   FSharpGeneratorNamespace="MyApp.Api"
                   OpenApiClientName="PetStoreClient" />

  <AdditionalFiles Include="native/simple.h"
                   FSharpGeneratorKind="c-header"
                   FSharpGeneratorNamespace="MyApp.Native"
                   NativeLibraryName="simple" />

  <AdditionalFiles Include="sql/GetUsers.sql"
                   FSharpGeneratorKind="sql"
                   FSharpGeneratorNamespace="MyApp.Data"
                   SqlConnectionName="AppDb" />
</ItemGroup>
```

Equivalent command-line support can be added later by accepting repeated
metadata pairs beside each additional file. V1 may use analyzer config only.

## Scenario 1: Typed JSON/Config Access

### Input

An additional file such as:

```json
{
  "serviceName": "orders",
  "retryCount": 3,
  "features": {
    "audit": true
  }
}
```

### Generator Behavior

The JSON generator:

- Selects files with kind `json` or extension `.json`.
- Uses `JsonRootType` or the logical file name to choose the root type name.
- Infers a conservative F# record shape from the JSON sample unless an explicit
  schema file is configured.
- Emits a generated module before consuming user source.
- Emits diagnostics for invalid JSON, unsupported unions, mixed arrays, and
  invalid generated identifiers.

### Generated Shape

Example generated API:

```fsharp
namespace MyApp.Config

type Features =
    { Audit: bool }

type AppSettings =
    { ServiceName: string
      RetryCount: int
      Features: Features }

module AppSettings =
    val parse : json: string -> AppSettings
    val load : path: string -> AppSettings
```

The generator may embed default JSON text only when explicitly requested by
metadata. Default behavior is to generate types and parsers, not hard-code local
machine configuration into the assembly.

## Scenario 2: OpenAPI Client Generation

### Input

An OpenAPI 3.x JSON or YAML document.

### Generator Behavior

The OpenAPI generator:

- Selects files with kind `openapi` or `.openapi.*` extensions.
- Parses and validates enough OpenAPI 3.x to produce deterministic code.
- Generates DTO records, discriminated unions for string enums, request builders,
  and an HTTP client wrapper.
- Uses metadata for namespace, client name, base URL strategy, and serialization
  library.
- Emits diagnostics for unsupported schema constructs, duplicate operation IDs,
  invalid names, and ambiguous content types.

### Generated Shape

Example generated API:

```fsharp
namespace MyApp.Api

type Pet =
    { Id: int64
      Name: string }

type PetStoreClient =
    new: httpClient: System.Net.Http.HttpClient -> PetStoreClient
    member GetPetById: petId: int64 -> Async<Pet>
```

OpenAPI generation should default to `BeforeLastSourceFile` placement so
application projects keep their final-file rule intact.

## Scenario 3: Native Binding Generation From A Simple C Header

### Input

A deliberately small C header subset:

```c
int add(int left, int right);
double distance(double x, double y);
```

### Supported V1 C Subset

- Function declarations.
- Primitive numeric types: `void`, `char`, `short`, `int`, `long`, `float`,
  `double`.
- `const char*` as `string`.
- Pointers as `nativeptr<'T>` or `IntPtr`, chosen by metadata.
- Simple `struct` declarations with primitive fields.
- No macros beyond simple constants.
- No preprocessor execution.

### Generator Behavior

The C header generator:

- Selects files with kind `c-header` or extension `.h`.
- Requires `NativeLibraryName`.
- Maps supported C declarations to `LibraryImport` or `DllImport` wrappers.
- Emits diagnostics for unsupported declarations rather than silently skipping
  them.
- Generates stable F# modules under the configured namespace.

### Generated Shape

Example generated API:

```fsharp
namespace MyApp.Native

module Simple =
    [<System.Runtime.InteropServices.LibraryImport("simple", EntryPoint = "add")>]
    extern int Add(int left, int right)
```

The V1 contract is intentionally small. More complete native binding generation
belongs in the generator assembly, potentially backed by ClangSharp or another
parser, not in this host library.

## Scenario 4: Typed SQL Access From `.sql` Files

### Input

SQL files use a small metadata header:

```sql
-- name: GetUserById
-- result: one
select id, name, email
from users
where id = @id;
```

### Generator Behavior

The SQL generator:

- Selects files with kind `sql` or extension `.sql`.
- Parses command metadata from comments.
- Extracts named parameters from the query text.
- Uses a configured schema source for result typing. V1 should prefer an
  additional schema file over live database introspection.
- Emits command functions or methods with typed parameters and typed result
  records.
- Emits diagnostics for missing query names, duplicate names, unknown result
  columns, unsupported parameter syntax, and schema mismatches.

### Generated Shape

Example generated API:

```fsharp
namespace MyApp.Data

type UserRow =
    { Id: int
      Name: string
      Email: string option }

module Queries =
    val getUserById :
        connection: System.Data.Common.DbConnection ->
        id: int ->
        Async<UserRow option>
```

V1 should not connect to a live database by default. A future opt-in mode can
allow design-time database introspection, but it must participate in cache
identity and have clear timeout/failure behavior.

## Generated Source Placement

Default placement by scenario:

| Scenario | Default Placement |
|---|---|
| JSON/config | `Prelude` or `BeforeLastSourceFile` |
| OpenAPI | `BeforeLastSourceFile` |
| C header | `BeforeLastSourceFile` |
| SQL | `BeforeLastSourceFile` |

`Prelude` is acceptable for pure helper attributes or small config modules that
must be visible to all user files. Larger domain outputs should use
`BeforeLastSourceFile` to preserve application project semantics.

## Diagnostics

Reserve these generator-specific diagnostic families:

```text
FSGJSON0001..0999
FSGOPENAPI0001..0999
FSGNATIVE0001..0999
FSGSQL0001..0999
```

The host-level `FSG0001` through `FSG0015` diagnostics remain reserved for
loader, adapter, placement, and configuration failures.

## Acceptance Tests

Add one fixture generator per scenario under
`tests/FSharpNativeGenerator.TestGenerators` and end-to-end tests under
`tests/FSharpNativeGenerator.Tests`.

Minimum tests:

- JSON additional file generates a typed record and parse function visible to a
  user source file.
- Invalid JSON reports a diagnostic with the additional-file path.
- OpenAPI file generates a client type and DTO referenced by user code.
- Duplicate OpenAPI operation IDs produce a diagnostic.
- C header generates a native module with expected extern signatures.
- Unsupported C declaration produces a diagnostic and does not crash the host.
- SQL file generates typed query functions visible to user code.
- SQL schema mismatch produces a diagnostic with the `.sql` path.
- Analyzer config metadata changes generated namespace/module names.
- Stable hint names do not collide for two additional files with the same file
  name in different directories.

## Implementation Order

1. Preserve file paths in diagnostics or add a documented fallback prefix.
2. Parse analyzer config and project `AdditionalFiles` metadata into rich
   additional-file inputs.
3. Add `AdditionalFilesProvider` while keeping `AdditionalTextsProvider` for
   compatibility.
4. Add provider combinators and generated-name helpers.
5. Add scenario fixture generators that prove host capability without bringing
   in heavy production parsers.
6. Add the scenario acceptance tests.
7. Only then build production-quality JSON/OpenAPI/C/SQL generator packages.

