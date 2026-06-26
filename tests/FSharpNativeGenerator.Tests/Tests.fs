module FSharpNativeGenerator.Tests

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Diagnostics
open System.IO
open System.Reflection
open System.Text.Json
open System.Threading
open FSharp.Compiler.SourceGeneration
open Xunit

let private text value =
    FSharpSourceText.OfString value

let private immutableArray values =
    ImmutableArray.CreateRange values

let private tempRoot () =
    Path.Combine(Path.GetTempPath(), "FSharpNativeGenerator.Tests", Guid.NewGuid().ToString("N"))

let private fileIn root name =
    Path.Combine(root, name) |> Path.GetFullPath

let private repoRoot () =
    let rec loop directory =
        if File.Exists(Path.Combine(directory, "FSharpNativeGenerator.slnx")) then
            directory
        else
            match Directory.GetParent(directory) with
            | null -> invalidOp "Could not locate repository root."
            | parent -> loop parent.FullName

    loop AppContext.BaseDirectory

let private snapshot outputKind sourceFiles =
    let sourceFiles = sourceFiles |> List.map Path.GetFullPath

    let projectOptions =
        {
            ProjectFilePath = fileIn (tempRoot ()) "App.fsproj"
            ProjectId = Some "test-project"
            SourceFiles = immutableArray sourceFiles
            OtherOptions = immutableArray [ "--define:TEST" ]
            OutputKind = outputKind
            Stamp = None
        }

    {
        ProjectOptions = projectOptions
        SourceFiles =
            sourceFiles
            |> List.map (fun path -> FSharpSourceFileSnapshot.create path "module UserCode")
            |> immutableArray
        AdditionalTexts = ImmutableArray<FSharpAdditionalText>.Empty
        AnalyzerConfigOptions =
            {
                GlobalOptions = Dictionary<string, string>() :> IReadOnlyDictionary<string, string>
                GetOptionsForPath = fun _ -> Dictionary<string, string>() :> IReadOnlyDictionary<string, string>
            }
    }

let private snapshotWithSourceContents outputKind sourceFiles =
    let baseSnapshot = snapshot outputKind (sourceFiles |> List.map fst)

    {
        baseSnapshot with
            SourceFiles =
                sourceFiles
                |> List.map (fun (path, content) -> FSharpSourceFileSnapshot.create path content)
                |> immutableArray
    }

let private snapshotWithAdditional outputKind sourceFiles additionalTexts =
    let baseSnapshot = snapshot outputKind sourceFiles

    {
        baseSnapshot with
            AdditionalTexts =
                additionalTexts
                |> List.map (fun (path, content) ->
                    {
                        Path = Path.GetFullPath(path)
                        GetText = fun _ -> Some(text content)
                        Checksum = Some(FSharpSourceText.checksum (text content))
                    })
                |> immutableArray
    }

let private snapshotWithAnalyzerOptions outputKind sourceFiles (globalOptions: seq<KeyValuePair<string, string>>) =
    let baseSnapshot = snapshot outputKind sourceFiles

    {
        baseSnapshot with
            AnalyzerConfigOptions =
                {
                    GlobalOptions = Dictionary<string, string>(globalOptions) :> IReadOnlyDictionary<string, string>
                    GetOptionsForPath = fun _ -> Dictionary<string, string>() :> IReadOnlyDictionary<string, string>
                }
    }

let private writeFile path (content: string) =
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))) |> ignore
    File.WriteAllText(path, content)

let private runDotnetBuild (projectPath: string) =
    let startInfo: ProcessStartInfo = ProcessStartInfo("dotnet", "build \"" + projectPath + "\" --nologo")
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    startInfo.WorkingDirectory <- Path.GetDirectoryName(projectPath)

    use proc = Process.Start(startInfo)
    let output = proc.StandardOutput.ReadToEnd()
    let error = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    proc.ExitCode, output + error

let private runProcess (fileName: string) (arguments: string) (workingDirectory: string) =
    let startInfo: ProcessStartInfo = ProcessStartInfo(fileName, arguments)
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.UseShellExecute <- false
    startInfo.WorkingDirectory <- workingDirectory

    use proc = Process.Start(startInfo)
    let output = proc.StandardOutput.ReadToEnd()
    let error = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    proc.ExitCode, output, error

