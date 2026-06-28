module FSharpNativeGenerator.Tests

open System
open System.IO
open System.Reflection
open System.Threading
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.SourceGeneration
open FSharpNativeGenerator.ScenarioGenerators
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

let private generatorOptionsWithAnalyzerConfigs root additionalFiles analyzerConfigFiles =
    { OutputDirectory = Path.Combine(root, "obj", "Generated", "FSharp")
      EmitGeneratedFiles = false
      AdditionalFiles = additionalFiles
      AnalyzerConfigFiles = analyzerConfigFiles
      MaxPasses = 1 }

let private scenarioAnalyzerConfig sections =
    "root = true\n\n" + String.concat "\n\n" sections

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

let private projectOptionsWithReferences (checker: FSharpChecker) root (sourceFiles: string list) (references: string list) =
    let argv =
        [| yield "--target:library"
           yield "--warn:3"
           yield "-o:" + Path.Combine(root, "bin", "App.dll")
           for reference in references do
               yield "-r:" + reference
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

[<FSharpGenerator>]
type InitializeCountingGenerator(counter: int ref) =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            counter.Value <- counter.Value + 1
            context.RegisterSourceOutput(
                context.ProjectOptionsProvider,
                Action<FSharpSourceProductionContext, FSharpGeneratorProjectSnapshot>(fun productionContext _ ->
                    productionContext.AddImplementationSource("Counted", "module Counted", Prelude))
            )

[<Fact>]
let Adapter_InitializesOncePerAdapterInstance () =
    let root = tempRoot ()
    let counter = ref 0
    let adapter = IncrementalGeneratorAdapter(InitializeCountingGenerator(counter), "Tests/InitializeOnce") :> IFSharpSourceGenerator
    let ctx = context root [ Path.Combine(root, "User.fs") ] Map.empty []

    adapter.Generate ctx |> ignore
    adapter.Generate ctx |> ignore

    Assert.Equal(1, counter.Value)

[<Fact>]
let Placement_KindExtensionMismatchRejected () =
    let source =
        { GeneratorId = "g"
          HintName = "BadSignature"
          FileName = "BadSignature.fs"
          SourceText = "module BadSignature"
          Kind = FSharpGeneratedSourceKind.Signature
          Placement = Prelude
          CompanionImplementationHintName = None }

    let diagnostics = expectError (FSharpGeneratedSourcePlacementResolver.resolve [ "A.fs" ] [] [ source ])
    Assert.Contains(diagnostics, fun diagnostic -> diagnostic.Id = "FSG0013")

[<Fact>]
let Placement_UserImplementationSignatureRejected () =
    let source =
        { GeneratorId = "g"
          HintName = "DomainSig"
          FileName = "DomainSig.fsi"
          SourceText = "module Domain"
          Kind = FSharpGeneratedSourceKind.Signature
          Placement = Prelude
          CompanionImplementationHintName = Some "Domain" }

    let diagnostics = expectError (FSharpGeneratedSourcePlacementResolver.resolve [ "Domain.fs" ] [] [ source ])
    Assert.Contains(diagnostics, fun diagnostic -> diagnostic.Id = "FSG0014")

[<Fact>]
let Configuration_ParseCommandLineLikeArgumentsSplitsKnownSwitchesAndRemainingArgs () =
    let config, remaining, diagnostics =
        FSharpSourceGeneratorConfiguration.parseCommandLineLikeArguments
            [ "--define:TRACE"
              "--fsharp-source-generator:/tmp/generator.dll"
              "--fsharp-generator-additional-file:/tmp/schema.json"
              "--fsharp-source-generator-analyzer-config:/tmp/.editorconfig" ]

    Assert.Empty diagnostics
    Assert.Equal<string list>([ "/tmp/generator.dll" ], config.GeneratorPaths)
    Assert.Equal<string list>([ "/tmp/schema.json" ], config.AdditionalFilePaths)
    Assert.Equal<string list>([ "/tmp/.editorconfig" ], config.AnalyzerConfigPaths)
    Assert.Equal<string list>([ "--define:TRACE" ], remaining)

[<FSharpGenerator>]
type ThrowingInitializationGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize _ = invalidOp "initialize boom"

[<FSharpGenerator>]
type ThrowingPostInitializationGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            context.RegisterPostInitializationOutput(
                Action<FSharpPostInitializationContext>(fun _ -> invalidOp "post-init boom")
            )

[<FSharpGenerator>]
type ThrowingSourceOutputGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            context.RegisterSourceOutput(
                context.ProjectOptionsProvider,
                Action<FSharpSourceProductionContext, FSharpGeneratorProjectSnapshot>(fun _ _ -> invalidOp "source-output boom")
            )

[<FSharpGenerator>]
type AnalyzerConfigObservingGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            context.RegisterSourceOutput(
                context.AnalyzerConfigOptionsProvider,
                Action<FSharpSourceProductionContext, FSharpAnalyzerConfigOptions>(fun productionContext options ->
                    let hasValue = options.GlobalOptions.ContainsKey "build_property.GeneratedModuleName"
                    let source = if hasValue then "module AnalyzerConfigWasSet" else "module AnalyzerConfigWasEmpty"
                    productionContext.AddImplementationSource("AnalyzerConfigObservation", source, Prelude))
            )

[<FSharpGenerator>]
type KindCollectingGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            let jsonFiles =
                context.AdditionalFilesProvider
                |> FSharpIncrementalValuesProvider.whereKind "json"
                |> FSharpIncrementalValuesProvider.collectToValue

            let combined =
                FSharpIncrementalValueProvider.map2
                    (fun (snapshot: FSharpGeneratorProjectSnapshot) files -> snapshot.AssemblyName, files)
                    context.ProjectOptionsProvider
                    jsonFiles

            context.RegisterSourceOutput(
                combined,
                Action<FSharpSourceProductionContext, string option * FSharpAdditionalFileInput list>(fun productionContext (_, files) ->
                    productionContext.AddImplementationSource("KindCollection", sprintf "module KindCollection\nlet count = %d" files.Length, Prelude))
            )

[<FSharpGenerator>]
type CompositeExtensionCollectingGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            let openApiFiles =
                context.AdditionalFilesProvider
                |> FSharpIncrementalValuesProvider.wherePathExtension ".openapi.json"
                |> FSharpIncrementalValuesProvider.collectToValue

            context.RegisterSourceOutput(
                openApiFiles,
                Action<FSharpSourceProductionContext, FSharpAdditionalFileInput list>(fun productionContext files ->
                    productionContext.AddImplementationSource("CompositeExtensionCollection", sprintf "module CompositeExtensionCollection\nlet count = %d" files.Length, Prelude))
            )

[<Fact>]
let Loader_ConstructorFailureDoesNotHideOtherValidGenerators () =
    let path = typeof<CliHarnessGenerator>.Assembly.Location
    let result = FSharpGeneratorAssemblyLoader.loadFromPath path

    Assert.Contains(result.Generators, fun generator -> generator.TypeName.EndsWith("CliHarnessGenerator", StringComparison.Ordinal))
    Assert.Contains(result.Diagnostics, fun diagnostic -> diagnostic.Id = "FSG0002" && diagnostic.Message.Contains("ConstructorThrowsGenerator", StringComparison.Ordinal))

[<Fact>]
let Loader_RejectsMissingPublicParameterlessConstructor () =
    let path = typeof<CliHarnessGenerator>.Assembly.Location
    let result = FSharpGeneratorAssemblyLoader.loadFromPath path

    Assert.Contains(result.Diagnostics, fun diagnostic -> diagnostic.Id = "FSG0002" && diagnostic.Message.Contains("NoPublicParameterlessConstructorGenerator", StringComparison.Ordinal))

[<Fact>]
let Adapter_InitializesOnceUnderConcurrentGenerateCalls () =
    let root = tempRoot ()
    let counter = ref 0
    let adapter = IncrementalGeneratorAdapter(InitializeCountingGenerator(counter), "Tests/ConcurrentInitialize") :> IFSharpSourceGenerator
    let ctx = context root [ Path.Combine(root, "User.fs") ] Map.empty []

    [ 1..16 ]
    |> List.map (fun _ -> async { return adapter.Generate ctx })
    |> Async.Parallel
    |> Async.RunSynchronously
    |> ignore

    Assert.Equal(1, counter.Value)

[<Fact>]
let Adapter_InitializationExceptionBecomesDiagnostic () =
    let root = tempRoot ()
    let adapter = IncrementalGeneratorAdapter(ThrowingInitializationGenerator(), "Tests/ThrowInit") :> IFSharpSourceGenerator
    let output = adapter.Generate(context root [ Path.Combine(root, "User.fs") ] Map.empty [])

    Assert.Empty output.GeneratedSources
    Assert.Contains(output.Diagnostics, fun diagnostic -> diagnostic.Id = "FSG0003" && diagnostic.Message.Contains("initialize boom", StringComparison.Ordinal))

[<Fact>]
let Adapter_PostInitializationExceptionBecomesDiagnostic () =
    let root = tempRoot ()
    let adapter = IncrementalGeneratorAdapter(ThrowingPostInitializationGenerator(), "Tests/ThrowPostInit") :> IFSharpSourceGenerator
    let output = adapter.Generate(context root [ Path.Combine(root, "User.fs") ] Map.empty [])

    Assert.Empty output.GeneratedSources
    Assert.Contains(output.Diagnostics, fun diagnostic -> diagnostic.Id = "FSG0004" && diagnostic.Message.Contains("post-init boom", StringComparison.Ordinal))

[<Fact>]
let Adapter_SourceOutputExceptionBecomesDiagnostic () =
    let root = tempRoot ()
    let adapter = IncrementalGeneratorAdapter(ThrowingSourceOutputGenerator(), "Tests/ThrowSource") :> IFSharpSourceGenerator
    let output = adapter.Generate(context root [ Path.Combine(root, "User.fs") ] Map.empty [])

    Assert.Empty output.GeneratedSources
    Assert.Contains(output.Diagnostics, fun diagnostic -> diagnostic.Id = "FSG0005" && diagnostic.Message.Contains("source-output boom", StringComparison.Ordinal))

[<Fact>]
let Adapter_AnalyzerConfigProviderIsDeterministicallyEmptyUntilForkContextCarriesAnalyzerConfigs () =
    let root = tempRoot ()
    let adapter = IncrementalGeneratorAdapter(AnalyzerConfigObservingGenerator(), "Tests/AnalyzerConfig") :> IFSharpSourceGenerator
    let output = adapter.Generate(context root [ Path.Combine(root, "User.fs") ] Map.empty [])

    let source = Assert.Single output.GeneratedSources
    Assert.Equal("AnalyzerConfigObservation", source.HintName)
    Assert.Contains("module AnalyzerConfigWasEmpty", source.SourceText)

[<Fact>]
let Host_AnalyzerConfigProviderReadsGlobalOptions () =
    let root = tempRoot ()
    let userFile = Path.Combine(root, "User.fs")
    let analyzer = Path.Combine(root, ".globalconfig")
    writeFile userFile "module User"
    writeFile analyzer "is_global = true\nbuild_property.GeneratedModuleName = AnalyzerConfigWasSet\n"
    let checker = FSharpChecker.Create()
    let host = FSharpGeneratorHost(checker)
    let options = projectOptions checker root [ userFile ]
    let result =
        host.RunGenerators(options, [ loaded (AnalyzerConfigObservingGenerator()) "Tests/AnalyzerConfigHost" ], generatorOptionsWithAnalyzerConfigs root [] [ analyzer ])
        |> Async.RunSynchronously
        |> snd

    let source = Assert.Single result.GeneratedSources
    Assert.Contains("module AnalyzerConfigWasSet", source.SourceText)

[<Fact>]
let Host_AdditionalFilesProviderSupportsKindFilterCollectToValueAndMap2 () =
    let root = tempRoot ()
    let userFile = Path.Combine(root, "User.fs")
    let jsonFile = Path.Combine(root, "settings.json")
    let sqlFile = Path.Combine(root, "query.sql")
    let analyzer = Path.Combine(root, ".editorconfig")
    writeFile userFile "module User"
    writeFile jsonFile "{}"
    writeFile sqlFile "select 1"
    writeFile analyzer (
        scenarioAnalyzerConfig
            [ "[*.json]\nbuild_metadata.AdditionalFiles.FSharpGeneratorKind = json"
              "[*.sql]\nbuild_metadata.AdditionalFiles.FSharpGeneratorKind = sql" ])
    let checker = FSharpChecker.Create()
    let host = FSharpGeneratorHost(checker)
    let options = projectOptions checker root [ userFile ]
    let result =
        host.RunGenerators(options, [ loaded (KindCollectingGenerator()) "Tests/KindCollect" ], generatorOptionsWithAnalyzerConfigs root [ jsonFile; sqlFile ] [ analyzer ])
        |> Async.RunSynchronously
        |> snd

    let source = Assert.Single result.GeneratedSources
    Assert.Contains("let count = 1", source.SourceText)

[<Fact>]
let Host_AdditionalFilesProviderSupportsCompositeExtensionFilter () =
    let root = tempRoot ()
    let userFile = Path.Combine(root, "User.fs")
    let openApiFile = Path.Combine(root, "petstore.openapi.json")
    let jsonFile = Path.Combine(root, "settings.json")
    writeFile userFile "module User"
    writeFile openApiFile "{}"
    writeFile jsonFile "{}"
    let checker = FSharpChecker.Create()
    let host = FSharpGeneratorHost(checker)
    let options = projectOptions checker root [ userFile ]
    let result =
        host.RunGenerators(options, [ loaded (CompositeExtensionCollectingGenerator()) "Tests/CompositeExtension" ], generatorOptions root [ openApiFile; jsonFile ])
        |> Async.RunSynchronously
        |> snd

    let source = Assert.Single result.GeneratedSources
    Assert.Contains("let count = 1", source.SourceText)

[<Fact>]
let AdditionalFileInput_MetadataHelpersExposeCommonValues () =
    let metadata =
        System.Collections.Generic.Dictionary<string, string>
            (dict
                [ "FSharpGeneratorNamespace", "Sample.Namespace"
                  "FSharpGeneratorModule", "SampleModule"
                  "Required", "value" ])
    let options = System.Collections.Generic.Dictionary<string, string>(dict [ "custom_option", "configured" ])
    let file =
        { Path = Path.Combine(tempRoot (), "settings.json")
          Text = "{}"
          LogicalName = None
          Kind = Some "json"
          Metadata = metadata :> System.Collections.Generic.IReadOnlyDictionary<string, string>
          Options = options :> System.Collections.Generic.IReadOnlyDictionary<string, string> }

    Assert.Equal(Some "value", FSharpAdditionalFileInput.tryGetMetadata "Required" file)
    Assert.Equal(Ok "value", FSharpAdditionalFileInput.requireMetadata "Required" file)
    Assert.True((FSharpAdditionalFileInput.requireMetadata "Missing" file).IsError)
    Assert.Equal(Some "configured", FSharpAdditionalFileInput.tryGetOption "custom_option" file)
    Assert.Equal("settings", FSharpAdditionalFileInput.logicalNameOrFileName file)
    Assert.Equal("Sample.Namespace", FSharpAdditionalFileInput.namespaceOrDefault "Default.Namespace" file)
    Assert.Equal("SampleModule", FSharpAdditionalFileInput.moduleOrDefault "DefaultModule" file)

[<Fact>]
let Placement_RelativeAnchorMatchesAbsoluteOriginalPath () =
    let root = tempRoot ()
    let original = Path.Combine(root, "A.fs")
    let relativeAnchor = Path.GetRelativePath(Directory.GetCurrentDirectory(), original)
    let source =
        { GeneratorId = "g"
          HintName = "BeforeA"
          FileName = Path.Combine(root, "BeforeA.fs")
          SourceText = "module BeforeA"
          Kind = FSharpGeneratedSourceKind.Implementation
          Placement = BeforeFile relativeAnchor
          CompanionImplementationHintName = None }

    let generated, diagnostics = expectOk (FSharpGeneratedSourcePlacementResolver.resolve [ original ] [] [ source ])
    Assert.Empty diagnostics
    Assert.Equal(FSharpGeneratedSourceOrder.BeforeFile(Path.GetFullPath relativeAnchor), generated.Head.Order)

let private runScenarioGenerator root userSource additionalPath additionalText analyzerText generator =
    let userFile = Path.Combine(root, "User.fs")
    let analyzer = Path.Combine(root, ".editorconfig")
    writeFile userFile userSource
    writeFile additionalPath additionalText
    writeFile analyzer analyzerText
    let checker = FSharpChecker.Create()
    let host = FSharpGeneratorHost(checker)
    let options = projectOptions checker root [ userFile ]

    host.RunGenerators(options, [ loaded generator "Tests/Scenario" ], generatorOptionsWithAnalyzerConfigs root [ additionalPath ] [ analyzer ])
    |> Async.RunSynchronously
    |> snd

let private compileScenario root userSource additionalPath additionalText analyzerText generator references =
    let userFile = Path.Combine(root, "User.fs")
    let analyzer = Path.Combine(root, ".editorconfig")
    writeFile userFile userSource
    writeFile additionalPath additionalText
    writeFile analyzer analyzerText
    let host = FSharpGeneratorHost(FSharpChecker.Create())
    let argv =
        [| yield "fsc.exe"
           yield "--targetprofile:netcore"
           yield "--target:library"
           yield "-o:" + Path.Combine(root, "App.dll")
           for reference in references do
               yield "-r:" + reference
           yield userFile |]

    host.Compile(argv, [ loaded generator "Tests/ScenarioCompile" ], generatorOptionsWithAnalyzerConfigs root [ additionalPath ] [ analyzer ])
    |> Async.RunSynchronously

[<Fact>]
let Scenario_JsonAdditionalFileGeneratesTypedRecordVisibleToUserCode () =
    let root = tempRoot ()
    let additional = Path.Combine(root, "appsettings.json")
    let userFile = Path.Combine(root, "User.fs")
    let analyzer = Path.Combine(root, ".editorconfig")
    writeFile userFile """module User
let parsed = MyApp.Config.AppSettingsLoader.parse "{ \"serviceName\": \"orders\", \"retryCount\": 3, \"features\": { \"audit\": true } }"
let serviceName: string = parsed.ServiceName
let retryCount: int = parsed.RetryCount
let audit: bool = parsed.Features.Audit"""
    writeFile additional """{ "serviceName": "orders", "retryCount": 3, "features": { "audit": true } }"""
    writeFile analyzer (
        scenarioAnalyzerConfig
            [ "[*.json]\nbuild_metadata.AdditionalFiles.FSharpGeneratorKind = json\nbuild_metadata.AdditionalFiles.FSharpGeneratorNamespace = MyApp.Config\nbuild_metadata.AdditionalFiles.JsonRootType = AppSettings" ])

    let checker = FSharpChecker.Create()
    let host = FSharpGeneratorHost(checker)
    let options = projectOptionsWithReferences checker root [ userFile ] [ typeof<System.Text.Json.JsonDocument>.Assembly.Location ]
    let results, runResult =
        host.ParseAndCheck(options, [ loaded (JsonConfigGenerator()) "Tests/JsonScenario" ], generatorOptionsWithAnalyzerConfigs root [ additional ] [ analyzer ])
        |> Async.RunSynchronously

    Assert.Empty runResult.Diagnostics
    Assert.Contains(runResult.GeneratedSources, fun source -> source.SourceText.Contains("type AppSettings", StringComparison.Ordinal))
    Assert.Empty(results.Diagnostics |> Array.filter (fun diagnostic -> diagnostic.Severity = FSharpDiagnosticSeverity.Error))

[<Fact>]
let Scenario_InvalidJsonReportsDiagnosticWithAdditionalFilePath () =
    let root = tempRoot ()
    let additional = Path.Combine(root, "broken.json")
    let result =
        runScenarioGenerator
            root
            "module User"
            additional
            "{ invalid"
            (scenarioAnalyzerConfig [ "[*.json]\nbuild_metadata.AdditionalFiles.FSharpGeneratorKind = json" ])
            (JsonConfigGenerator())

    Assert.Contains(result.Diagnostics, fun diagnostic -> diagnostic.Id = "FSGJSON0001" && diagnostic.Message.Contains(additional, StringComparison.Ordinal))

[<Fact>]
let Scenario_OpenApiGeneratesClientAndDtoVisibleToUserCode () =
    let root = tempRoot ()
    let additional = Path.Combine(root, "petstore.openapi.json")
    let userFile = Path.Combine(root, "User.fs")
    let analyzer = Path.Combine(root, ".editorconfig")
    writeFile userFile """module User
let pet: MyApp.Api.Pet = { Id = 1L; Name = "Milo" }
let client = MyApp.Api.PetStoreClient(fun _ -> async { return "{ \"name\": \"Milo\" }" })
let loadedPet: Async<MyApp.Api.Pet> = client.GetPetById 1L"""
    writeFile additional """{ "openapi": "3.0.0", "components": { "schemas": { "Pet": { "type": "object", "properties": { "id": { "type": "integer", "format": "int64" }, "name": { "type": "string" } } } } }, "paths": { "/pets/{id}": { "get": { "operationId": "getPetById", "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Pet" } } } } } } } } }"""
    writeFile analyzer (
        scenarioAnalyzerConfig
            [ "[*.openapi.json]\nbuild_metadata.AdditionalFiles.FSharpGeneratorKind = openapi\nbuild_metadata.AdditionalFiles.FSharpGeneratorNamespace = MyApp.Api\nbuild_metadata.AdditionalFiles.OpenApiClientName = PetStoreClient" ])

    let checker = FSharpChecker.Create()
    let host = FSharpGeneratorHost(checker)
    let options = projectOptionsWithReferences checker root [ userFile ] [ typeof<System.Text.Json.JsonDocument>.Assembly.Location ]
    let results, runResult =
        host.ParseAndCheck(options, [ loaded (OpenApiClientGenerator()) "Tests/OpenApiScenario" ], generatorOptionsWithAnalyzerConfigs root [ additional ] [ analyzer ])
        |> Async.RunSynchronously

    Assert.Empty runResult.Diagnostics
    Assert.Contains(runResult.GeneratedSources, fun source -> source.SourceText.Contains("type PetStoreClient", StringComparison.Ordinal))
    Assert.Contains(runResult.GeneratedSources, fun source -> source.SourceText.Contains("JsonSerializer.Deserialize", StringComparison.Ordinal))
    Assert.DoesNotContain(runResult.GeneratedSources, fun source -> source.SourceText.Contains("Unchecked.defaultof", StringComparison.Ordinal))
    Assert.Empty(results.Diagnostics |> Array.filter (fun diagnostic -> diagnostic.Severity = FSharpDiagnosticSeverity.Error))

[<Fact>]
let Scenario_OpenApiDuplicateOperationIdsReportDiagnostic () =
    let root = tempRoot ()
    let additional = Path.Combine(root, "duplicate.openapi.json")
    let result =
        runScenarioGenerator
            root
            "module User"
            additional
            """{ "openapi": "3.0.0", "paths": { "/a": { "get": { "operationId": "same" } }, "/b": { "post": { "operationId": "same" } } } }"""
            (scenarioAnalyzerConfig [ "[*.openapi.json]\nbuild_metadata.AdditionalFiles.FSharpGeneratorKind = openapi" ])
            (OpenApiClientGenerator())

    Assert.Contains(result.Diagnostics, fun diagnostic -> diagnostic.Id = "FSGOPENAPI0002" && diagnostic.Message.Contains(additional, StringComparison.Ordinal))

[<Fact>]
let Scenario_OpenApiUnsupportedResponseShapeReportsDiagnostic () =
    let root = tempRoot ()
    let additional = Path.Combine(root, "unsupported.openapi.json")
    let result =
        runScenarioGenerator
            root
            "module User"
            additional
            """{ "openapi": "3.0.0", "paths": { "/pets/{id}": { "get": { "operationId": "getPetById", "responses": { "204": { "description": "No content" } } } } } }"""
            (scenarioAnalyzerConfig [ "[*.openapi.json]\nbuild_metadata.AdditionalFiles.FSharpGeneratorKind = openapi" ])
            (OpenApiClientGenerator())

    Assert.Contains(result.Diagnostics, fun diagnostic -> diagnostic.Id = "FSGOPENAPI0004" && diagnostic.Message.Contains("response schema reference", StringComparison.Ordinal))
    Assert.Empty result.GeneratedSources

[<Fact>]
let Scenario_CHeaderGeneratesNativeExterns () =
    let root = tempRoot ()
    let additional = Path.Combine(root, "simple.h")
    let userFile = Path.Combine(root, "User.fs")
    let analyzer = Path.Combine(root, ".editorconfig")
    writeFile userFile "module User\nlet addPtr = MyApp.Native.Simple.Add"
    writeFile additional "int add(int left, int right);\ndouble distance(double x, double y);"
    writeFile analyzer (scenarioAnalyzerConfig [ "[*.h]\nbuild_metadata.AdditionalFiles.FSharpGeneratorKind = c-header\nbuild_metadata.AdditionalFiles.FSharpGeneratorNamespace = MyApp.Native\nbuild_metadata.AdditionalFiles.NativeLibraryName = simple\nbuild_metadata.AdditionalFiles.NativeModuleName = Simple" ])
    let checker = FSharpChecker.Create()
    let host = FSharpGeneratorHost(checker)
    let options =
        projectOptionsWithReferences
            checker
            root
            [ userFile ]
            [ typeof<System.Runtime.InteropServices.DllImportAttribute>.Assembly.Location ]
    let results, result =
        host.ParseAndCheck(options, [ loaded (NativeHeaderBindingGenerator()) "Tests/NativeScenario" ], generatorOptionsWithAnalyzerConfigs root [ additional ] [ analyzer ])
        |> Async.RunSynchronously

    Assert.Empty result.Diagnostics
    let source = Assert.Single result.GeneratedSources
    Assert.Contains("module Simple", source.SourceText)
    Assert.Contains("extern int Add(int left, int right)", source.SourceText)
    Assert.Contains("DllImport(\"simple\"", source.SourceText)
    Assert.Empty(results.Diagnostics |> Array.filter (fun diagnostic -> diagnostic.Severity = FSharpDiagnosticSeverity.Error))

[<Fact>]
let Scenario_CHeaderUnsupportedDeclarationReportsDiagnostic () =
    let root = tempRoot ()
    let additional = Path.Combine(root, "unsupported.h")
    let result =
        runScenarioGenerator
            root
            "module User"
            additional
            "#define VALUE 1\nint add(int left, int right);"
            (scenarioAnalyzerConfig [ "[*.h]\nbuild_metadata.AdditionalFiles.FSharpGeneratorKind = c-header\nbuild_metadata.AdditionalFiles.NativeLibraryName = simple" ])
            (NativeHeaderBindingGenerator())

    Assert.Contains(result.Diagnostics, fun diagnostic -> diagnostic.Id = "FSGNATIVE0001" && diagnostic.Message.Contains(additional, StringComparison.Ordinal))

[<Fact>]
let Scenario_SqlFileGeneratesTypedQueryVisibleToUserCode () =
    let root = tempRoot ()
    let additional = Path.Combine(root, "GetUserById.sql")
    let userFile = Path.Combine(root, "User.fs")
    let analyzer = Path.Combine(root, ".editorconfig")
    writeFile userFile """module User
let row: MyApp.Data.GetUserByIdRow = { Id = 1; Name = "Ada"; Email = None }
let executor (_: string) (_: (string * obj) list) = async { return Some(Map.ofList [ "id", box 1; "name", box "Ada"; "email", box (Some "a@example.com") ]) }
let queryResult: Async<MyApp.Data.GetUserByIdRow option> = MyApp.Data.Queries.getUserById executor 1"""
    writeFile additional "-- name: GetUserById
-- result: one
select id, name, email
from users
where id = @id;"
    writeFile analyzer (
        scenarioAnalyzerConfig
            [ "[*.sql]
build_metadata.AdditionalFiles.FSharpGeneratorKind = sql
build_metadata.AdditionalFiles.FSharpGeneratorNamespace = MyApp.Data
build_metadata.AdditionalFiles.SqlResultColumns = id:int,name:string,email:string option
build_metadata.AdditionalFiles.SqlParameterTypes = id:int" ])

    let checker = FSharpChecker.Create()
    let host = FSharpGeneratorHost(checker)
    let options = projectOptions checker root [ userFile ]
    let results, runResult =
        host.ParseAndCheck(options, [ loaded (SqlQueryGenerator()) "Tests/SqlScenario" ], generatorOptionsWithAnalyzerConfigs root [ additional ] [ analyzer ])
        |> Async.RunSynchronously

    Assert.Empty runResult.Diagnostics
    Assert.Contains(runResult.GeneratedSources, fun source -> source.SourceText.Contains("type GetUserByIdRow", StringComparison.Ordinal))
    Assert.Contains(runResult.GeneratedSources, fun source -> source.SourceText.Contains("execute sqlText parameters", StringComparison.Ordinal))
    Assert.DoesNotContain(runResult.GeneratedSources, fun source -> source.SourceText.Contains("return None", StringComparison.Ordinal))
    Assert.Empty(results.Diagnostics |> Array.filter (fun diagnostic -> diagnostic.Severity = FSharpDiagnosticSeverity.Error))

[<Fact>]
let Scenario_SqlSchemaMismatchReportsDiagnostic () =
    let root = tempRoot ()
    let additional = Path.Combine(root, "GetUserById.sql")
    let result =
        runScenarioGenerator
            root
            "module User"
            additional
            "-- name: GetUserById\nselect id, email\nfrom users\nwhere id = @id;"
            (scenarioAnalyzerConfig [ "[*.sql]\nbuild_metadata.AdditionalFiles.FSharpGeneratorKind = sql\nbuild_metadata.AdditionalFiles.SqlResultColumns = id:int" ])
            (SqlQueryGenerator())

    Assert.Contains(result.Diagnostics, fun diagnostic -> diagnostic.Id = "FSGSQL0002" && diagnostic.Message.Contains(additional, StringComparison.Ordinal))

[<Fact>]
let Scenario_AnalyzerConfigMetadataChangesGeneratedNamespace () =
    let root = tempRoot ()
    let additional = Path.Combine(root, "settings.json")
    let result =
        runScenarioGenerator
            root
            "module User"
            additional
            """{ "name": "demo" }"""
            (scenarioAnalyzerConfig [ "[*.json]\nbuild_metadata.AdditionalFiles.FSharpGeneratorKind = json\nbuild_metadata.AdditionalFiles.FSharpGeneratorNamespace = Custom.Config\nbuild_metadata.AdditionalFiles.JsonRootType = Settings" ])
            (JsonConfigGenerator())

    let source = Assert.Single result.GeneratedSources
    Assert.Contains("namespace Custom.Config", source.SourceText)
    Assert.Contains("type Settings", source.SourceText)

[<Fact>]
let Scenario_JsonPropertyNamesAreEscapedInGeneratedSource () =
    let root = tempRoot ()
    let additional = Path.Combine(root, "settings.json")
    let result =
        runScenarioGenerator
            root
            "module User"
            additional
            """{ "quoted\"name": "demo" }"""
            (scenarioAnalyzerConfig [ "[*.json]\nbuild_metadata.AdditionalFiles.FSharpGeneratorKind = json\nbuild_metadata.AdditionalFiles.JsonRootType = Settings" ])
            (JsonConfigGenerator())

    Assert.Empty result.Diagnostics
    let source = Assert.Single result.GeneratedSources
    Assert.Contains("quoted\\\"name", source.SourceText)

[<Fact>]
let Scenario_StableHintNamesDoNotCollideForSameFileNameInDifferentDirectories () =
    let root = tempRoot ()
    let first = Path.Combine(root, "one", "settings.json")
    let second = Path.Combine(root, "two", "settings.json")
    let userFile = Path.Combine(root, "User.fs")
    let analyzer = Path.Combine(root, ".editorconfig")
    writeFile userFile "module User"
    writeFile first """{ "first": true }"""
    writeFile second """{ "second": true }"""
    writeFile analyzer (scenarioAnalyzerConfig [ "[*.json]\nbuild_metadata.AdditionalFiles.FSharpGeneratorKind = json\nbuild_metadata.AdditionalFiles.JsonRootType = Settings" ])

    let checker = FSharpChecker.Create()
    let host = FSharpGeneratorHost(checker)
    let options = projectOptions checker root [ userFile ]
    let result =
        host.RunGenerators(options, [ loaded (JsonConfigGenerator()) "Tests/JsonStableHints" ], generatorOptionsWithAnalyzerConfigs root [ first; second ] [ analyzer ])
        |> Async.RunSynchronously
        |> snd

    Assert.Empty result.Diagnostics
    Assert.Equal(2, result.GeneratedSources.Length)
    Assert.Equal(2, result.GeneratedSources |> List.map _.HintName |> Set.ofList |> Set.count)

[<Fact>]
let Scenario_AnalyzerConfigContentChangeInvalidatesForkRunCache () =
    let root = tempRoot ()
    let userFile = Path.Combine(root, "User.fs")
    let additional = Path.Combine(root, "settings.json")
    let analyzer = Path.Combine(root, ".editorconfig")
    writeFile userFile "module User"
    writeFile additional """{ "name": "demo" }"""
    writeFile analyzer (scenarioAnalyzerConfig [ "[*.json]\nbuild_metadata.AdditionalFiles.FSharpGeneratorKind = json\nbuild_metadata.AdditionalFiles.FSharpGeneratorNamespace = First.Config\nbuild_metadata.AdditionalFiles.JsonRootType = Settings" ])
    let checker = FSharpChecker.Create()
    let host = FSharpGeneratorHost(checker)
    let options = projectOptions checker root [ userFile ]
    let generator = loaded (JsonConfigGenerator()) "Tests/JsonConfigCache"

    let _, first =
        host.RunGenerators(options, [ generator ], generatorOptionsWithAnalyzerConfigs root [ additional ] [ analyzer ])
        |> Async.RunSynchronously

    writeFile analyzer (scenarioAnalyzerConfig [ "[*.json]\nbuild_metadata.AdditionalFiles.FSharpGeneratorKind = json\nbuild_metadata.AdditionalFiles.FSharpGeneratorNamespace = Second.Config\nbuild_metadata.AdditionalFiles.JsonRootType = Settings" ])

    let _, second =
        host.RunGenerators(options, [ generator ], generatorOptionsWithAnalyzerConfigs root [ additional ] [ analyzer ])
        |> Async.RunSynchronously

    Assert.False second.CacheHit
    Assert.Contains(first.GeneratedSources, fun source -> source.SourceText.Contains("namespace First.Config", StringComparison.Ordinal))
    Assert.Contains(second.GeneratedSources, fun source -> source.SourceText.Contains("namespace Second.Config", StringComparison.Ordinal))


[<Fact>]
let Scenario_OpenApiYamlReportsExplicitUnsupportedDiagnostic () =
    let root = tempRoot ()
    let additional = Path.Combine(root, "petstore.openapi.yaml")
    let result =
        runScenarioGenerator
            root
            "module User"
            additional
            "openapi: 3.0.0"
            (scenarioAnalyzerConfig [ "[*.openapi.yaml]\nbuild_metadata.AdditionalFiles.FSharpGeneratorKind = openapi" ])
            (OpenApiClientGenerator())

    Assert.Contains(result.Diagnostics, fun diagnostic -> diagnostic.Id = "FSGOPENAPI0003" && diagnostic.Message.Contains(additional, StringComparison.Ordinal))

[<Fact>]
let Scenario_CHeaderConstCharPointerArgumentIsGeneratedAsString () =
    let root = tempRoot ()
    let additional = Path.Combine(root, "strings.h")
    let result =
        runScenarioGenerator
            root
            "module User"
            additional
            "int puts_name(const char* name);"
            (scenarioAnalyzerConfig [ "[*.h]\nbuild_metadata.AdditionalFiles.FSharpGeneratorKind = c-header\nbuild_metadata.AdditionalFiles.NativeLibraryName = simple" ])
            (NativeHeaderBindingGenerator())

    Assert.Empty result.Diagnostics
    let source = Assert.Single result.GeneratedSources
    Assert.Contains("extern int PutsName(string name)", source.SourceText)

[<Fact>]
let Scenario_CHeaderLibraryNameIsEscapedInGeneratedSource () =
    let root = tempRoot ()
    let additional = Path.Combine(root, "strings.h")
    let result =
        runScenarioGenerator
            root
            "module User"
            additional
            "int puts_name(const char* name);"
            (scenarioAnalyzerConfig [ "[*.h]\nbuild_metadata.AdditionalFiles.FSharpGeneratorKind = c-header\nbuild_metadata.AdditionalFiles.NativeLibraryName = quoted\"native" ])
            (NativeHeaderBindingGenerator())

    Assert.Empty result.Diagnostics
    let source = Assert.Single result.GeneratedSources
    Assert.Contains("quoted\\\"native", source.SourceText)

[<Fact>]
let Scenario_CHeaderUnsupportedArgumentReportsDiagnosticInsteadOfDroppingArgument () =
    let root = tempRoot ()
    let additional = Path.Combine(root, "unsupported-arg.h")
    let result =
        runScenarioGenerator
            root
            "module User"
            additional
            "int bad(unsigned value);"
            (scenarioAnalyzerConfig [ "[*.h]\nbuild_metadata.AdditionalFiles.FSharpGeneratorKind = c-header\nbuild_metadata.AdditionalFiles.NativeLibraryName = simple" ])
            (NativeHeaderBindingGenerator())

    Assert.Contains(result.Diagnostics, fun diagnostic -> diagnostic.Id = "FSGNATIVE0001" && diagnostic.Message.Contains("unsigned", StringComparison.Ordinal))
    Assert.Empty result.GeneratedSources

[<Fact>]
let Scenario_SqlMissingParameterTypeReportsDiagnostic () =
    let root = tempRoot ()
    let additional = Path.Combine(root, "GetUserById.sql")
    let result =
        runScenarioGenerator
            root
            "module User"
            additional
            "-- name: GetUserById\nselect id\nfrom users\nwhere id = @id;"
            (scenarioAnalyzerConfig [ "[*.sql]\nbuild_metadata.AdditionalFiles.FSharpGeneratorKind = sql\nbuild_metadata.AdditionalFiles.SqlResultColumns = id:int" ])
            (SqlQueryGenerator())

    Assert.Contains(result.Diagnostics, fun diagnostic -> diagnostic.Id = "FSGSQL0003" && diagnostic.Message.Contains("@id", StringComparison.Ordinal))

[<Fact>]
let Scenario_SqlUnsupportedMetadataTypeReportsDiagnostic () =
    let root = tempRoot ()
    let additional = Path.Combine(root, "GetUserById.sql")
    let result =
        runScenarioGenerator
            root
            "module User"
            additional
            "-- name: GetUserById\nselect id\nfrom users\nwhere id = @id;"
            (scenarioAnalyzerConfig [ "[*.sql]\nbuild_metadata.AdditionalFiles.FSharpGeneratorKind = sql\nbuild_metadata.AdditionalFiles.SqlResultColumns = id:System.Guid\nbuild_metadata.AdditionalFiles.SqlParameterTypes = id:int" ])
            (SqlQueryGenerator())

    Assert.Contains(result.Diagnostics, fun diagnostic -> diagnostic.Id = "FSGSQL0004" && diagnostic.Message.Contains("System.Guid", StringComparison.Ordinal))

[<Fact>]
let Scenario_CompilePathWorksForJsonOpenApiNativeAndSqlExamples () =
    let jsonRoot = tempRoot ()
    let jsonDiagnostics, jsonRunResult, jsonException =
        compileScenario
            jsonRoot
            """module User
let parsed = MyApp.Config.AppSettingsLoader.parse "{ \"serviceName\": \"orders\", \"retryCount\": 3, \"features\": { \"audit\": true } }"
let audit: bool = parsed.Features.Audit"""
            (Path.Combine(jsonRoot, "appsettings.json"))
            """{ "serviceName": "orders", "retryCount": 3, "features": { "audit": true } }"""
            (scenarioAnalyzerConfig [ "[*.json]\nbuild_metadata.AdditionalFiles.FSharpGeneratorKind = json\nbuild_metadata.AdditionalFiles.FSharpGeneratorNamespace = MyApp.Config\nbuild_metadata.AdditionalFiles.JsonRootType = AppSettings" ])
            (JsonConfigGenerator())
            [ typeof<System.Text.Json.JsonDocument>.Assembly.Location ]

    Assert.Empty jsonRunResult.Diagnostics
    Assert.Empty(jsonDiagnostics |> Array.filter (fun diagnostic -> diagnostic.Severity = FSharpDiagnosticSeverity.Error))
    Assert.Null jsonException

    let openApiRoot = tempRoot ()
    let openApiDiagnostics, openApiRunResult, openApiException =
        compileScenario
            openApiRoot
            """module User
let client = MyApp.Api.PetStoreClient(fun _ -> async { return "{ \"name\": \"Milo\" }" })
let loadedPet: Async<MyApp.Api.Pet> = client.GetPetById 1L"""
            (Path.Combine(openApiRoot, "petstore.openapi.json"))
            """{ "openapi": "3.0.0", "components": { "schemas": { "Pet": { "type": "object", "properties": { "id": { "type": "integer", "format": "int64" }, "name": { "type": "string" } } } } }, "paths": { "/pets/{id}": { "get": { "operationId": "getPetById", "responses": { "200": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Pet" } } } } } } } } }"""
            (scenarioAnalyzerConfig [ "[*.openapi.json]\nbuild_metadata.AdditionalFiles.FSharpGeneratorKind = openapi\nbuild_metadata.AdditionalFiles.FSharpGeneratorNamespace = MyApp.Api\nbuild_metadata.AdditionalFiles.OpenApiClientName = PetStoreClient" ])
            (OpenApiClientGenerator())
            [ typeof<System.Text.Json.JsonDocument>.Assembly.Location ]

    Assert.Empty openApiRunResult.Diagnostics
    Assert.Empty(openApiDiagnostics |> Array.filter (fun diagnostic -> diagnostic.Severity = FSharpDiagnosticSeverity.Error))
    Assert.Null openApiException

    let nativeRoot = tempRoot ()
    let nativeDiagnostics, nativeRunResult, nativeException =
        compileScenario
            nativeRoot
            "module User\nlet addPtr = MyApp.Native.Simple.Add"
            (Path.Combine(nativeRoot, "simple.h"))
            "int add(int left, int right);"
            (scenarioAnalyzerConfig [ "[*.h]\nbuild_metadata.AdditionalFiles.FSharpGeneratorKind = c-header\nbuild_metadata.AdditionalFiles.FSharpGeneratorNamespace = MyApp.Native\nbuild_metadata.AdditionalFiles.NativeLibraryName = simple\nbuild_metadata.AdditionalFiles.NativeModuleName = Simple" ])
            (NativeHeaderBindingGenerator())
            [ typeof<System.Runtime.InteropServices.DllImportAttribute>.Assembly.Location ]

    Assert.Empty nativeRunResult.Diagnostics
    Assert.Empty(nativeDiagnostics |> Array.filter (fun diagnostic -> diagnostic.Severity = FSharpDiagnosticSeverity.Error))
    Assert.Null nativeException

    let sqlRoot = tempRoot ()
    let sqlDiagnostics, sqlRunResult, sqlException =
        compileScenario
            sqlRoot
            """module User
let executor (_: string) (_: (string * obj) list) = async { return Some(Map.ofList [ "id", box 1; "name", box "Ada" ]) }
let queryResult: Async<MyApp.Data.GetUserByIdRow option> = MyApp.Data.Queries.getUserById executor 1"""
            (Path.Combine(sqlRoot, "GetUserById.sql"))
            "-- name: GetUserById
select id, name
from users
where id = @id;"
            (scenarioAnalyzerConfig [ "[*.sql]
build_metadata.AdditionalFiles.FSharpGeneratorKind = sql
build_metadata.AdditionalFiles.FSharpGeneratorNamespace = MyApp.Data
build_metadata.AdditionalFiles.SqlResultColumns = id:int,name:string
build_metadata.AdditionalFiles.SqlParameterTypes = id:int" ])
            (SqlQueryGenerator())
            []

    Assert.Empty sqlRunResult.Diagnostics
    Assert.Empty(sqlDiagnostics |> Array.filter (fun diagnostic -> diagnostic.Severity = FSharpDiagnosticSeverity.Error))
    Assert.Null sqlException

[<FSharpGenerator>]
type FilePathDiagnosticGenerator(path: string) =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            context.RegisterSourceOutput(
                context.ProjectOptionsProvider,
                Action<FSharpSourceProductionContext, FSharpGeneratorProjectSnapshot>(fun productionContext _ ->
                    productionContext.ReportDiagnostic(
                        { FSharpGeneratorDiagnostic.create "TESTFILEPATH" "diagnostic with file path" FSharpDiagnosticSeverity.Warning with
                            FilePath = Some path }))
            )

[<Fact>]
let Adapter_FilePathDiagnosticFallbackPrefixesMessage () =
    let root = tempRoot ()
    let diagnosticPath = Path.Combine(root, "settings.json")
    let adapter = IncrementalGeneratorAdapter(FilePathDiagnosticGenerator(diagnosticPath), "Tests/FilePathDiagnostic") :> IFSharpSourceGenerator
    let output = adapter.Generate(context root [ Path.Combine(root, "User.fs") ] Map.empty [])

    let diagnostic = Assert.Single output.Diagnostics
    Assert.Equal("TESTFILEPATH", diagnostic.Id)
    Assert.StartsWith(diagnosticPath + ":", diagnostic.Message, StringComparison.Ordinal)
