module FSharpNativeGenerator.Tests

open System
open System.IO
open System.Reflection
open System.Threading
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.SourceGeneration
open FSharpNativeGenerator.TestGenerators
open Xunit

let private tempRoot () =
    Path.Combine(Path.GetTempPath(), "FSharpNativeGenerator.Tests", Guid.NewGuid().ToString("N"))

let private writeFile path (content: string) =
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath path)) |> ignore
    File.WriteAllText(path, content)

let private generatorOptions root additionalFiles =
    { OutputDirectory = Path.Combine(root, "obj", "Generated", "FSharp")
      EmitGeneratedFiles = false
      AdditionalFiles = additionalFiles
      AnalyzerConfigFiles = []
      MaxPasses = 1 }

let private context root sourceFiles additionalFiles otherOptions =
    { ProjectFileName = Some(Path.Combine(root, "App.fsproj"))
      ProjectDirectory = root
      SourceFiles = sourceFiles
      OtherOptions = otherOptions
      References = []
      DefineConstants = []
      OutputFile = None
      AssemblyName = Some "App"
      AdditionalFiles = additionalFiles
      CancellationToken = CancellationToken.None }

let private projectOptions (checker: FSharpChecker) root (sourceFiles: string list) =
    let argv =
        [| yield "--target:library"
           yield "--warn:3"
           yield "-o:" + Path.Combine(root, "bin", "App.dll")
           yield! sourceFiles |]

    checker.GetProjectOptionsFromCommandLineArgs(Path.Combine(root, "App.fsproj"), argv)

let private loaded (generator: IFSharpIncrementalGenerator) id =
    { Generator = generator
      GeneratorId = id
      AssemblyPath = Assembly.GetExecutingAssembly().Location
      TypeName = generator.GetType().FullName }

let private expectOk result =
    match result with
    | Ok value -> value
    | Error diagnostics -> failwithf "Expected Ok, got Error: %A" diagnostics

let private expectError result =
    match result with
    | Ok value -> failwithf "Expected Error, got Ok: %A" value
    | Error diagnostics -> diagnostics

[<FSharpGenerator>]
type PostInitGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            context.RegisterPostInitializationOutput(
                Action<FSharpPostInitializationContext>(fun post ->
                    post.AddImplementationSource("Prelude", "module Prelude\nlet value = 1"))
            )

[<FSharpGenerator>]
type AdditionalEchoGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            let modules = context.AdditionalTextsProvider |> FSharpIncrementalValuesProvider.map _.Text

            context.RegisterSourceOutput(
                modules,
                Action<FSharpSourceProductionContext, string>(fun productionContext moduleName ->
                    productionContext.AddImplementationSource(moduleName, "module " + moduleName, Prelude))
            )

[<FSharpGenerator>]
type DiagnosticGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            context.RegisterSourceOutput(
                context.ProjectOptionsProvider,
                Action<FSharpSourceProductionContext, FSharpGeneratorProjectSnapshot>(fun productionContext _ ->
                    productionContext.ReportDiagnostic(FSharpGeneratorDiagnostic.create "TEST0001" "reported" FSharpDiagnosticSeverity.Warning))
            )

[<FSharpGenerator>]
type GeneratedSymbolGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            context.RegisterSourceOutput(
                context.ProjectOptionsProvider,
                Action<FSharpSourceProductionContext, FSharpGeneratorProjectSnapshot>(fun productionContext _ ->
                    productionContext.AddImplementationSource("GeneratedApi", "module GeneratedApi\nlet answer = 42", Prelude))
            )

[<Fact>]
let Loader_LoadsPublicAttributedGenerator () =
    let path = typeof<CliHarnessGenerator>.Assembly.Location
    let result = FSharpGeneratorAssemblyLoader.loadFromPath path

    Assert.Contains(result.Generators, fun generator -> generator.TypeName.EndsWith("CliHarnessGenerator", StringComparison.Ordinal))
    Assert.Contains(result.Generators, fun generator -> generator.GeneratorId.Contains("/", StringComparison.Ordinal))

