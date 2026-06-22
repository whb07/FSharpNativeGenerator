namespace FSharp.Compiler.SourceGeneration

open System
open System.Collections.Immutable
open System.Diagnostics
open System.IO
open System.Text
open System.Threading

type FSharpGeneratorDriver private (generators: ImmutableArray<IFSharpIncrementalGenerator>, options: FSharpGeneratorDriverOptions, registeredGenerators: ImmutableArray<RegisteredGenerator> option) =
    static member Create(generators: seq<IFSharpIncrementalGenerator>, options: FSharpGeneratorDriverOptions) =
        FSharpGeneratorDriver(ImmutableArray.CreateRange generators, options, None)

    member _.Options = options

    member private this.InitializeGenerators() =
        match registeredGenerators with
        | Some value -> this, value
        | None ->
            let registered =
                generators
                |> Seq.map (fun generator ->
                    let generatorType = generator.GetType()
                    let generatorName = generatorType.FullName
                    let postInitOutputs = ResizeArray<Action<FSharpPostInitializationContext>>()
                    let sourceOutputs = ResizeArray<RegisteredSourceOutput>()
                    let diagnostics = ResizeArray<FSharpGeneratorDiagnostic>()

                    let hasMarker =
                        generatorType.GetCustomAttributes(typeof<FSharpGeneratorAttribute>, false).Length > 0

                    if not hasMarker then
                        diagnostics.Add(Diagnostics.error "FSG0002" (sprintf "Generator type '%s' is missing FSharpGeneratorAttribute." generatorName))
                    else
                        let initContext =
                            FSharpIncrementalGeneratorInitializationContext(
                                { Evaluate = fun snapshot -> snapshot.ProjectOptions },
                                { EvaluateMany = fun snapshot -> snapshot.SourceFiles :> seq<_> },
                                { EvaluateMany = fun snapshot -> snapshot.AdditionalTexts :> seq<_> },
                                { Evaluate = fun snapshot -> snapshot.AnalyzerConfigOptions },
                                postInitOutputs,
                                sourceOutputs)

                        try
                            generator.Initialize initContext
                        with ex ->
                            diagnostics.Add(Diagnostics.error "FSG0003" (sprintf "Generator '%s' threw during initialization: %s" generatorName ex.Message))

                    {
                        Generator = generator
                        GeneratorName = generatorName
                        PostInitializationOutputs = ImmutableArray.CreateRange postInitOutputs
                        SourceOutputs = ImmutableArray.CreateRange sourceOutputs
                        InitializationDiagnostics = ImmutableArray.CreateRange diagnostics
                    })
                |> ImmutableArray.CreateRange

            FSharpGeneratorDriver(generators, options, Some registered), registered

    member private _.MaterializePending(pending: PendingGeneratedSource list) =
        let diagnostics = ResizeArray<FSharpGeneratorDiagnostic>()

        let duplicateHints =
            pending
            |> Seq.countBy (fun source -> source.GeneratorName, source.HintName)
            |> Seq.filter (fun (_, count) -> count > 1)

        for ((generatorName, hintName), _) in duplicateHints do
            diagnostics.Add(Diagnostics.error "FSG0006" (sprintf "Generator '%s' emitted duplicate hint name '%s'." generatorName hintName))

        pending
        |> List.choose (fun source ->
            if String.IsNullOrWhiteSpace source.HintName then
                diagnostics.Add(Diagnostics.error "FSG0011" (sprintf "Generator '%s' emitted an empty hint name." source.GeneratorName))
                None
            elif GeneratedPaths.conflictsWithKind source.Kind source.HintName then
                diagnostics.Add(Diagnostics.error "FSG0013" (sprintf "Generated source '%s' kind does not match its file extension." source.HintName))
                None
            else
                let resolvedPath =
                    GeneratedPaths.resolvedPath options.GeneratedRoot source.GeneratorName source.Kind source.HintName

                Some
                    {
                        GeneratorName = source.GeneratorName
                        HintName = source.HintName
                        ResolvedPath = resolvedPath
                        Kind = source.Kind
                        SourceText = source.SourceText
                        Placement = source.Placement
                        Checksum = FSharpSourceText.checksum source.SourceText
                    }),
        diagnostics |> Seq.toList

    member this.RunGenerators(projectSnapshot: FSharpGeneratorProjectSnapshot, cancellationToken: CancellationToken) =
        let stopwatch = Stopwatch.StartNew()
        let updatedDriver, initializedGenerators = this.InitializeGenerators()
        let diagnostics = ResizeArray<FSharpGeneratorDiagnostic>()
        let pending = ResizeArray<PendingGeneratedSource>()

        if options.MaxGenerationPasses <> 1 then
            diagnostics.Add(Diagnostics.error "FSG0010" "Fixed-point generation is not supported in V1. MaxGenerationPasses must be 1.")

        if not (diagnostics |> Seq.exists (fun diagnostic -> diagnostic.Severity = Error)) then
            for generator in initializedGenerators do
                diagnostics.AddRange generator.InitializationDiagnostics

                if generator.InitializationDiagnostics |> Seq.exists (fun diagnostic -> diagnostic.Severity = Error) then
                    ()
                else
                    let postInitContext = FSharpPostInitializationContext(generator.GeneratorName, pending, diagnostics, cancellationToken)

                    for output in generator.PostInitializationOutputs do
                        try
                            output.Invoke postInitContext
                        with ex ->
                            diagnostics.Add(Diagnostics.error "FSG0004" (sprintf "Generator '%s' threw during post-initialization output: %s" generator.GeneratorName ex.Message))

                    let productionContext = FSharpSourceProductionContext(generator.GeneratorName, pending, diagnostics, cancellationToken)

                    for output in generator.SourceOutputs do
                        try
                            output projectSnapshot productionContext
                        with ex ->
                            diagnostics.Add(Diagnostics.error "FSG0004" (sprintf "Generator '%s' threw during source output: %s" generator.GeneratorName ex.Message))

        let materialized, materializeDiagnostics = updatedDriver.MaterializePending(pending |> Seq.toList)
        diagnostics.AddRange materializeDiagnostics

        for diagnostic in materialized |> Seq.collect GeneratedSourceValidation.validate do
            diagnostics.Add diagnostic

        let units, unitDiagnostics = Placement.buildUnits materialized (pending |> Seq.toList)
        diagnostics.AddRange unitDiagnostics

        let generatedPaths = materialized |> List.map _.ResolvedPath

        let updatedSourceFiles, placementDiagnostics =
            Placement.resolve projectSnapshot.ProjectOptions.OutputKind (projectSnapshot.ProjectOptions.SourceFiles |> Seq.toList) generatedPaths units

        diagnostics.AddRange placementDiagnostics

        let generatedSources =
            if diagnostics |> Seq.exists (fun diagnostic -> diagnostic.Severity = Error) then
                ImmutableArray<FSharpGeneratedSource>.Empty
            else
                ImmutableArray.CreateRange materialized

        let generatedStore =
            generatedSources
            |> Seq.fold (fun (store: FSharpGeneratedSourceStore) (source: FSharpGeneratedSource) -> store.Add source) FSharpGeneratedSourceStore.Empty

        if options.EmitGeneratedFiles && not (diagnostics |> Seq.exists (fun diagnostic -> diagnostic.Severity = Error)) then
            let outputRoot = defaultArg options.GeneratedFilesOutputPath options.GeneratedRoot

            for source in generatedSources do
                let relativePath = Path.GetRelativePath(Path.GetFullPath(options.GeneratedRoot), source.ResolvedPath)
                let outputPath = Path.Combine(outputRoot, relativePath)
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)) |> ignore
                File.WriteAllText(outputPath, source.SourceText.Text, Encoding.UTF8)

        stopwatch.Stop()

        let result =
            {
                GeneratedSources = generatedSources
                Diagnostics = ImmutableArray.CreateRange diagnostics
                UpdatedSourceFiles =
                    if diagnostics |> Seq.exists (fun diagnostic -> diagnostic.Severity = Error) then
                        projectSnapshot.ProjectOptions.SourceFiles
                    else
                        updatedSourceFiles
                GeneratedSourceStore = generatedStore
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
            }

        options.ReportPath |> Option.iter (fun path -> RunReport.write path result)

        updatedDriver, result

    member this.RunGeneratorsAndUpdateProjectOptions(projectSnapshot: FSharpGeneratorProjectSnapshot, cancellationToken: CancellationToken) =
        let updatedDriver, result = this.RunGenerators(projectSnapshot, cancellationToken)
        let projectIdentity = FSharpProjectCacheIdentity.compute projectSnapshot result.GeneratedSources
        let driverIdentity = FSharpGeneratorDriverIdentity.compute generators options
        let identity = Hashing.sha256Many [ Hashing.toHex projectIdentity; Hashing.toHex driverIdentity ]

        let updatedOptions =
            {
                projectSnapshot.ProjectOptions with
                    SourceFiles = result.UpdatedSourceFiles
                    Stamp = Some(Hashing.toHex identity)
            }

        updatedDriver, updatedOptions, result
