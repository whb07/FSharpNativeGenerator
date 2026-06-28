namespace FSharp.Compiler.SourceGeneration

open System
open System.Collections.Generic
open System.IO
open FSharp.Compiler.Diagnostics

[<NoComparison>]
type PendingGeneratedSource =
    { GeneratorId: string
      HintName: string
      FileName: string
      SourceText: string
      Kind: FSharpGeneratedSourceKind
      Placement: FSharpGeneratedSourcePlacement
      CompanionImplementationHintName: string option }

module FSharpGeneratedSourcePlacementResolver =
    let private error id message =
        { Id = id
          Message = message
          Severity = FSharpDiagnosticSeverity.Error
          Range = None }

    let private isApplication (otherOptions: string list) =
        let rec loop options =
            match options with
            | [] -> false
            | option :: value :: rest when option = "--target" && (value = "exe" || value = "winexe") -> true
            | option :: rest when option = "--target:exe" || option = "--target=exe" || option = "--target:winexe" || option = "--target=winexe" -> true
            | _ :: rest -> loop rest

        loop otherOptions

    let private extensionMatchesKind (source: PendingGeneratedSource) =
        let extension = Path.GetExtension(source.FileName)

        String.IsNullOrWhiteSpace extension
        || match source.Kind with
           | FSharpGeneratedSourceKind.Implementation -> extension.Equals(".fs", StringComparison.OrdinalIgnoreCase)
           | FSharpGeneratedSourceKind.Signature -> extension.Equals(".fsi", StringComparison.OrdinalIgnoreCase)

    let resolve
        (originalFiles: string list)
        (otherOptions: string list)
        (generated: PendingGeneratedSource list)
        : Result<FSharpGeneratedSource list * FSharpSourceGeneratorDiagnostic list, FSharpSourceGeneratorDiagnostic list> =

        let diagnostics = ResizeArray<FSharpSourceGeneratorDiagnostic>()
        let originalSet = HashSet<string>(originalFiles, StringComparer.OrdinalIgnoreCase)
        let generatedPathSet = HashSet<string>(generated |> List.map _.FileName, StringComparer.OrdinalIgnoreCase)
        let hints = HashSet<string>(StringComparer.OrdinalIgnoreCase)

        for source in generated do
            if not (hints.Add source.HintName) then
                diagnostics.Add(error "FSG0006" (sprintf "Duplicate generated hint name '%s' within generator '%s'." source.HintName source.GeneratorId))

            if not (extensionMatchesKind source) then
                diagnostics.Add(error "FSG0013" (sprintf "Generated source '%s' kind does not match file extension '%s'." source.HintName source.FileName))

            match source.Kind, source.CompanionImplementationHintName with
            | FSharpGeneratedSourceKind.Signature, Some companion when not (generated |> List.exists (fun candidate -> candidate.HintName = companion && candidate.Kind = FSharpGeneratedSourceKind.Implementation)) ->
                diagnostics.Add(error "FSG0014" (sprintf "Generated signature '%s' has no generated implementation companion '%s'." source.HintName companion))
            | _ -> ()

            match source.Placement with
            | BeforeFile anchor | AfterFile anchor ->
                if generatedPathSet.Contains anchor || not (originalSet.Contains anchor) then
                    diagnostics.Add(error "FSG0007" (sprintf "Generated source '%s' references missing or generated placement anchor '%s'." source.HintName anchor))
            | EndOfProject when isApplication otherOptions ->
                diagnostics.Add(error "FSG0012" (sprintf "Generated source '%s' cannot use EndOfProject placement for an application." source.HintName))
            | _ -> ()

        if diagnostics.Count > 0 then
            Error (Seq.toList diagnostics)
        else
            let firstOriginal = originalFiles |> List.tryHead

            let lastImplementation =
                originalFiles
                |> List.rev
                |> List.tryFind (fun path -> not (Path.GetExtension(path).Equals(".fsi", StringComparison.OrdinalIgnoreCase)))

            let mapOrder placement =
                match placement with
                | Prelude ->
                    match firstOriginal with
                    | Some first -> FSharpGeneratedSourceOrder.BeforeFile first
                    | None -> FSharpGeneratedSourceOrder.EndOfProject
                | BeforeFile anchor -> FSharpGeneratedSourceOrder.BeforeFile anchor
                | AfterFile anchor -> FSharpGeneratedSourceOrder.AfterFile anchor
                | BeforeLastSourceFile ->
                    match lastImplementation with
                    | Some last -> FSharpGeneratedSourceOrder.BeforeFile last
                    | None -> FSharpGeneratedSourceOrder.EndOfProject
                | EndOfProject -> FSharpGeneratedSourceOrder.EndOfProject

            generated
            |> List.map (fun source ->
                { HintName = source.HintName
                  FileName = source.FileName
                  SourceText = source.SourceText
                  Kind = source.Kind
                  Order = mapOrder source.Placement })
            |> fun sources -> Ok(sources, [])

module Placement =
    let resolveOrder originalFiles generated otherOptions =
        let pending =
            generated
            |> List.map (fun (hintName, placement, kind) ->
                { GeneratorId = ""
                  HintName = hintName
                  FileName = hintName
                  SourceText = ""
                  Kind = kind
                  Placement = placement
                  CompanionImplementationHintName = None })

        FSharpGeneratedSourcePlacementResolver.resolve originalFiles otherOptions pending
        |> Result.map (fun (sources, _) -> sources |> List.map (fun source -> source.HintName, source.Order))
