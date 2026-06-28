namespace FSharp.Compiler.SourceGeneration

open System.IO
open FSharp.Compiler.CodeAnalysis

[<Sealed>]
type FSharpGeneratorHost(?checker: FSharpChecker) =
    let checker = defaultArg checker (FSharpChecker.Create())

    let adapt generators =
        generators
        |> List.map (fun loaded -> IncrementalGeneratorAdapter(loaded.Generator, loaded.GeneratorId) :> IFSharpSourceGenerator)

    let withAnalyzerConfigIdentity (generatorOptions: FSharpSourceGeneratorOptions) =
        let identity = FSharpAnalyzerConfigSupport.contentIdentityPath generatorOptions.AnalyzerConfigFiles
        { generatorOptions with AnalyzerConfigFiles = generatorOptions.AnalyzerConfigFiles @ [ identity ] }

    let projectDirectory (options: FSharpProjectOptions) =
        match Path.GetDirectoryName options.ProjectFileName with
        | null
        | "" -> Directory.GetCurrentDirectory()
        | directory -> directory

    let compileProjectDirectory (argv: string array) =
        argv
        |> Array.tryFind (fun arg ->
            arg.EndsWith(".fs", System.StringComparison.OrdinalIgnoreCase)
            || arg.EndsWith(".fsi", System.StringComparison.OrdinalIgnoreCase))
        |> Option.bind (fun sourceFile ->
            match Path.GetDirectoryName(Path.GetFullPath sourceFile) with
            | null
            | "" -> None
            | directory -> Some directory)
        |> Option.defaultWith Directory.GetCurrentDirectory

    member _.Checker = checker

    member _.LoadFromConfiguration(config: FSharpSourceGeneratorConfiguration) : LoadedFSharpGenerator list * FSharpSourceGeneratorDiagnostic list =
        let results = config.GeneratorPaths |> List.map FSharpGeneratorAssemblyLoader.loadFromPath
        results |> List.collect _.Generators, results |> List.collect _.Diagnostics

    member _.RunGenerators
        (options: FSharpProjectOptions, generators: LoadedFSharpGenerator list, generatorOptions: FSharpSourceGeneratorOptions)
        : Async<FSharpProjectOptions * FSharpSourceGeneratorRunResult> =
        let projectDirectory = projectDirectory options
        let generatorOptions = withAnalyzerConfigIdentity generatorOptions

        FSharpAnalyzerConfigSupport.registerForProjectDirectory projectDirectory generatorOptions.AnalyzerConfigFiles
        checker.RunSourceGeneratorsAndUpdateProject(options, adapt generators, generatorOptions)

    member _.ParseAndCheck
        (options: FSharpProjectOptions, generators: LoadedFSharpGenerator list, generatorOptions: FSharpSourceGeneratorOptions)
        : Async<FSharpCheckProjectResults * FSharpSourceGeneratorRunResult> =
        let projectDirectory = projectDirectory options
        let generatorOptions = withAnalyzerConfigIdentity generatorOptions

        FSharpAnalyzerConfigSupport.registerForProjectDirectory projectDirectory generatorOptions.AnalyzerConfigFiles
        checker.ParseAndCheckProjectWithSourceGenerators(options, adapt generators, generatorOptions)

    member _.Compile
        (argv: string array, generators: LoadedFSharpGenerator list, generatorOptions: FSharpSourceGeneratorOptions)
        : Async<FSharp.Compiler.Diagnostics.FSharpDiagnostic array * FSharpSourceGeneratorRunResult * exn option> =
        let generatorOptions = withAnalyzerConfigIdentity generatorOptions
        FSharpAnalyzerConfigSupport.registerForProjectDirectory (compileProjectDirectory argv) generatorOptions.AnalyzerConfigFiles
        FSharpAnalyzerConfigSupport.registerForProjectDirectory (Directory.GetCurrentDirectory()) generatorOptions.AnalyzerConfigFiles
        checker.CompileWithSourceGenerators(argv, adapt generators, generatorOptions)
