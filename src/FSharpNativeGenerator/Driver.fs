namespace FSharp.Compiler.SourceGeneration

open System
open System.Collections.Immutable
open System.Diagnostics
open System.IO
open System.Text
open System.Threading

type internal FSharpGeneratorRunCacheEntry =
    { Key: string
      Result: FSharpGeneratorDriverRunResult }

module private ProjectOutputPath =
    let private tryPrefixedValue (prefix: string) (value: string) =
        if value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
            let candidate = value.Substring(prefix.Length)

            if String.IsNullOrWhiteSpace candidate then
                None
            else
                Some candidate
        else
            None

    let private projectDirectory (projectOptions: FSharpProjectOptions) =
        let projectPath = Path.GetFullPath projectOptions.ProjectFilePath
        let directory = Path.GetDirectoryName projectPath

        if String.IsNullOrWhiteSpace directory then
            Environment.CurrentDirectory
        else
            directory

    let private normalize (projectOptions: FSharpProjectOptions) (path: string) =
        let trimmedPath = path.Trim().Trim('"')

        if Path.IsPathRooted trimmedPath then
            Path.GetFullPath trimmedPath
        else
            Path.GetFullPath(Path.Combine(projectDirectory projectOptions, trimmedPath))

    let tryGet (projectOptions: FSharpProjectOptions) =
        let rec loop remainingOptions =
            match remainingOptions with
            | [] -> None
            | option :: value :: _ when
                String.Equals(option, "--out", StringComparison.OrdinalIgnoreCase)
                || String.Equals(option, "-o", StringComparison.OrdinalIgnoreCase)
                ->
                Some value
            | option :: tail ->
                match tryPrefixedValue "--out:" option with
                | Some value -> Some value
                | None ->
                    let outputPath =
                        [ "--out="; "-o:"; "-o="; "/out:"; "/out=" ]
                        |> List.tryPick (fun prefix -> tryPrefixedValue prefix option)

                    match outputPath with
                    | Some value -> Some value
                    | None -> loop tail

        projectOptions.OtherOptions
        |> Seq.toList
        |> loop
        |> Option.map (normalize projectOptions)

module private GeneratorIdentity =
    let create (generatorType: Type) =
        let assemblyName = generatorType.Assembly.GetName().Name

        let typeName =
            if String.IsNullOrWhiteSpace generatorType.FullName then
                generatorType.Name
            else
                generatorType.FullName

        if String.IsNullOrWhiteSpace assemblyName then
            typeName
        else
            assemblyName + "." + typeName

