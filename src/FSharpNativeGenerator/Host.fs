namespace FSharp.Compiler.SourceGeneration

open FSharp.Compiler.CodeAnalysis

[<Sealed>]
type FSharpGeneratorHost(?checker: FSharpChecker) =
    let checker = defaultArg checker (FSharpChecker.Create())

    let adapt generators =
        generators
        |> List.map (fun loaded -> IncrementalGeneratorAdapter(loaded.Generator, loaded.GeneratorId) :> IFSharpSourceGenerator)

    member _.Checker = checker

    member _.LoadFromConfiguration(config: FSharpSourceGeneratorConfiguration) : LoadedFSharpGenerator list * FSharpSourceGeneratorDiagnostic list =
        let results = config.GeneratorPaths |> List.map FSharpGeneratorAssemblyLoader.loadFromPath
        results |> List.collect _.Generators, results |> List.collect _.Diagnostics

    member _.RunGenerators
        (options: FSharpProjectOptions, generators: LoadedFSharpGenerator list, generatorOptions: FSharpSourceGeneratorOptions)
        : Async<FSharpProjectOptions * FSharpSourceGeneratorRunResult> =
        checker.RunSourceGeneratorsAndUpdateProject(options, adapt generators, generatorOptions)

    member _.ParseAndCheck
        (options: FSharpProjectOptions, generators: LoadedFSharpGenerator list, generatorOptions: FSharpSourceGeneratorOptions)
        : Async<FSharpCheckProjectResults * FSharpSourceGeneratorRunResult> =
        checker.ParseAndCheckProjectWithSourceGenerators(options, adapt generators, generatorOptions)

    member _.Compile
        (argv: string array, generators: LoadedFSharpGenerator list, generatorOptions: FSharpSourceGeneratorOptions)
        : Async<FSharp.Compiler.Diagnostics.FSharpDiagnostic array * FSharpSourceGeneratorRunResult * exn option> =
        checker.CompileWithSourceGenerators(argv, adapt generators, generatorOptions)
