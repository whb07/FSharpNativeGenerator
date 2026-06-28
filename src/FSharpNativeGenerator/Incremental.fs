namespace FSharp.Compiler.SourceGeneration

open System
open System.Threading

[<NoComparison; NoEquality>]
type FSharpIncrementalValueProvider<'T> =
    internal
        { Evaluate: obj -> 'T }

[<NoComparison; NoEquality>]
type FSharpIncrementalValuesProvider<'T> =
    internal
        { EvaluateMany: obj -> seq<'T> }

[<RequireQualifiedAccess>]
module FSharpIncrementalValueProvider =
    let map (mapping: 'T -> 'U) (provider: FSharpIncrementalValueProvider<'T>) =
        { Evaluate = fun snapshot -> provider.Evaluate snapshot |> mapping }

    let bind (mapping: 'T -> FSharpIncrementalValueProvider<'U>) (provider: FSharpIncrementalValueProvider<'T>) =
        { Evaluate =
            fun snapshot ->
                let next = provider.Evaluate snapshot |> mapping
                next.Evaluate snapshot }

[<RequireQualifiedAccess>]
module FSharpIncrementalValuesProvider =
    let map (mapping: 'T -> 'U) (provider: FSharpIncrementalValuesProvider<'T>) =
        { EvaluateMany = fun snapshot -> provider.EvaluateMany snapshot |> Seq.map mapping }

    let filter (predicate: 'T -> bool) (provider: FSharpIncrementalValuesProvider<'T>) =
        { EvaluateMany = fun snapshot -> provider.EvaluateMany snapshot |> Seq.filter predicate }

    let choose (chooser: 'T -> 'U option) (provider: FSharpIncrementalValuesProvider<'T>) =
        { EvaluateMany = fun snapshot -> provider.EvaluateMany snapshot |> Seq.choose chooser }

    let collect (mapping: 'T -> seq<'U>) (provider: FSharpIncrementalValuesProvider<'T>) =
        { EvaluateMany = fun snapshot -> provider.EvaluateMany snapshot |> Seq.collect mapping }

type FSharpPostInitializationContext
    internal
    (
        pending: ResizeArray<PendingGeneratedSource>,
        diagnostics: ResizeArray<FSharpGeneratorDiagnostic>,
        cancellationToken: CancellationToken,
        createFileName: string -> FSharpGeneratedSourceKind -> string
    ) =
    member _.CancellationToken = cancellationToken

    member _.AddImplementationSource(hintName: string, sourceText: string) =
        pending.Add
            { GeneratorId = ""
              HintName = hintName
              FileName = createFileName hintName FSharpGeneratedSourceKind.Implementation
              SourceText = sourceText
              Kind = FSharpGeneratedSourceKind.Implementation
              Placement = Prelude
              CompanionImplementationHintName = None }

    member _.ReportDiagnostic(diagnostic: FSharpGeneratorDiagnostic) = diagnostics.Add diagnostic

type FSharpSourceProductionContext
    internal
    (
        pending: ResizeArray<PendingGeneratedSource>,
        diagnostics: ResizeArray<FSharpGeneratorDiagnostic>,
        cancellationToken: CancellationToken,
        createFileName: string -> FSharpGeneratedSourceKind -> string
    ) =
    member _.CancellationToken = cancellationToken

    member _.AddImplementationSource(hintName: string, sourceText: string, placement: FSharpGeneratedSourcePlacement) =
        pending.Add
            { GeneratorId = ""
              HintName = hintName
              FileName = createFileName hintName FSharpGeneratedSourceKind.Implementation
              SourceText = sourceText
              Kind = FSharpGeneratedSourceKind.Implementation
              Placement = placement
              CompanionImplementationHintName = None }

    member _.AddSignatureSource
        (
            hintName: string,
            sourceText: string,
            companionImplementationHintName: string,
            placement: FSharpGeneratedSourcePlacement
        ) =
        pending.Add
            { GeneratorId = ""
              HintName = hintName
              FileName = createFileName hintName FSharpGeneratedSourceKind.Signature
              SourceText = sourceText
              Kind = FSharpGeneratedSourceKind.Signature
              Placement = placement
              CompanionImplementationHintName = Some companionImplementationHintName }

    member _.ReportDiagnostic(diagnostic: FSharpGeneratorDiagnostic) = diagnostics.Add diagnostic

type internal RegisteredSourceOutput = obj -> FSharpSourceProductionContext -> unit

type FSharpIncrementalGeneratorInitializationContext
    internal
    (
        projectOptionsProvider: FSharpIncrementalValueProvider<FSharpGeneratorProjectSnapshot>,
        sourceFilesProvider: FSharpIncrementalValuesProvider<FSharpSourceFileInput>,
        additionalTextsProvider: FSharpIncrementalValuesProvider<FSharpAdditionalTextInput>,
        analyzerConfigOptionsProvider: FSharpIncrementalValueProvider<FSharpAnalyzerConfigOptions>,
        postInitOutputs: ResizeArray<Action<FSharpPostInitializationContext>>,
        sourceOutputs: ResizeArray<RegisteredSourceOutput>
    ) =
    member _.ProjectOptionsProvider = projectOptionsProvider
    member _.SourceFilesProvider = sourceFilesProvider
    member _.AdditionalTextsProvider = additionalTextsProvider
    member _.AnalyzerConfigOptionsProvider = analyzerConfigOptionsProvider

    member _.RegisterPostInitializationOutput(action: Action<FSharpPostInitializationContext>) =
        postInitOutputs.Add action

    member _.RegisterSourceOutput<'T>
        (source: FSharpIncrementalValueProvider<'T>, action: Action<FSharpSourceProductionContext, 'T>)
        =
        sourceOutputs.Add(fun state context -> action.Invoke(context, source.Evaluate state))

    member _.RegisterSourceOutput<'T>
        (source: FSharpIncrementalValuesProvider<'T>, action: Action<FSharpSourceProductionContext, 'T>)
        =
        sourceOutputs.Add(fun state context ->
            for value in source.EvaluateMany state do
                action.Invoke(context, value))

type IFSharpIncrementalGenerator =
    abstract Initialize: FSharpIncrementalGeneratorInitializationContext -> unit