type FSharpGeneratorDriver
    private
    (
        generators: ImmutableArray<IFSharpIncrementalGenerator>,
        options: FSharpGeneratorDriverOptions,
        registeredGenerators: ImmutableArray<RegisteredGenerator> option,
        runCacheEntry: FSharpGeneratorRunCacheEntry option
    ) =
    static member Create(generators: seq<IFSharpIncrementalGenerator>, options: FSharpGeneratorDriverOptions) =
        FSharpGeneratorDriver(ImmutableArray.CreateRange generators, options, None, None)

    member _.Options = options

    member private this.InitializeGenerators() =
        match registeredGenerators with
        | Some value -> this, value
        | None ->
            let registered =
                generators
                |> Seq.map (fun generator ->
                    let generatorType = generator.GetType()
                    let generatorName = GeneratorIdentity.create generatorType
                    let postInitOutputs = ResizeArray<Action<FSharpPostInitializationContext>>()
                    let sourceOutputs = ResizeArray<RegisteredSourceOutput>()
                    let diagnostics = ResizeArray<FSharpGeneratorDiagnostic>()

                    let generatorAttribute = FSharpGeneratorAttributeHelpers.tryGet generatorType

                    match generatorAttribute with
                    | None ->
                        diagnostics.Add(
                            Diagnostics.error
                                "FSG0002"
                                (sprintf "Generator type '%s' is missing FSharpGeneratorAttribute." generatorName)
                        )
                    | Some attribute when not (FSharpGeneratorAttributeHelpers.isSupportedApiVersion attribute) ->
                        diagnostics.Add(
                            Diagnostics.error
                                "FSG0015"
                                (sprintf
                                    "Generator type '%s' references unsupported F# source-generation API version %d. Supported version is %d."
                                    generatorName
                                    attribute.ApiVersion
                                    FSharpGeneratorApiVersion.Current)
                        )
                    | Some _ ->
                        let initContext =
                            FSharpIncrementalGeneratorInitializationContext(
                                { Evaluate = fun snapshot -> snapshot.ProjectOptions },
                                { EvaluateMany = fun snapshot -> snapshot.SourceFiles :> seq<_> },
                                { EvaluateMany = fun snapshot -> snapshot.AdditionalTexts :> seq<_> },
                                { Evaluate = fun snapshot -> snapshot.AnalyzerConfigOptions },
                                postInitOutputs,
                                sourceOutputs
                            )

                        try
                            generator.Initialize initContext
                        with ex ->
                            diagnostics.Add(
                                Diagnostics.error
                                    "FSG0003"
                                    (sprintf "Generator '%s' threw during initialization: %s" generatorName ex.Message)
                            )

                    { Generator = generator
                      GeneratorName = generatorName
                      PostInitializationOutputs = ImmutableArray.CreateRange postInitOutputs
                      SourceOutputs = ImmutableArray.CreateRange sourceOutputs
                      InitializationDiagnostics = ImmutableArray.CreateRange diagnostics })
                |> ImmutableArray.CreateRange

            FSharpGeneratorDriver(generators, options, Some registered, runCacheEntry), registered

    member private _.MaterializePending(pending: PendingGeneratedSource list) =
        let diagnostics = ResizeArray<FSharpGeneratorDiagnostic>()

        let duplicateHints =
            pending
            |> Seq.countBy (fun source -> source.GeneratorName, source.HintName, source.Kind)
            |> Seq.filter (fun (_, count) -> count > 1)

        for ((generatorName, hintName, _), _) in duplicateHints do
            diagnostics.Add(
                Diagnostics.error
                    "FSG0006"
                    (sprintf "Generator '%s' emitted duplicate hint name '%s'." generatorName hintName)
            )

        let materialized =
            pending
            |> List.choose (fun source ->
                if String.IsNullOrWhiteSpace source.HintName then
                    diagnostics.Add(
                        Diagnostics.error
                            "FSG0011"
                            (sprintf "Generator '%s' emitted an empty hint name." source.GeneratorName)
                    )

                    None
                elif GeneratedPaths.conflictsWithKind source.Kind source.HintName then
                    diagnostics.Add(
                        Diagnostics.error
                            "FSG0013"
                            (sprintf "Generated source '%s' kind does not match its file extension." source.HintName)
                    )

                    None
                else
                    let resolvedPath =
                        GeneratedPaths.resolvedPath
                            options.GeneratedRoot
                            source.GeneratorName
                            source.Kind
                            source.HintName

                    Some
                        { GeneratorName = source.GeneratorName
                          HintName = source.HintName
                          ResolvedPath = resolvedPath
                          Kind = source.Kind
                          SourceText = source.SourceText
                          Placement = source.Placement
                          Checksum = FSharpSourceText.checksum source.SourceText })

        let duplicatePaths =
            materialized
            |> Seq.groupBy (fun source -> Path.GetFullPath(source.ResolvedPath).ToUpperInvariant())
            |> Seq.choose (fun (_, sources) ->
                let sources = sources |> Seq.toList

                if sources.Length > 1 then
                    Some sources.Head.ResolvedPath
                else
                    None)

        for resolvedPath in duplicatePaths do
            diagnostics.Add(
                Diagnostics.error "FSG0006" (sprintf "Generated path '%s' is produced more than once." resolvedPath)
            )

        materialized, diagnostics |> Seq.toList

    member this.RunGenerators(projectSnapshot: FSharpGeneratorProjectSnapshot, cancellationToken: CancellationToken) =
        cancellationToken.ThrowIfCancellationRequested()

        let stopwatch = Stopwatch.StartNew()
        let updatedDriver, initializedGenerators = this.InitializeGenerators()

        let cacheKey =
            FSharpGeneratorRunCacheKey.compute generators options projectSnapshot cancellationToken
            |> Hashing.toHex

        let runFresh () =
            let diagnostics = ResizeArray<FSharpGeneratorDiagnostic>()
            let postInitializationPending = ResizeArray<PendingGeneratedSource>()
            let sourcePending = ResizeArray<PendingGeneratedSource>()

            let hasErrorDiagnostics () =
                diagnostics |> Seq.exists (fun diagnostic -> diagnostic.Severity = Error)

            let projectSourceFiles =
                projectSnapshot.ProjectOptions.SourceFiles
                |> Seq.map Path.GetFullPath
                |> Seq.toList

            let snapshotSourceFiles =
                projectSnapshot.SourceFiles
                |> Seq.map _.Path
                |> Seq.map Path.GetFullPath
                |> Seq.toList

            if projectSourceFiles.Length <> snapshotSourceFiles.Length then
                diagnostics.Add(
                    Diagnostics.error
                        "FSG0011"
                        (sprintf
                            "Project source file count %d does not match source snapshot count %d."
                            projectSourceFiles.Length
                            snapshotSourceFiles.Length)
                )
            else
                let mismatch =
                    List.zip projectSourceFiles snapshotSourceFiles
                    |> List.tryFind (fun (projectPath, snapshotPath) ->
                        not (String.Equals(projectPath, snapshotPath, StringComparison.OrdinalIgnoreCase)))

                match mismatch with
                | Some(projectPath, snapshotPath) ->
                    diagnostics.Add(
                        Diagnostics.error
                            "FSG0011"
                            (sprintf
                                "Project source path '%s' does not match source snapshot path '%s'."
                                projectPath
                                snapshotPath)
                    )
                | None -> ()

            if options.MaxGenerationPasses <> 1 then
                diagnostics.Add(
                    Diagnostics.error
                        "FSG0010"
                        "Fixed-point generation is not supported in V1. MaxGenerationPasses must be 1."
                )

            match ProjectOutputPath.tryGet projectSnapshot.ProjectOptions with
            | Some projectOutputPath ->
                for generator in initializedGenerators do
                    let generatorAssemblyPath = generator.Generator.GetType().Assembly.Location

                    if
                        not (String.IsNullOrWhiteSpace generatorAssemblyPath)
                        && String.Equals(
                            Path.GetFullPath generatorAssemblyPath,
                            projectOutputPath,
                            StringComparison.OrdinalIgnoreCase
                        )
                    then
                        diagnostics.Add(
                            Diagnostics.error
                                "FSG0002"
                                (sprintf
                                    "Generator type '%s' cannot be used while building the assembly that defines it ('%s')."
                                    generator.GeneratorName
                                    projectOutputPath)
                        )
            | None -> ()

            let runnableGenerators = ResizeArray<RegisteredGenerator>()

            if not (hasErrorDiagnostics ()) then
                for generator in initializedGenerators do
                    diagnostics.AddRange generator.InitializationDiagnostics

                    if
                        not (
                            generator.InitializationDiagnostics
                            |> Seq.exists (fun diagnostic -> diagnostic.Severity = Error)
                        )
                    then
                        runnableGenerators.Add generator

            if not (hasErrorDiagnostics ()) then
                for generator in runnableGenerators do
                    let postInitContext =
                        FSharpPostInitializationContext(
                            generator.GeneratorName,
                            postInitializationPending,
                            diagnostics,
                            cancellationToken
                        )

                    for output in generator.PostInitializationOutputs do
                        cancellationToken.ThrowIfCancellationRequested()

                        try
                            output.Invoke postInitContext
                        with
                        | :? OperationCanceledException -> reraise ()
                        | ex ->
                            diagnostics.Add(
                                Diagnostics.error
                                    "FSG0004"
                                    (sprintf
                                        "Generator '%s' threw during post-initialization output: %s"
                                        generator.GeneratorName
                                        ex.Message)
                            )

            let postInitializationMaterialized, postInitializationMaterializeDiagnostics =
                updatedDriver.MaterializePending(postInitializationPending |> Seq.toList)

            diagnostics.AddRange postInitializationMaterializeDiagnostics

            for diagnostic in
                postInitializationMaterialized
                |> Seq.collect (GeneratedSourceValidation.validate projectSnapshot.ProjectOptions) do
                diagnostics.Add diagnostic

            let sourceOutputSnapshot =
                if hasErrorDiagnostics () then
                    projectSnapshot
                else
                    let postInitializationSourceFiles =
                        postInitializationMaterialized
                        |> List.map (fun source ->
                            FSharpSourceFileSnapshot.createFromSourceText source.ResolvedPath source.SourceText)

                    let sourceFiles =
                        (postInitializationMaterialized |> List.map _.ResolvedPath)
                        @ (projectSnapshot.ProjectOptions.SourceFiles |> Seq.toList)

                    { projectSnapshot with
                        ProjectOptions =
                            { projectSnapshot.ProjectOptions with
                                SourceFiles = ImmutableArray.CreateRange sourceFiles }
                        SourceFiles =
                            ImmutableArray.CreateRange(
                                postInitializationSourceFiles @ (projectSnapshot.SourceFiles |> Seq.toList)
                            ) }

            if not (hasErrorDiagnostics ()) then
                for generator in runnableGenerators do
                    let productionContext =
                        FSharpSourceProductionContext(
                            generator.GeneratorName,
                            sourcePending,
                            diagnostics,
                            cancellationToken
                        )

                    for output in generator.SourceOutputs do
                        cancellationToken.ThrowIfCancellationRequested()

                        try
                            output sourceOutputSnapshot productionContext
                        with
                        | :? OperationCanceledException -> reraise ()
                        | ex ->
                            diagnostics.Add(
                                Diagnostics.error
                                    "FSG0004"
                                    (sprintf
                                        "Generator '%s' threw during source output: %s"
                                        generator.GeneratorName
                                        ex.Message)
                            )

            let skipFinalMaterialization = hasErrorDiagnostics ()

            let pending =
                (postInitializationPending |> Seq.toList) @ (sourcePending |> Seq.toList)

            let materialized, materializeDiagnostics =
                if skipFinalMaterialization then
                    postInitializationMaterialized, []
                else
                    updatedDriver.MaterializePending pending

            diagnostics.AddRange materializeDiagnostics

            if not skipFinalMaterialization then
                for diagnostic in
                    materialized
                    |> Seq.collect (GeneratedSourceValidation.validate projectSnapshot.ProjectOptions) do
                    diagnostics.Add diagnostic

            let originalSourcePathSet =
                projectSnapshot.ProjectOptions.SourceFiles
                |> Seq.map (fun path -> Path.GetFullPath(path).ToUpperInvariant())
                |> Set.ofSeq

            for source in materialized do
                if originalSourcePathSet.Contains(Path.GetFullPath(source.ResolvedPath).ToUpperInvariant()) then
                    diagnostics.Add(
                        Diagnostics.error
                            "FSG0011"
                            (sprintf
                                "Generated source '%s' resolves to original source path '%s'."
                                source.HintName
                                source.ResolvedPath)
                    )

            let originalImplementationHints =
                projectSnapshot.ProjectOptions.SourceFiles
                |> Seq.filter (fun path -> path.EndsWith(".fs", StringComparison.OrdinalIgnoreCase))
                |> Seq.map (fun path -> Path.GetFileNameWithoutExtension(path).ToUpperInvariant())
                |> Set.ofSeq

            let units, unitDiagnostics =
                Placement.buildUnits originalImplementationHints materialized (pending |> Seq.toList)

            diagnostics.AddRange unitDiagnostics

            let generatedPaths = materialized |> List.map _.ResolvedPath

            let updatedSourceFiles, placementDiagnostics =
                Placement.resolve
                    projectSnapshot.ProjectOptions.OutputKind
                    (projectSnapshot.ProjectOptions.SourceFiles |> Seq.toList)
                    generatedPaths
                    units

            diagnostics.AddRange placementDiagnostics

            let generatedSources =
                if diagnostics |> Seq.exists (fun diagnostic -> diagnostic.Severity = Error) then
                    ImmutableArray<FSharpGeneratedSource>.Empty
                else
                    ImmutableArray.CreateRange materialized

            let generatedStore =
                generatedSources
                |> Seq.fold
                    (fun (store: FSharpGeneratedSourceStore) (source: FSharpGeneratedSource) -> store.Add source)
                    FSharpGeneratedSourceStore.Empty

            if
                options.EmitGeneratedFiles
                && not (diagnostics |> Seq.exists (fun diagnostic -> diagnostic.Severity = Error))
            then
                let outputRoot = defaultArg options.GeneratedFilesOutputPath options.GeneratedRoot

                for source in generatedSources do
                    let relativePath =
                        Path.GetRelativePath(Path.GetFullPath(options.GeneratedRoot), source.ResolvedPath)

                    let outputPath = Path.Combine(outputRoot, relativePath)
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)) |> ignore
                    File.WriteAllText(outputPath, source.SourceText.Text, Encoding.UTF8)

            stopwatch.Stop()

            let result =
                { GeneratedSources = generatedSources
                  Diagnostics = ImmutableArray.CreateRange diagnostics
                  UpdatedSourceFiles =
                    if diagnostics |> Seq.exists (fun diagnostic -> diagnostic.Severity = Error) then
                        projectSnapshot.ProjectOptions.SourceFiles
                    else
                        updatedSourceFiles
                  GeneratedSourceStore = generatedStore
                  ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
                  CacheHit = false }

            options.ReportPath |> Option.iter (fun path -> RunReport.write path result)

            FSharpGeneratorDriver(
                generators,
                options,
                Some initializedGenerators,
                Some { Key = cacheKey; Result = result }
            ),
            result

        match runCacheEntry with
        | Some entry when entry.Key = cacheKey ->
            let cachedResult =
                { entry.Result with
                    CacheHit = true
                    ElapsedMilliseconds = stopwatch.ElapsedMilliseconds }

            options.ReportPath
            |> Option.iter (fun path -> RunReport.write path cachedResult)

            updatedDriver, cachedResult
        | _ -> runFresh ()

    member this.RunGeneratorsAndUpdateProjectOptions
        (projectSnapshot: FSharpGeneratorProjectSnapshot, cancellationToken: CancellationToken)
        =
        let updatedDriver, result = this.RunGenerators(projectSnapshot, cancellationToken)

        let projectIdentity =
            FSharpProjectCacheIdentity.compute projectSnapshot result.GeneratedSources

        let driverIdentity = FSharpGeneratorDriverIdentity.compute generators options

        let identity =
            Hashing.sha256Many [ Hashing.toHex projectIdentity; Hashing.toHex driverIdentity ]

        let updatedOptions =
            { projectSnapshot.ProjectOptions with
                SourceFiles = result.UpdatedSourceFiles
                Stamp = Some(Hashing.toHex identity) }

        updatedDriver, updatedOptions, result