[<Fact>]
let Loader_RejectsMissingAttribute () =
    let path = typeof<CliHarnessGenerator>.Assembly.Location
    let result = FSharpGeneratorAssemblyLoader.loadFromPath path

    Assert.Contains(result.Diagnostics, fun diagnostic -> diagnostic.Id = "FSG0002" && diagnostic.Message.Contains("MissingAttributeGenerator", StringComparison.Ordinal))

[<Fact>]
let Loader_RejectsUnsupportedApiVersion () =
    let path = typeof<CliHarnessGenerator>.Assembly.Location
    let result = FSharpGeneratorAssemblyLoader.loadFromPath path

    Assert.Contains(result.Diagnostics, fun diagnostic -> diagnostic.Id = "FSG0015" && diagnostic.Message.Contains("UnsupportedApiGenerator", StringComparison.Ordinal))

[<Fact>]
let Loader_ReportsAssemblyLoadFailure () =
    let result = FSharpGeneratorAssemblyLoader.loadFromPath(Path.Combine(tempRoot (), "missing.dll"))

    Assert.Empty result.Generators
    Assert.Contains(result.Diagnostics, fun diagnostic -> diagnostic.Id = "FSG0001")

[<Fact>]
let Adapter_RunsPostInitializationOutput () =
    let root = tempRoot ()
    let adapter = IncrementalGeneratorAdapter(PostInitGenerator(), "Tests/PostInit") :> IFSharpSourceGenerator
    let output = adapter.Generate(context root [ Path.Combine(root, "User.fs") ] Map.empty [])

    let source = Assert.Single output.GeneratedSources
    Assert.Equal("Prelude", source.HintName)
    Assert.Equal(FSharpGeneratedSourceOrder.BeforeFile(Path.Combine(root, "User.fs")), source.Order)

[<Fact>]
let Adapter_RunsSourceOutputForAdditionalFile () =
    let root = tempRoot ()
    let adapter = IncrementalGeneratorAdapter(AdditionalEchoGenerator(), "Tests/Additional") :> IFSharpSourceGenerator
    let output = adapter.Generate(context root [ Path.Combine(root, "User.fs") ] (Map.ofList [ "schema.txt", "SchemaModule" ]) [])

    let source = Assert.Single output.GeneratedSources
    Assert.Equal("SchemaModule", source.HintName)
    Assert.Contains("module SchemaModule", source.SourceText)

[<Fact>]
let Adapter_MapsDiagnosticsToForkDiagnostics () =
    let root = tempRoot ()
    let adapter = IncrementalGeneratorAdapter(DiagnosticGenerator(), "Tests/Diagnostics") :> IFSharpSourceGenerator
    let output = adapter.Generate(context root [ Path.Combine(root, "User.fs") ] Map.empty [])

    let diagnostic = Assert.Single output.Diagnostics
    Assert.Equal("TEST0001", diagnostic.Id)
    Assert.Equal(FSharpDiagnosticSeverity.Warning, diagnostic.Severity)

[<Fact>]
let Adapter_ProducesStableGeneratedFileName () =
    let root = tempRoot ()
    let adapter = IncrementalGeneratorAdapter(PostInitGenerator(), "Assembly/Namespace.Type") :> IFSharpSourceGenerator
    let output = adapter.Generate(context root [ Path.Combine(root, "User.fs") ] Map.empty [])

    let source = Assert.Single output.GeneratedSources
    let expected = Path.Combine(root, "obj", "Generated", "FSharp", "Assembly", "Namespace.Type", "Prelude.fs") |> Path.GetFullPath
    Assert.Equal(expected, source.FileName)