let private writeFSharpProject projectPath sourceFiles =
    let compileItems =
        sourceFiles
        |> Seq.map (fun sourceFile -> sprintf "    <Compile Include=\"%s\" />" sourceFile)
        |> String.concat Environment.NewLine

    writeFile
        projectPath
        ($"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
{compileItems}
  </ItemGroup>
</Project>
""")

let private runWith options generator snapshot =
    let driver = FSharpGeneratorDriver.Create([ generator ], options)
    driver.RunGenerators(snapshot, CancellationToken.None) |> snd

let private hasDiagnostic id (result: FSharpGeneratorDriverRunResult) =
    result.Diagnostics |> Seq.exists (fun diagnostic -> diagnostic.Id = id)

let private generatedPath root (generatorType: Type) kind hint =
    let extension =
        match kind with
        | Implementation -> ".fs"
        | Signature -> ".fsi"

    Path.Combine(root, generatorType.FullName, hint + extension)
    |> Path.GetFullPath

[<FSharpGenerator>]
type ImplementationGenerator(hintName: string, placement: FSharpGeneratedSourcePlacement) =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            context.RegisterSourceOutput(
                context.ProjectOptionsProvider,
                Action<FSharpSourceProductionContext, FSharpProjectOptions>(fun productionContext _ ->
                    productionContext.AddImplementationSource(hintName, text "module Generated", placement)))

[<FSharpGenerator>]
type SignaturePairGenerator(placement: FSharpGeneratedSourcePlacement) =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            context.RegisterSourceOutput(
                context.ProjectOptionsProvider,
                Action<FSharpSourceProductionContext, FSharpProjectOptions>(fun productionContext _ ->
                    productionContext.AddSignatureSource("ClientSig", text "module Generated.Client", "Client", placement)
                    productionContext.AddImplementationSource("Client", text "module Generated.Client", placement)))

[<FSharpGenerator>]
type MissingSignatureCompanionGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            context.RegisterSourceOutput(
                context.ProjectOptionsProvider,
                Action<FSharpSourceProductionContext, FSharpProjectOptions>(fun productionContext _ ->
                    productionContext.AddSignatureSource("ClientSig", text "module Generated.Client", "MissingClient", Prelude)))

[<FSharpGenerator>]
type UserImplementationSignatureGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            context.RegisterSourceOutput(
                context.ProjectOptionsProvider,
                Action<FSharpSourceProductionContext, FSharpProjectOptions>(fun productionContext _ ->
                    productionContext.AddSignatureSource("Domain", text "module Domain\nval value: int", "Domain", Prelude)))

[<FSharpGenerator>]
type ThrowingInitializationGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize _ =
            invalidOp "boom"

[<FSharpGenerator>]
type ThrowingExecutionGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            context.RegisterSourceOutput(
                context.ProjectOptionsProvider,
                Action<FSharpSourceProductionContext, FSharpProjectOptions>(fun _ _ -> invalidOp "boom"))

type UnmarkedGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize _ =
            ()

[<FSharpGenerator>]
type CyclicAnchorGenerator(root: string) =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            context.RegisterSourceOutput(
                context.ProjectOptionsProvider,
                Action<FSharpSourceProductionContext, FSharpProjectOptions>(fun productionContext _ ->
                    let generatorType = typeof<CyclicAnchorGenerator>
                    let pathA = generatedPath root generatorType Implementation "A"
                    let pathB = generatedPath root generatorType Implementation "B"

                    productionContext.AddImplementationSource("A", text "module A", BeforeFile pathB)
                    productionContext.AddImplementationSource("B", text "module B", BeforeFile pathA)))

[<FSharpGenerator>]
type DuplicateHintGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            context.RegisterSourceOutput(
                context.ProjectOptionsProvider,
                Action<FSharpSourceProductionContext, FSharpProjectOptions>(fun productionContext _ ->
                    productionContext.AddImplementationSource("Dup", text "module A", Prelude)
                    productionContext.AddImplementationSource("Dup", text "module B", Prelude)))

[<FSharpGenerator>]
type NoOutputGeneratorA() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize _ =
            ()

[<FSharpGenerator>]
type NoOutputGeneratorB() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize _ =
            ()

[<FSharpGenerator>]
type LoadableGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize _ =
            ()

[<FSharpGenerator>]
type InvalidAttributedType() =
    class
    end

[<FSharpGenerator>]
type AdditionalTextGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            let schemas =
                context.AdditionalTextsProvider
                |> FSharpIncrementalValuesProvider.filter (fun additional -> additional.Path.EndsWith(".schema", StringComparison.OrdinalIgnoreCase))
                |> FSharpIncrementalValuesProvider.choose (fun additional ->
                    additional.GetText CancellationToken.None
                    |> Option.map (fun sourceText -> Path.GetFileNameWithoutExtension(additional.Path), sourceText))

            context.RegisterSourceOutput(
                schemas,
                Action<FSharpSourceProductionContext, string * FSharpSourceText>(fun productionContext (hintName, sourceText) ->
                    productionContext.AddImplementationSource(hintName, sourceText, Prelude)))

[<FSharpGenerator>]
type CancellationObservingGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            context.RegisterSourceOutput(
                context.ProjectOptionsProvider,
                Action<FSharpSourceProductionContext, FSharpProjectOptions>(fun productionContext _ ->
                    if productionContext.CancellationToken.CanBeCanceled then
                        productionContext.ReportDiagnostic(FSharpGeneratorDiagnostic.create "TEST0001" "Cancellation token was visible." Info)))

[<FSharpGenerator>]
type SameHintGeneratorA() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            context.RegisterSourceOutput(
                context.ProjectOptionsProvider,
                Action<FSharpSourceProductionContext, FSharpProjectOptions>(fun productionContext _ ->
                    productionContext.AddImplementationSource("SharedHint", text "module A", Prelude)))

[<FSharpGenerator>]
type SameHintGeneratorB() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            context.RegisterSourceOutput(
                context.ProjectOptionsProvider,
                Action<FSharpSourceProductionContext, FSharpProjectOptions>(fun productionContext _ ->
                    productionContext.AddImplementationSource("SharedHint", text "module B", Prelude)))

[<FSharpGenerator>]
type AnalyzerConfigGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            let configuredName =
                context.AnalyzerConfigOptionsProvider
                |> FSharpIncrementalValueProvider.map (fun options ->
                    match options.GlobalOptions.TryGetValue("build_property.GeneratedModuleName") with
                    | true, value -> value
                    | false, _ -> "DefaultGenerated")

            context.RegisterSourceOutput(
                configuredName,
                Action<FSharpSourceProductionContext, string>(fun productionContext moduleName ->
                    productionContext.AddImplementationSource(moduleName, text ("module " + moduleName), Prelude)))

[<FSharpGenerator>]
type InvalidSourceGenerator(hintName: string, source: string) =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            context.RegisterSourceOutput(
                context.ProjectOptionsProvider,
                Action<FSharpSourceProductionContext, FSharpProjectOptions>(fun productionContext _ ->
                    productionContext.AddImplementationSource(hintName, text source, Prelude)))

[<FSharpGenerator>]
type CountingGenerator(counter: ref<int>) =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            context.RegisterSourceOutput(
                context.ProjectOptionsProvider,
                Action<FSharpSourceProductionContext, FSharpProjectOptions>(fun productionContext _ ->
                    counter.Value <- counter.Value + 1
                    productionContext.AddImplementationSource("Counted", text "module Counted\nlet value = 1", Prelude)))

[<FSharpGenerator>]
type BuildHarnessGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            context.RegisterSourceOutput(
                context.ProjectOptionsProvider,
                Action<FSharpSourceProductionContext, FSharpProjectOptions>(fun productionContext _ ->
                    productionContext.AddImplementationSource("GeneratedPrelude", text "module GeneratedPrelude\nlet answer = 42", Prelude)))

[<FSharpGenerator>]
type BuildHarnessSignaturePairGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            context.RegisterSourceOutput(
                context.ProjectOptionsProvider,
                Action<FSharpSourceProductionContext, FSharpProjectOptions>(fun productionContext _ ->
                    productionContext.AddSignatureSource("GeneratedContract", text "module GeneratedContract\nval answer: int", "GeneratedContract", Prelude)
                    productionContext.AddImplementationSource("GeneratedContract", text "module GeneratedContract\nlet answer = 42", Prelude)))

[<FSharpGenerator>]
type PostInitializationAttributeGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            context.RegisterPostInitializationOutput(
                Action<FSharpPostInitializationContext>(fun postInitializationContext ->
                    postInitializationContext.AddImplementationSource(
                        "GeneratedMarkerAttribute",
                        text "namespace GeneratedSupport\n\nopen System\n\n[<AttributeUsage(AttributeTargets.All)>]\ntype GeneratedMarkerAttribute() =\n    inherit Attribute()")))

[<Fact>]
let ``prelude source is inserted before original files and stored`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let program = fileIn root "Program.fs"
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let result = snapshot Application [ domain; program ] |> runWith options (ImplementationGenerator("PreludeTypes", Prelude))

    Assert.Empty(result.Diagnostics)
    Assert.Equal(result.GeneratedSources.[0].ResolvedPath, result.UpdatedSourceFiles.[0])
    Assert.Equal(domain, result.UpdatedSourceFiles.[1])
    Assert.Equal(program, result.UpdatedSourceFiles.[2])
    Assert.True(result.GeneratedSourceStore.TryGetText(result.GeneratedSources.[0].ResolvedPath).IsSome)

[<Fact>]
let ``after file placement inserts generated source before later consumers`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let program = fileIn root "Program.fs"
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let result = snapshot Application [ domain; program ] |> runWith options (ImplementationGenerator("DomainExtensions", AfterFile domain))

    Assert.Empty(result.Diagnostics)
    Assert.Equal<IReadOnlyList<string>>(immutableArray [ domain; result.GeneratedSources.[0].ResolvedPath; program ], result.UpdatedSourceFiles)

[<Fact>]
let ``before last source preserves application final file`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let program = fileIn root "Program.fs"
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let result = snapshot Application [ domain; program ] |> runWith options (ImplementationGenerator("Helpers", BeforeLastSourceFile))

    Assert.Empty(result.Diagnostics)
    Assert.Equal(program, result.UpdatedSourceFiles.[2])

[<Fact>]
let ``end of project is rejected for applications`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let program = fileIn root "Program.fs"
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let result = snapshot Application [ domain; program ] |> runWith options (ImplementationGenerator("Late", EndOfProject))

    Assert.True(hasDiagnostic "FSG0012" result)
    Assert.Empty(result.GeneratedSources)
    Assert.Equal<string array>([| domain; program |], result.UpdatedSourceFiles |> Seq.toArray)

[<Fact>]
let ``end of project is allowed for libraries`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let result = snapshot Library [ domain ] |> runWith options (ImplementationGenerator("Late", EndOfProject))

    Assert.Empty(result.Diagnostics)
    Assert.Equal(domain, result.UpdatedSourceFiles.[0])
    Assert.Equal(result.GeneratedSources.[0].ResolvedPath, result.UpdatedSourceFiles.[1])

[<Fact>]
let ``duplicate hint names fail generation`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let result = snapshot Library [ domain ] |> runWith options (DuplicateHintGenerator())

    Assert.True(hasDiagnostic "FSG0006" result)

[<Fact>]
let ``missing anchor fails generation`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let result = snapshot Library [ domain ] |> runWith options (ImplementationGenerator("BeforeMissing", BeforeFile(fileIn root "Missing.fs")))

    Assert.True(hasDiagnostic "FSG0007" result)
    Assert.Empty(result.GeneratedSources)

[<Fact>]
let ``generated anchor cycle fails generation`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let generatedRoot = fileIn root "generated"
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = generatedRoot }
    let result = snapshot Library [ domain ] |> runWith options (CyclicAnchorGenerator(generatedRoot))

    Assert.True(hasDiagnostic "FSG0009" result)
    Assert.Empty(result.GeneratedSources)

[<Fact>]
let ``missing marker attribute fails generator initialization`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let result = snapshot Library [ domain ] |> runWith options (UnmarkedGenerator())

    Assert.True(hasDiagnostic "FSG0002" result)

[<Fact>]
let ``initialization exception is reported`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let result = snapshot Library [ domain ] |> runWith options (ThrowingInitializationGenerator())

    Assert.True(hasDiagnostic "FSG0003" result)

[<Fact>]
let ``execution exception is reported`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let result = snapshot Library [ domain ] |> runWith options (ThrowingExecutionGenerator())

    Assert.True(hasDiagnostic "FSG0004" result)

[<Fact>]
let ``generated signature companion is placed before implementation`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let result = snapshot Library [ domain ] |> runWith options (SignaturePairGenerator(Prelude))

    Assert.Empty(result.Diagnostics)
    Assert.Equal(Signature, result.GeneratedSources.[0].Kind)
    Assert.Equal(Implementation, result.GeneratedSources.[1].Kind)
    Assert.Equal(result.GeneratedSources.[0].ResolvedPath, result.UpdatedSourceFiles.[0])
    Assert.Equal(result.GeneratedSources.[1].ResolvedPath, result.UpdatedSourceFiles.[1])

[<Fact>]
let ``generated signature with missing companion fails`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let result = snapshot Library [ domain ] |> runWith options (MissingSignatureCompanionGenerator())

    Assert.True(hasDiagnostic "FSG0008" result)

[<Fact>]
let ``generated signature for user implementation fails with explicit diagnostic`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let result = snapshot Library [ domain ] |> runWith options (UserImplementationSignatureGenerator())

    Assert.True(hasDiagnostic "FSG0014" result)
    Assert.False(hasDiagnostic "FSG0008" result)
    Assert.Empty(result.GeneratedSources)

[<Fact>]
let ``implementation hint with fsi extension is rejected`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let result = snapshot Library [ domain ] |> runWith options (ImplementationGenerator("Wrong.fsi", Prelude))

    Assert.True(hasDiagnostic "FSG0013" result)

[<Fact>]
let ``empty generated source reports generated file path`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let result = snapshot Library [ domain ] |> runWith options (InvalidSourceGenerator("Empty", ""))

    let diagnostic = Assert.Single(result.Diagnostics |> Seq.filter (fun diagnostic -> diagnostic.Id = "FSG0005" && diagnostic.FilePath.IsSome))
    Assert.Contains("Empty.fs", diagnostic.FilePath.Value)
    Assert.Empty(result.GeneratedSources)

[<Fact>]
let ``generated source without module or namespace is rejected`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let result = snapshot Library [ domain ] |> runWith options (InvalidSourceGenerator("NoContainer", "let value = 1"))

    Assert.True(hasDiagnostic "FSG0005" result)
    Assert.Empty(result.GeneratedSources)

[<Fact>]
let ``project cache identity changes when generated source changes`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let project = snapshot Library [ domain ]
    let resultA = project |> runWith options (ImplementationGenerator("Generated", Prelude))
    let resultB = project |> runWith options (ImplementationGenerator("Generated2", Prelude))

    let identityA = FSharpProjectCacheIdentity.compute project resultA.GeneratedSources
    let identityB = FSharpProjectCacheIdentity.compute project resultB.GeneratedSources

    Assert.NotEqual<byte seq>(identityA, identityB)

[<Fact>]
let ``RunGeneratorsAndUpdateProjectOptions updates source files and stamp`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let project = snapshot Library [ domain ]
    let driver = FSharpGeneratorDriver.Create([ ImplementationGenerator("Generated", Prelude) ], options)

    let _, updatedOptions, result = driver.RunGeneratorsAndUpdateProjectOptions(project, CancellationToken.None)

    Assert.Empty(result.Diagnostics)
    Assert.Equal<string array>(result.UpdatedSourceFiles |> Seq.toArray, updatedOptions.SourceFiles |> Seq.toArray)
    Assert.True(updatedOptions.Stamp.IsSome)

[<Fact>]
let ``project stamp changes when generator set changes even if no sources are generated`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let project = snapshot Library [ domain ]

    let _, optionsA, _ = FSharpGeneratorDriver.Create([ NoOutputGeneratorA() ], options).RunGeneratorsAndUpdateProjectOptions(project, CancellationToken.None)
    let _, optionsB, _ = FSharpGeneratorDriver.Create([ NoOutputGeneratorB() ], options).RunGeneratorsAndUpdateProjectOptions(project, CancellationToken.None)

    Assert.NotEqual(optionsA.Stamp, optionsB.Stamp)

[<Fact>]
let ``driver reuses cached result when inputs are unchanged`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let counter = ref 0
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let project = snapshot Library [ domain ]
    let driver = FSharpGeneratorDriver.Create([ CountingGenerator(counter) ], options)

    let updatedDriver, first = driver.RunGenerators(project, CancellationToken.None)
    let _, second = updatedDriver.RunGenerators(project, CancellationToken.None)

    Assert.False(first.CacheHit)
    Assert.True(second.CacheHit)
    Assert.Equal(1, counter.Value)
    Assert.Equal<string array>(first.UpdatedSourceFiles |> Seq.toArray, second.UpdatedSourceFiles |> Seq.toArray)

[<Fact>]
let ``source content change invalidates cached result`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let counter = ref 0
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let firstProject = snapshotWithSourceContents Library [ domain, "module Domain\nlet value = 1" ]
    let secondProject = snapshotWithSourceContents Library [ domain, "module Domain\nlet value = 2" ]
    let driver = FSharpGeneratorDriver.Create([ CountingGenerator(counter) ], options)

    let updatedDriver, first = driver.RunGenerators(firstProject, CancellationToken.None)
    let _, second = updatedDriver.RunGenerators(secondProject, CancellationToken.None)

    Assert.False(first.CacheHit)
    Assert.False(second.CacheHit)
    Assert.Equal(2, counter.Value)

[<Fact>]
let ``additional file checksum change invalidates cached result`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let schema = fileIn root "schema.json"
    let counter = ref 0
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let firstProject = snapshotWithAdditional Library [ domain ] [ schema, """{"name":"A"}""" ]
    let secondProject = snapshotWithAdditional Library [ domain ] [ schema, """{"name":"B"}""" ]
    let driver = FSharpGeneratorDriver.Create([ CountingGenerator(counter) ], options)

    let updatedDriver, first = driver.RunGenerators(firstProject, CancellationToken.None)
    let _, second = updatedDriver.RunGenerators(secondProject, CancellationToken.None)

    Assert.False(first.CacheHit)
    Assert.False(second.CacheHit)
    Assert.Equal(2, counter.Value)

[<Fact>]
let ``analyzer config option change invalidates cached result`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let counter = ref 0
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let firstProject = snapshotWithAnalyzerOptions Library [ domain ] [ KeyValuePair("build_property.Mode", "A") ]
    let secondProject = snapshotWithAnalyzerOptions Library [ domain ] [ KeyValuePair("build_property.Mode", "B") ]
    let driver = FSharpGeneratorDriver.Create([ CountingGenerator(counter) ], options)

    let updatedDriver, first = driver.RunGenerators(firstProject, CancellationToken.None)
    let _, second = updatedDriver.RunGenerators(secondProject, CancellationToken.None)

    Assert.False(first.CacheHit)
    Assert.False(second.CacheHit)
    Assert.Equal(2, counter.Value)

[<Fact>]
let ``additional text provider can filter and generate source`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let schema = fileIn root "Customer.schema"
    let ignored = fileIn root "notes.txt"
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let project = snapshotWithAdditional Library [ domain ] [ schema, "module Generated.Customer"; ignored, "ignore me" ]
    let result = project |> runWith options (AdditionalTextGenerator())

    Assert.Empty(result.Diagnostics)
    let generatedSource = Assert.Single(result.GeneratedSources)
    Assert.Equal("Customer", generatedSource.HintName)
    Assert.Contains("module Generated.Customer", generatedSource.SourceText.Text)

[<Fact>]
let ``analyzer config provider can drive generated output`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let project =
        snapshotWithAnalyzerOptions
            Library
            [ domain ]
            [ KeyValuePair("build_property.GeneratedModuleName", "ConfiguredModule") ]

    let result = project |> runWith options (AnalyzerConfigGenerator())

    Assert.Empty(result.Diagnostics)
    let generatedSource = Assert.Single(result.GeneratedSources)
    Assert.Equal("ConfiguredModule", generatedSource.HintName)
    Assert.Contains("module ConfiguredModule", generatedSource.SourceText.Text)

[<Fact>]
let ``cancellation token is propagated to source production context`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    use cts = new CancellationTokenSource()
    let driver = FSharpGeneratorDriver.Create([ CancellationObservingGenerator() ], options)
    let _, result = driver.RunGenerators(snapshot Library [ domain ], cts.Token)

    Assert.True(hasDiagnostic "TEST0001" result)

[<Fact>]
let ``same hint names across different generators remain path unique`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let options = { FSharpGeneratorDriverOptions.defaults with GeneratedRoot = fileIn root "generated" }
    let driver = FSharpGeneratorDriver.Create([ SameHintGeneratorA(); SameHintGeneratorB() ], options)
    let _, result = driver.RunGenerators(snapshot Library [ domain ], CancellationToken.None)

    Assert.Empty(result.Diagnostics)
    Assert.Equal(2, result.GeneratedSources.Length)
    Assert.NotEqual<string>(result.GeneratedSources.[0].ResolvedPath, result.GeneratedSources.[1].ResolvedPath)

[<Fact>]
let ``source file order change updates project cache identity`` () =
    let root = tempRoot ()
    let first = fileIn root "First.fs"
    let second = fileIn root "Second.fs"
    let snapshotA = snapshot Library [ first; second ]
    let snapshotB = snapshot Library [ second; first ]

    let identityA = FSharpProjectCacheIdentity.compute snapshotA []
    let identityB = FSharpProjectCacheIdentity.compute snapshotB []

    Assert.NotEqual<byte seq>(identityA, identityB)

[<Fact>]
let ``fixed point generation request is rejected`` () =
    let root = tempRoot ()
    let domain = fileIn root "Domain.fs"
    let options =
        {
            FSharpGeneratorDriverOptions.defaults with
                GeneratedRoot = fileIn root "generated"
                MaxGenerationPasses = 2
        }

    let result = snapshot Library [ domain ] |> runWith options (ImplementationGenerator("Generated", Prelude))

    Assert.True(hasDiagnostic "FSG0010" result)
    Assert.Empty(result.GeneratedSources)

[<Fact>]
let ``assembly loader discovers attributed parameterless generators`` () =
    let assemblyPath = Assembly.GetExecutingAssembly().Location
    let result = FSharpGeneratorAssemblyLoader.loadFromPath assemblyPath

    Assert.Empty(result.Diagnostics |> Seq.filter (fun diagnostic -> diagnostic.Severity = Error && diagnostic.Id <> "FSG0002"))
    Assert.Contains(result.Generators, fun generator -> generator.GetType() = typeof<LoadableGenerator>)

[<Fact>]
let ``assembly loader reports attributed type without generator interface`` () =
    let assemblyPath = Assembly.GetExecutingAssembly().Location
    let result = FSharpGeneratorAssemblyLoader.loadFromPath assemblyPath

    Assert.True(result.Diagnostics |> Seq.exists (fun diagnostic -> diagnostic.Id = "FSG0002" && diagnostic.Message.Contains(nameof(InvalidAttributedType), StringComparison.Ordinal)))

[<Fact>]
let ``emit generated files writes configured output path`` () =
    let root = tempRoot ()
    let outputRoot = fileIn root "written"
    let domain = fileIn root "Domain.fs"
    let options =
        {
            FSharpGeneratorDriverOptions.defaults with
                GeneratedRoot = fileIn root "generated"
                GeneratedFilesOutputPath = Some outputRoot
                EmitGeneratedFiles = true
        }

    let result = snapshot Library [ domain ] |> runWith options (ImplementationGenerator("Generated", Prelude))

    Assert.Empty(result.Diagnostics)

    let relativePath = Path.GetRelativePath(Path.GetFullPath(options.GeneratedRoot), result.GeneratedSources.[0].ResolvedPath)
    let outputPath = Path.Combine(outputRoot, relativePath)

    Assert.True(File.Exists(outputPath), outputPath)
    Assert.Contains("module Generated", File.ReadAllText(outputPath))

[<Fact>]
let ``report path writes generated source and diagnostic summary`` () =
    let root = tempRoot ()
    let reportPath = fileIn root "reports/generator.json"
    let domain = fileIn root "Domain.fs"
    let options =
        {
            FSharpGeneratorDriverOptions.defaults with
                GeneratedRoot = fileIn root "generated"
                ReportPath = Some reportPath
        }

    let result = snapshot Library [ domain ] |> runWith options (ImplementationGenerator("Generated", Prelude))

    Assert.Empty(result.Diagnostics)
    Assert.True(File.Exists(reportPath), reportPath)

    use report = JsonDocument.Parse(File.ReadAllText(reportPath))
    let generatedSources = report.RootElement.GetProperty("GeneratedSources")
    let firstGeneratedSource = generatedSources[0]

    Assert.Equal("Generated", firstGeneratedSource.GetProperty("HintName").GetString())
    Assert.Equal(result.GeneratedSources.[0].ResolvedPath, firstGeneratedSource.GetProperty("ResolvedPath").GetString())

[<Fact>]
let ``generated ordered source list builds in a real FSharp project`` () =
    let root = tempRoot ()
    let generatedRoot = fileIn root "generated"
    let projectPath = fileIn root "Harness.fsproj"
    let consumer = fileIn root "Consumer.fs"

    writeFile consumer "module Consumer\nlet value = GeneratedPrelude.answer + 1"

    let options =
        {
            FSharpGeneratorDriverOptions.defaults with
                GeneratedRoot = generatedRoot
                EmitGeneratedFiles = true
                GeneratedFilesOutputPath = Some generatedRoot
        }

    let result = snapshot Library [ consumer ] |> runWith options (BuildHarnessGenerator())

    Assert.Empty(result.Diagnostics)
    writeFSharpProject projectPath result.UpdatedSourceFiles

    let exitCode, output = runDotnetBuild projectPath
    if exitCode <> 0 then
        failwith output

[<Fact>]
let ``generated signature and implementation pair builds in resolved order`` () =
    let root = tempRoot ()
    let generatedRoot = fileIn root "generated"
    let projectPath = fileIn root "Harness.fsproj"
    let consumer = fileIn root "Consumer.fs"

    writeFile consumer "module Consumer\nlet value: int = GeneratedContract.answer"

    let options =
        {
            FSharpGeneratorDriverOptions.defaults with
                GeneratedRoot = generatedRoot
                EmitGeneratedFiles = true
                GeneratedFilesOutputPath = Some generatedRoot
        }

    let result = snapshot Library [ consumer ] |> runWith options (BuildHarnessSignaturePairGenerator())

    Assert.Empty(result.Diagnostics)
    Assert.Equal(Signature, result.GeneratedSources.[0].Kind)
    Assert.Equal(Implementation, result.GeneratedSources.[1].Kind)
    writeFSharpProject projectPath result.UpdatedSourceFiles

    let exitCode, output = runDotnetBuild projectPath
    if exitCode <> 0 then
        failwith output

[<Fact>]
let ``post initialization generated attribute can be referenced from user source`` () =
    let root = tempRoot ()
    let generatedRoot = fileIn root "generated"
    let projectPath = fileIn root "Harness.fsproj"
    let consumer = fileIn root "Consumer.fs"

    writeFile consumer "module Consumer\n\n[<GeneratedSupport.GeneratedMarker>]\nlet value = 42"

    let options =
        {
            FSharpGeneratorDriverOptions.defaults with
                GeneratedRoot = generatedRoot
                EmitGeneratedFiles = true
                GeneratedFilesOutputPath = Some generatedRoot
        }

    let result = snapshot Library [ consumer ] |> runWith options (PostInitializationAttributeGenerator())

    Assert.Empty(result.Diagnostics)
    let generatedSource = Assert.Single(result.GeneratedSources)
    Assert.Equal("GeneratedMarkerAttribute", generatedSource.HintName)
    Assert.Equal(result.GeneratedSources.[0].ResolvedPath, result.UpdatedSourceFiles.[0])
    writeFSharpProject projectPath result.UpdatedSourceFiles

    let exitCode, output = runDotnetBuild projectPath
    if exitCode <> 0 then
        failwith output

[<Fact>]
let ``command line parser extracts generator options and preserves unrelated arguments`` () =
    let root = tempRoot ()
    let generatorPath = fileIn root "Generator.dll"
    let additionalPath = fileIn root "schema.json"
    let analyzerConfigPath = fileIn root ".globalconfig"
    let outputPath = fileIn root "generated"
    let reportPath = fileIn root "report.json"

    let result =
        FSharpSourceGeneratorConfiguration.parseCommandLineArguments
            [
                "--target:library"
                "--fsharp-source-generator:" + generatorPath
                "--fsharp-generator-additional-file:" + additionalPath
                "--fsharp-source-generator-analyzer-config:" + analyzerConfigPath
                "--emit-fsharp-generated-files+"
                "--fsharp-generated-files-output:" + outputPath
                "--fsharp-source-generator-report:" + reportPath
            ]

    Assert.Empty(result.Diagnostics)
    Assert.Equal<string array>([| "--target:library" |], result.RemainingArguments |> Seq.toArray)
    Assert.Equal(generatorPath, result.Configuration.GeneratorPaths.[0])
    Assert.Equal(additionalPath, result.Configuration.AdditionalFilePaths.[0])
    Assert.Equal(analyzerConfigPath, result.Configuration.AnalyzerConfigPaths.[0])
    Assert.True(result.Configuration.DriverOptions.EmitGeneratedFiles)
    Assert.Equal(Some outputPath, result.Configuration.DriverOptions.GeneratedFilesOutputPath)
    Assert.Equal(Some reportPath, result.Configuration.DriverOptions.ReportPath)
    Assert.Equal(CommandLine, result.Configuration.DriverOptions.HostKind)

[<Fact>]
let ``command line parser reports invalid source generator switches`` () =
    let result =
        FSharpSourceGeneratorConfiguration.parseCommandLineArguments
            [
                "--fsharp-source-generator:"
                "--emit-fsharp-generated-files:maybe"
            ]

    Assert.Equal(2, result.Diagnostics |> Seq.filter (fun diagnostic -> diagnostic.Id = "FSG0011") |> Seq.length)

[<Fact>]
let ``MSBuild configuration maps items and properties to driver options`` () =
    let root = tempRoot ()
    let generatorPath = fileIn root "Generator.dll"
    let additionalPath = fileIn root "schema.json"
    let outputPath = fileIn root "obj/Generated/FSharp"
    let reportPath = fileIn root "generator-report.json"

    let result =
        FSharpSourceGeneratorConfiguration.fromMSBuildItems
            [ { Include = generatorPath } ]
            [ { Include = additionalPath } ]
            {
                EmitFSharpGeneratedFiles = Some "true"
                FSharpGeneratedFilesOutputPath = Some outputPath
                FSharpSourceGeneratorReportPath = Some reportPath
            }

    Assert.Empty(result.Diagnostics)
    Assert.Equal(generatorPath, result.Configuration.GeneratorPaths.[0])
    Assert.Equal(additionalPath, result.Configuration.AdditionalFilePaths.[0])
    Assert.True(result.Configuration.DriverOptions.EmitGeneratedFiles)
    Assert.Equal(Some outputPath, result.Configuration.DriverOptions.GeneratedFilesOutputPath)
    Assert.Equal(Some reportPath, result.Configuration.DriverOptions.ReportPath)
    Assert.Equal(MSBuild, result.Configuration.DriverOptions.HostKind)

[<Fact>]
let ``MSBuild source generator item loads generator assembly`` () =
    let generatorAssembly =
        Path.Combine(repoRoot (), "tests/FSharpNativeGenerator.TestGenerators/bin/Debug/net10.0/FSharpNativeGenerator.TestGenerators.dll")
        |> Path.GetFullPath

    let result =
        FSharpSourceGeneratorConfiguration.fromMSBuildItems
            [ { Include = generatorAssembly } ]
            []
            {
                EmitFSharpGeneratedFiles = None
                FSharpGeneratedFilesOutputPath = None
                FSharpSourceGeneratorReportPath = None
            }

    let loadResult = FSharpSourceGeneratorConfiguration.loadGenerators result.Configuration

    Assert.Empty(result.Diagnostics)
    Assert.Empty(loadResult.Diagnostics)
    Assert.Single(loadResult.Generators) |> ignore

[<Fact>]
let ``MSBuild configuration reports invalid boolean property`` () =
    let result =
        FSharpSourceGeneratorConfiguration.fromMSBuildItems
            []
            []
            {
                EmitFSharpGeneratedFiles = Some "sometimes"
                FSharpGeneratedFilesOutputPath = None
                FSharpSourceGeneratorReportPath = None
            }

    Assert.True(result.Diagnostics |> Seq.exists (fun diagnostic -> diagnostic.Id = "FSG0011"))

[<Fact>]
let ``configuration creates additional text snapshots from files`` () =
    let root = tempRoot ()
    let additionalPath = fileIn root "schema.json"
    writeFile additionalPath """{"name":"Customer"}"""

    let result =
        FSharpSourceGeneratorConfiguration.parseCommandLineArguments
            [
                "--fsharp-generator-additional-file:" + additionalPath
            ]

    let additionalText = Assert.Single(FSharpSourceGeneratorConfiguration.additionalTexts result.Configuration)

    Assert.Equal(additionalPath, additionalText.Path)
    Assert.True(additionalText.Checksum.IsSome)
    Assert.Contains("Customer", additionalText.GetText(CancellationToken.None).Value.Text)

[<Fact>]
let ``configuration loads global and per-path analyzer config options`` () =
    let root = tempRoot ()
    let configPath = fileIn root ".globalconfig"
    let sourcePath = fileIn root "Customer.fs"

    writeFile
        configPath
        """is_global = true
build_property.GeneratedModuleName = GlobalModule

[*.fs]
build_property.GeneratedModuleName = FsModule
dotnet_diagnostic.test.severity = warning
"""

    let parsed =
        FSharpSourceGeneratorConfiguration.parseCommandLineArguments
            [
                "--fsharp-source-generator-analyzer-config:" + configPath
            ]

    let result = FSharpSourceGeneratorConfiguration.analyzerConfigOptions parsed.Configuration
    let sourceOptions = result.Options.GetOptionsForPath sourcePath

    Assert.Empty(result.Diagnostics)
    Assert.Equal("true", result.Options.GlobalOptions["is_global"])
    Assert.Equal("GlobalModule", result.Options.GlobalOptions["build_property.GeneratedModuleName"])
    Assert.Equal("FsModule", sourceOptions["build_property.GeneratedModuleName"])
    Assert.Equal("warning", sourceOptions["dotnet_diagnostic.test.severity"])

[<Fact>]
let ``configuration reports missing analyzer config files`` () =
    let root = tempRoot ()
    let missingConfigPath = fileIn root "missing.globalconfig"
    let parsed =
        FSharpSourceGeneratorConfiguration.parseCommandLineArguments
            [
                "--fsharp-source-generator-analyzer-config:" + missingConfigPath
            ]

    let result = FSharpSourceGeneratorConfiguration.analyzerConfigOptions parsed.Configuration

    Assert.True(result.Diagnostics |> Seq.exists (fun diagnostic -> diagnostic.Id = "FSG0011" && diagnostic.FilePath = Some missingConfigPath))

[<Fact>]
let ``configuration loads generators from configured assembly paths`` () =
    let assemblyPath = Assembly.GetExecutingAssembly().Location
    let result =
        FSharpSourceGeneratorConfiguration.parseCommandLineArguments
            [
                "--fsharp-source-generator:" + assemblyPath
            ]

    let loadResult = FSharpSourceGeneratorConfiguration.loadGenerators result.Configuration

    Assert.Contains(loadResult.Generators, fun generator -> generator.GetType() = typeof<LoadableGenerator>)

[<Fact>]
let ``NuGet analyzer folder generator loads from analyzers dotnet fs`` () =
    let root = tempRoot ()
    let packageRoot = fileIn root "package"
    let analyzerFolder = Path.Combine(packageRoot, "analyzers", "dotnet", "fs")
    let sourceGeneratorAssembly =
        Path.Combine(repoRoot (), "tests/FSharpNativeGenerator.TestGenerators/bin/Debug/net10.0/FSharpNativeGenerator.TestGenerators.dll")
        |> Path.GetFullPath
    let packagedGeneratorAssembly = Path.Combine(analyzerFolder, "FSharpNativeGenerator.TestGenerators.dll")

    Directory.CreateDirectory(analyzerFolder) |> ignore
    File.Copy(sourceGeneratorAssembly, packagedGeneratorAssembly)

    let generatorPaths = FSharpSourceGeneratorConfiguration.generatorPathsFromNuGetPackage packageRoot
    let loadResult = FSharpSourceGeneratorConfiguration.loadGeneratorsFromNuGetPackage packageRoot

    Assert.Equal<string array>([| packagedGeneratorAssembly |], generatorPaths |> Seq.toArray)
    Assert.Empty(loadResult.Diagnostics)
    Assert.Single(loadResult.Generators) |> ignore

[<Fact>]
let ``NuGet analyzer folder loading reports missing fs analyzer folder`` () =
    let root = tempRoot ()
    let packageRoot = fileIn root "package"
    Directory.CreateDirectory(packageRoot) |> ignore

    let loadResult = FSharpSourceGeneratorConfiguration.loadGeneratorsFromNuGetPackage packageRoot

    Assert.True(loadResult.Diagnostics |> Seq.exists (fun diagnostic -> diagnostic.Id = "FSG0001" && diagnostic.FilePath = Some packageRoot))

[<Fact>]
let ``CLI runs generators without MSBuild and prints updated source list`` () =
    let root = tempRoot ()
    let consumer = fileIn root "Consumer.fs"
    let generatedOutput = fileIn root "generated"
    let reportPath = fileIn root "report.json"
    let repositoryRoot = repoRoot ()
    let cliProject = Path.Combine(repositoryRoot, "src/FSharpNativeGenerator.Cli/FSharpNativeGenerator.Cli.fsproj") |> Path.GetFullPath
    let generatorAssembly =
        Path.Combine(repositoryRoot, "tests/FSharpNativeGenerator.TestGenerators/bin/Debug/net10.0/FSharpNativeGenerator.TestGenerators.dll")
        |> Path.GetFullPath

    writeFile consumer "module Consumer\nlet value = GeneratedPrelude.answer"

    let arguments =
        String.concat
            " "
            [
                "run"
                "--project"
                "\"" + cliProject + "\""
                "--"
                "--fsharp-source-generator:" + "\"" + generatorAssembly + "\""
                "--emit-fsharp-generated-files+"
                "--fsharp-generated-files-output:" + "\"" + generatedOutput + "\""
                "--fsharp-source-generator-report:" + "\"" + reportPath + "\""
                "\"" + consumer + "\""
            ]

    let exitCode, output, error = runProcess "dotnet" arguments root

    if exitCode <> 0 then
        failwith (output + error)

    Assert.Contains(consumer, output)
    Assert.True(File.Exists(reportPath), reportPath)

    use report = JsonDocument.Parse(File.ReadAllText(reportPath))
    let generatedSources = report.RootElement.GetProperty("GeneratedSources")
    let generatedPrelude =
        generatedSources.EnumerateArray()
        |> Seq.find (fun item -> item.GetProperty("HintName").GetString() = "GeneratedPrelude")

    let generatedPath = generatedPrelude.GetProperty("ResolvedPath").GetString()

    Assert.Contains(generatedPath, output)
    Assert.True(File.Exists(generatedPath), generatedPath)

[<Fact>]
let ``CLI passes analyzer config options to loaded generators`` () =
    let root = tempRoot ()
    let consumer = fileIn root "Consumer.fs"
    let configPath = fileIn root ".globalconfig"
    let generatedOutput = fileIn root "generated"
    let reportPath = fileIn root "report.json"
    let repositoryRoot = repoRoot ()
    let cliProject = Path.Combine(repositoryRoot, "src/FSharpNativeGenerator.Cli/FSharpNativeGenerator.Cli.fsproj") |> Path.GetFullPath
    let generatorAssembly =
        Path.Combine(repositoryRoot, "tests/FSharpNativeGenerator.TestGenerators/bin/Debug/net10.0/FSharpNativeGenerator.TestGenerators.dll")
        |> Path.GetFullPath

    writeFile consumer "module Consumer\nlet value = ConfiguredPrelude.answer"
    writeFile configPath "build_property.GeneratedModuleName = ConfiguredPrelude"

    let arguments =
        String.concat
            " "
            [
                "run"
                "--project"
                "\"" + cliProject + "\""
                "--"
                "--fsharp-source-generator:" + "\"" + generatorAssembly + "\""
                "--fsharp-source-generator-analyzer-config:" + "\"" + configPath + "\""
                "--emit-fsharp-generated-files+"
                "--fsharp-generated-files-output:" + "\"" + generatedOutput + "\""
                "--fsharp-source-generator-report:" + "\"" + reportPath + "\""
                "\"" + consumer + "\""
            ]

    let exitCode, output, error = runProcess "dotnet" arguments root

    if exitCode <> 0 then
        failwith (output + error)

    use report = JsonDocument.Parse(File.ReadAllText(reportPath))
    let generatedSources = report.RootElement.GetProperty("GeneratedSources")
    let configuredPrelude =
        generatedSources.EnumerateArray()
        |> Seq.find (fun item -> item.GetProperty("HintName").GetString() = "ConfiguredPrelude")

    let generatedPath = configuredPrelude.GetProperty("ResolvedPath").GetString()

    Assert.Contains(generatedPath, output)
    Assert.True(File.Exists(generatedPath), generatedPath)
