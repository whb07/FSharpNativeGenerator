namespace FSharp.Compiler.SourceGeneration

open System
open System.Collections.Generic
open System.IO
open FSharp.Compiler.Diagnostics

[<Sealed>]
type IncrementalGeneratorAdapter(inner: IFSharpIncrementalGenerator, generatorId: string) =
    let initLock = obj()
    let mutable initialized = false
    let mutable initializationDiagnostic: FSharpSourceGeneratorDiagnostic option = None
    let postInitOutputs = ResizeArray<Action<FSharpPostInitializationContext>>()
    let sourceOutputs = ResizeArray<RegisteredSourceOutput>()

    let emptyAnalyzerOptions =
        { GlobalOptions = Dictionary<string, string>() :> IReadOnlyDictionary<string, string>
          GetOptionsForPath = fun _ -> Dictionary<string, string>() :> IReadOnlyDictionary<string, string> }

    let initContext =
        FSharpIncrementalGeneratorInitializationContext(
            { Evaluate = fun state -> (state :?> FSharpGeneratorProjectSnapshot * Map<string, string>) |> fst },
            { EvaluateMany =
                fun state ->
                    let snapshot, _ = state :?> FSharpGeneratorProjectSnapshot * Map<string, string>
                    snapshot.SourceFiles
                    |> Seq.map (fun path ->
                        { Path = path
                          IsSignatureFile = Path.GetExtension(path).Equals(".fsi", StringComparison.OrdinalIgnoreCase) }) },
            { EvaluateMany =
                fun state ->
                    let _, additionalFiles = state :?> FSharpGeneratorProjectSnapshot * Map<string, string>
                    additionalFiles
                    |> Seq.map (fun kvp -> { Path = kvp.Key; Text = kvp.Value }) },
            { Evaluate = fun _ -> emptyAnalyzerOptions },
            postInitOutputs,
            sourceOutputs)

    let error id message =
        { Id = id
          Message = message
          Severity = FSharpDiagnosticSeverity.Error
          Range = None }

    let exceptionMessage (ex: exn) =
        if isNull ex.InnerException then ex.Message else ex.InnerException.Message

    let ensureInitialized () =
        if not initialized then
            lock initLock (fun () ->
                if not initialized then
                    try
                        inner.Initialize initContext
                    with ex ->
                        initializationDiagnostic <-
                            Some(error "FSG0003" (sprintf "Generator '%s' failed during initialization: %s" generatorId (exceptionMessage ex)))

                    initialized <- true)

    let sanitizeSegment (value: string) =
        let invalidChars = Path.GetInvalidFileNameChars() |> Set.ofArray

        let sanitized =
            value
            |> Seq.map (fun ch -> if invalidChars.Contains ch then '_' else ch)
            |> Seq.toArray
            |> String

        if String.IsNullOrWhiteSpace sanitized then "_" else sanitized

    let ensureExtension kind (hintName: string) =
        let extension = Path.GetExtension hintName

        if String.IsNullOrWhiteSpace extension then
            match kind with
            | FSharpGeneratedSourceKind.Implementation -> hintName + ".fs"
            | FSharpGeneratedSourceKind.Signature -> hintName + ".fsi"
        else
            hintName

    let createFileName projectDirectory hintName kind =
        let generatorSegments =
            generatorId.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map sanitizeSegment

        let fileName = ensureExtension kind (sanitizeSegment hintName)
        let root = Path.Combine(projectDirectory, "obj", "Generated", "FSharp")
        Path.Combine(Array.concat [| [| root |]; generatorSegments; [| fileName |] |]) |> Path.GetFullPath

    let mapDiagnostic (diagnostic: FSharpGeneratorDiagnostic) =
        FSharpGeneratorDiagnostic.toSourceGeneratorDiagnostic diagnostic

    let snapshotFromContext (ctx: FSharpSourceGeneratorContext) =
        { ProjectFileName = ctx.ProjectFileName
          ProjectDirectory = ctx.ProjectDirectory
          SourceFiles = ctx.SourceFiles
          OtherOptions = ctx.OtherOptions
          References = ctx.References
          DefineConstants = ctx.DefineConstants
          OutputFile = ctx.OutputFile
          AssemblyName = ctx.AssemblyName }

    let contextProjectDirectory (ctx: FSharpSourceGeneratorContext) =
        if String.IsNullOrWhiteSpace ctx.ProjectDirectory then
            Directory.GetCurrentDirectory()
        else
            ctx.ProjectDirectory

    interface IFSharpSourceGenerator with
        member _.Generate(ctx: FSharpSourceGeneratorContext) : FSharpSourceGeneratorOutput =
            ensureInitialized ()

            let pending = ResizeArray<PendingGeneratedSource>()
            let diagnostics = ResizeArray<FSharpGeneratorDiagnostic>()
            let projectDirectory = contextProjectDirectory ctx
            let createFileName = createFileName projectDirectory

            for action in postInitOutputs do
                let postContext = FSharpPostInitializationContext(pending, diagnostics, ctx.CancellationToken, createFileName)

                try
                    action.Invoke postContext
                with ex ->
                    diagnostics.Add(
                        FSharpGeneratorDiagnostic.create
                            "FSG0004"
                            (sprintf "Generator '%s' failed during post-initialization output: %s" generatorId (exceptionMessage ex))
                            FSharpDiagnosticSeverity.Error
                    )

            let snapshot = snapshotFromContext ctx

            for output in sourceOutputs do
                let productionContext = FSharpSourceProductionContext(pending, diagnostics, ctx.CancellationToken, createFileName)

                try
                    output (snapshot, ctx.AdditionalFiles) productionContext
                with ex ->
                    diagnostics.Add(
                        FSharpGeneratorDiagnostic.create
                            "FSG0005"
                            (sprintf "Generator '%s' failed during source output: %s" generatorId (exceptionMessage ex))
                            FSharpDiagnosticSeverity.Error
                    )

            let pendingWithGeneratorIds =
                pending
                |> Seq.map (fun source -> { source with GeneratorId = generatorId })
                |> Seq.toList

            let resolved =
                FSharpGeneratedSourcePlacementResolver.resolve ctx.SourceFiles ctx.OtherOptions pendingWithGeneratorIds

            let initializationDiagnostics = initializationDiagnostic |> Option.toList

            match resolved with
            | Ok(generatedSources, placementDiagnostics) ->
                { GeneratedSources = generatedSources
                  Diagnostics =
                    [ yield! initializationDiagnostics
                      yield! diagnostics |> Seq.map mapDiagnostic
                      yield! placementDiagnostics ] }
            | Error placementDiagnostics ->
                { GeneratedSources = []
                  Diagnostics =
                    [ yield! initializationDiagnostics
                      yield! diagnostics |> Seq.map mapDiagnostic
                      yield! placementDiagnostics ] }

    interface IFSharpSourceGeneratorWithId with
        member _.GeneratorId = generatorId