[<Fact>]
let Placement_PreludeBeforeFirstOriginal () =
    let source =
        { GeneratorId = "g"
          HintName = "Prelude"
          FileName = "Prelude.fs"
          SourceText = "module Prelude"
          Kind = FSharpGeneratedSourceKind.Implementation
          Placement = Prelude
          CompanionImplementationHintName = None }

    let result = FSharpGeneratedSourcePlacementResolver.resolve [ "A.fs"; "B.fs" ] [] [ source ]
    let generated, diagnostics = expectOk result
    Assert.Empty diagnostics
    Assert.Equal(FSharpGeneratedSourceOrder.BeforeFile "A.fs", generated.Head.Order)

[<Fact>]
let Placement_BeforeLastSourceFileBeforeLastImplementation () =
    let source =
        { GeneratorId = "g"
          HintName = "Helpers"
          FileName = "Helpers.fs"
          SourceText = "module Helpers"
          Kind = FSharpGeneratedSourceKind.Implementation
          Placement = BeforeLastSourceFile
          CompanionImplementationHintName = None }

    let result = FSharpGeneratedSourcePlacementResolver.resolve [ "A.fsi"; "A.fs"; "Program.fs" ] [] [ source ]
    let generated, _ = expectOk result
    Assert.Equal(FSharpGeneratedSourceOrder.BeforeFile "Program.fs", generated.Head.Order)

[<Fact>]
let Placement_EndOfProjectRejectedForApplication () =
    let source =
        { GeneratorId = "g"
          HintName = "Late"
          FileName = "Late.fs"
          SourceText = "module Late"
          Kind = FSharpGeneratedSourceKind.Implementation
          Placement = EndOfProject
          CompanionImplementationHintName = None }

    let diagnostics = expectError (FSharpGeneratedSourcePlacementResolver.resolve [ "Program.fs" ] [ "--target"; "exe" ] [ source ])
    Assert.Contains(diagnostics, fun diagnostic -> diagnostic.Id = "FSG0012")

[<Fact>]
let Placement_DuplicateHintRejected () =
    let source hint =
        { GeneratorId = "g"
          HintName = hint
          FileName = hint + ".fs"
          SourceText = "module " + hint
          Kind = FSharpGeneratedSourceKind.Implementation
          Placement = Prelude
          CompanionImplementationHintName = None }

    let diagnostics = expectError (FSharpGeneratedSourcePlacementResolver.resolve [ "A.fs" ] [] [ source "Dup"; source "Dup" ])
    Assert.Contains(diagnostics, fun diagnostic -> diagnostic.Id = "FSG0006")

[<Fact>]
let Placement_MissingAnchorRejected () =
    let source =
        { GeneratorId = "g"
          HintName = "BeforeMissing"
          FileName = "BeforeMissing.fs"
          SourceText = "module BeforeMissing"
          Kind = FSharpGeneratedSourceKind.Implementation
          Placement = BeforeFile "Missing.fs"
          CompanionImplementationHintName = None }

    let diagnostics = expectError (FSharpGeneratedSourcePlacementResolver.resolve [ "A.fs" ] [] [ source ])
    Assert.Contains(diagnostics, fun diagnostic -> diagnostic.Id = "FSG0007")

[<Fact>]
let Placement_EmptyOriginalsDoesNotThrow () =
    let source =
        { GeneratorId = "g"
          HintName = "Prelude"
          FileName = "Prelude.fs"
          SourceText = "module Prelude"
          Kind = FSharpGeneratedSourceKind.Implementation
          Placement = Prelude
          CompanionImplementationHintName = None }

    let generated, _ = expectOk (FSharpGeneratedSourcePlacementResolver.resolve [] [] [ source ])
    Assert.Equal(FSharpGeneratedSourceOrder.EndOfProject, generated.Head.Order)

