namespace FSharp.Compiler.SourceGeneration

open FSharp.Compiler.CodeAnalysis

[<Sealed>]
type FSharpGeneratorHost(?checker: FSharpChecker) =
    let checker = defaultArg checker (FSharpChecker.Create())

    let adapt analyzerConfigFiles generators =
        let analyzerOptions = FSharpAnalyzerConfigSupport.parseFiles analyzerConfigFiles

        generators
        |> List.map (fun loaded -> IncrementalGeneratorAdapter(loaded.Generator, loaded.GeneratorId, analyzerOptions) :> IFSharpSourceGenerator)

    let withAnalyzerConfigIdentity (generatorOptions: FSharpSourceGeneratorOptions) =
        let identity = FSharpAnalyzerConfigSupport.contentIdentityPath generatorOptions.AnalyzerConfigFiles
        { generatorOptions with AnalyzerConfigFiles = generatorOptions.AnalyzerConfigFiles @ [ identity ] }

    member _.Checker = checker

    member _.LoadFromConfiguration(config: FSharpSourceGeneratorConfiguration) : LoadedFSharpGenerator list * FSharpSourceGeneratorDiagnostic list =
        let results = config.GeneratorPaths |> List.map FSharpGeneratorAssemblyLoader.loadFromPath
        results |> List.collect _.Generators, results |> List.collect _.Diagnostics

    member _.RunGenerators
        (options: FSharpProjectOptions, generators: LoadedFSharpGenerator list, generatorOptions: FSharpSourceGeneratorOptions)
        : Async<FSharpProjectOptions * FSharpSourceGeneratorRunResult> =
        let originalAnalyzerConfigFiles = generatorOptions.AnalyzerConfigFiles
        let generatorOptions = withAnalyzerConfigIdentity generatorOptions
        checker.RunSourceGeneratorsAndUpdateProject(options, adapt originalAnalyzerConfigFiles generators, generatorOptions)

    member _.ParseAndCheck
        (options: FSharpProjectOptions, generators: LoadedFSharpGenerator list, generatorOptions: FSharpSourceGeneratorOptions)
        : Async<FSharpCheckProjectResults * FSharpSourceGeneratorRunResult> =
        let originalAnalyzerConfigFiles = generatorOptions.AnalyzerConfigFiles
        let generatorOptions = withAnalyzerConfigIdentity generatorOptions
        checker.ParseAndCheckProjectWithSourceGenerators(options, adapt originalAnalyzerConfigFiles generators, generatorOptions)

    member _.Compile
        (argv: string array, generators: LoadedFSharpGenerator list, generatorOptions: FSharpSourceGeneratorOptions)
        : Async<FSharp.Compiler.Diagnostics.FSharpDiagnostic array * FSharpSourceGeneratorRunResult * exn option> =
        let originalAnalyzerConfigFiles = generatorOptions.AnalyzerConfigFiles
        let generatorOptions = withAnalyzerConfigIdentity generatorOptions
        checker.CompileWithSourceGenerators(argv, adapt originalAnalyzerConfigFiles generators, generatorOptions)
