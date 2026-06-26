namespace FSharp.Compiler.SourceGeneration

open System
open System.Collections.Immutable
open System.Threading

type FSharpIncrementalValueProvider<'T> =
    internal
        { Evaluate: FSharpGeneratorProjectSnapshot -> 'T }

type FSharpIncrementalValuesProvider<'T> =
    internal
        { EvaluateMany: FSharpGeneratorProjectSnapshot -> seq<'T> }

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

type internal PendingGeneratedSource =
    { GeneratorName: string
      HintName: string
      Kind: FSharpGeneratedSourceKind
      SourceText: FSharpSourceText
      Placement: FSharpGeneratedSourcePlacement
      CompanionImplementationHintName: string option }

type FSharpPostInitializationContext
    internal
    (
        generatorName: string,
        pending: ResizeArray<PendingGeneratedSource>,
        diagnostics: ResizeArray<FSharpGeneratorDiagnostic>,
        cancellationToken: CancellationToken
    ) =
    member _.CancellationToken = cancellationToken

    member _.AddImplementationSource(hintName: string, sourceText: FSharpSourceText) =
        pending.Add
            { GeneratorName = generatorName
              HintName = hintName
              Kind = Implementation
              SourceText = sourceText
              Placement = Prelude
              CompanionImplementationHintName = None }

    member _.ReportDiagnostic(diagnostic: FSharpGeneratorDiagnostic) = diagnostics.Add diagnostic

type FSharpSourceProductionContext
    internal
    (
        generatorName: string,
        pending: ResizeArray<PendingGeneratedSource>,
        diagnostics: ResizeArray<FSharpGeneratorDiagnostic>,
        cancellationToken: CancellationToken
    ) =
    member _.CancellationToken = cancellationToken

    member _.AddImplementationSource
        (hintName: string, sourceText: FSharpSourceText, placement: FSharpGeneratedSourcePlacement)
        =
        pending.Add
            { GeneratorName = generatorName
              HintName = hintName
              Kind = Implementation
              SourceText = sourceText
              Placement = placement
              CompanionImplementationHintName = None }

    member _.AddSignatureSource
        (
            hintName: string,
            sourceText: FSharpSourceText,
            companionImplementationHintName: string,
            placement: FSharpGeneratedSourcePlacement
        ) =
        pending.Add
            { GeneratorName = generatorName
              HintName = hintName
              Kind = Signature
              SourceText = sourceText
              Placement = placement
              CompanionImplementationHintName = Some companionImplementationHintName }

    member _.ReportDiagnostic(diagnostic: FSharpGeneratorDiagnostic) = diagnostics.Add diagnostic

type internal RegisteredSourceOutput = FSharpGeneratorProjectSnapshot -> FSharpSourceProductionContext -> unit

type FSharpIncrementalGeneratorInitializationContext
    internal
    (
        projectOptionsProvider,
        sourceFilesProvider,
        additionalTextsProvider,
        analyzerConfigOptionsProvider,
        postInitOutputs: ResizeArray<Action<FSharpPostInitializationContext>>,
        sourceOutputs: ResizeArray<RegisteredSourceOutput>
    ) =
    member _.ProjectOptionsProvider: FSharpIncrementalValueProvider<FSharpProjectOptions> =
        projectOptionsProvider

    member _.SourceFilesProvider: FSharpIncrementalValuesProvider<FSharpSourceFileSnapshot> =
        sourceFilesProvider

    member _.AdditionalTextsProvider: FSharpIncrementalValuesProvider<FSharpAdditionalText> =
        additionalTextsProvider

    member _.AnalyzerConfigOptionsProvider: FSharpIncrementalValueProvider<FSharpAnalyzerConfigOptions> =
        analyzerConfigOptionsProvider

    member _.RegisterPostInitializationOutput(action: Action<FSharpPostInitializationContext>) =
        postInitOutputs.Add action

    member _.RegisterSourceOutput<'T>
        (source: FSharpIncrementalValueProvider<'T>, action: Action<FSharpSourceProductionContext, 'T>)
        =
        sourceOutputs.Add(fun snapshot context -> action.Invoke(context, source.Evaluate snapshot))

    member _.RegisterSourceOutput<'T>
        (source: FSharpIncrementalValuesProvider<'T>, action: Action<FSharpSourceProductionContext, 'T>)
        =
        sourceOutputs.Add(fun snapshot context ->
            for value in source.EvaluateMany snapshot do
                action.Invoke(context, value))

type IFSharpIncrementalGenerator =
    abstract Initialize: FSharpIncrementalGeneratorInitializationContext -> unit

type internal RegisteredGenerator =
    { Generator: IFSharpIncrementalGenerator
      GeneratorName: string
      PostInitializationOutputs: ImmutableArray<Action<FSharpPostInitializationContext>>
      SourceOutputs: ImmutableArray<RegisteredSourceOutput>
      InitializationDiagnostics: ImmutableArray<FSharpGeneratorDiagnostic> }