[<Fact>]
let Host_LoadFromConfigurationLoadsGenerators () =
    let host = FSharpGeneratorHost()
    let path = typeof<CliHarnessGenerator>.Assembly.Location
    let generators, diagnostics = host.LoadFromConfiguration { FSharpSourceGeneratorConfiguration.empty with GeneratorPaths = [ path ] }

    Assert.NotEmpty generators
    Assert.Contains(diagnostics, fun diagnostic -> diagnostic.Id = "FSG0015")

[<Fact>]
let Host_RunGeneratorsUpdatesProjectSourceFiles () =
    let root = tempRoot ()
    let userFile = Path.Combine(root, "User.fs")
    writeFile userFile "module User"
    let checker = FSharpChecker.Create()
    let host = FSharpGeneratorHost(checker)
    let options = projectOptions checker root [ userFile ]
    let updatedOptions, result =
        host.RunGenerators(options, [ loaded (GeneratedSymbolGenerator()) "Tests/GeneratedSymbol" ], generatorOptions root [])
        |> Async.RunSynchronously

    Assert.Empty result.Diagnostics
    Assert.True(updatedOptions.SourceFiles.Length > options.SourceFiles.Length)
    Assert.Contains(result.GeneratedSources, fun source -> source.HintName = "GeneratedApi")

[<Fact>]
let Host_ParseAndCheckSeesGeneratedSymbol () =
    let root = tempRoot ()
    let userFile = Path.Combine(root, "User.fs")
    writeFile userFile "module User\nlet value = GeneratedApi.answer"
    let checker = FSharpChecker.Create()
    let host = FSharpGeneratorHost(checker)
    let options = projectOptions checker root [ userFile ]
    let results, runResult =
        host.ParseAndCheck(options, [ loaded (GeneratedSymbolGenerator()) "Tests/GeneratedSymbol" ], generatorOptions root [])
        |> Async.RunSynchronously

    Assert.Empty runResult.Diagnostics
    Assert.Empty(results.Diagnostics |> Array.filter (fun diagnostic -> diagnostic.Severity = FSharpDiagnosticSeverity.Error))

[<Fact>]
let Host_CompileSucceedsWithGeneratedSymbol () =
    let root = tempRoot ()
    let userFile = Path.Combine(root, "User.fs")
    writeFile userFile "module User\nlet value = GeneratedApi.answer"
    let host = FSharpGeneratorHost(FSharpChecker.Create())
    let argv =
        [| "fsc.exe"
           "--targetprofile:netcore"
           "--target:library"
           "-o:" + Path.Combine(root, "App.dll")
           userFile |]

    let diagnostics, runResult, exn =
        host.Compile(argv, [ loaded (GeneratedSymbolGenerator()) "Tests/GeneratedSymbol" ], generatorOptions root [])
        |> Async.RunSynchronously

    Assert.Empty runResult.Diagnostics
    Assert.Empty(diagnostics |> Array.filter (fun diagnostic -> diagnostic.Severity = FSharpDiagnosticSeverity.Error))
    Assert.Null exn

[<Fact>]
let Host_AdditionalFileChangeInvalidatesForkRunCache () =
    let root = tempRoot ()
    let userFile = Path.Combine(root, "User.fs")
    let additional = Path.Combine(root, "schema.txt")
    writeFile userFile "module User"
    writeFile additional "FirstModule"
    let checker = FSharpChecker.Create()
    let host = FSharpGeneratorHost(checker)
    let options = projectOptions checker root [ userFile ]
    let generator = loaded (AdditionalFileGenerator()) "Tests/AdditionalFile"

    let _, first = host.RunGenerators(options, [ generator ], generatorOptions root [ additional ]) |> Async.RunSynchronously
    writeFile additional "SecondModule"
    let _, second = host.RunGenerators(options, [ generator ], generatorOptions root [ additional ]) |> Async.RunSynchronously

    Assert.False second.CacheHit
    Assert.Contains(first.GeneratedSources, fun source -> source.HintName = "FirstModule")
    Assert.Contains(second.GeneratedSources, fun source -> source.HintName = "SecondModule")
