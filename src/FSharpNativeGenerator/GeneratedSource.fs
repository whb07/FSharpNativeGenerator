namespace FSharp.Compiler.SourceGeneration

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Text

module internal GeneratedPaths =
    let private invalidChars =
        Array.concat
            [ Path.GetInvalidFileNameChars()
              [| '<'; '>'; ':'; '"'; '/'; '\\'; '|'; '?'; '*' |] ]
        |> Array.distinct
        |> String
        |> Regex.Escape

    let private invalidPattern =
        Regex(sprintf "[%s]+" invalidChars, RegexOptions.Compiled)

    let stripKnownExtension (hintName: string) =
        if hintName.EndsWith(".fsi", StringComparison.OrdinalIgnoreCase) then
            hintName.Substring(0, hintName.Length - 4)
        elif hintName.EndsWith(".fs", StringComparison.OrdinalIgnoreCase) then
            hintName.Substring(0, hintName.Length - 3)
        else
            hintName

    let sanitizeSegment (value: string) =
        let cleaned = invalidPattern.Replace(stripKnownExtension value, "_")

        if String.IsNullOrWhiteSpace(cleaned) then
            "Generated"
        else
            cleaned

    let extensionFor =
        function
        | Implementation -> ".fs"
        | Signature -> ".fsi"

    let conflictsWithKind kind (hintName: string) =
        match kind with
        | Implementation -> hintName.EndsWith(".fsi", StringComparison.OrdinalIgnoreCase)
        | Signature ->
            hintName.EndsWith(".fs", StringComparison.OrdinalIgnoreCase)
            && not (hintName.EndsWith(".fsi", StringComparison.OrdinalIgnoreCase))

    let resolvedPath generatedRoot generatorName kind hintName =
        Path.Combine(generatedRoot, sanitizeSegment generatorName, sanitizeSegment hintName + extensionFor kind)
        |> Path.GetFullPath

module internal Diagnostics =
    let error id message =
        FSharpGeneratorDiagnostic.create id message Error

    let withPath path (diagnostic: FSharpGeneratorDiagnostic) =
        { diagnostic with FilePath = Some path }

module internal RunReport =
    let private generatedSourceReport (source: FSharpGeneratedSource) =
        { GeneratorName = source.GeneratorName
          HintName = source.HintName
          ResolvedPath = source.ResolvedPath
          Kind = string source.Kind
          Placement = string source.Placement
          Checksum = Hashing.toHex source.Checksum }

    let private diagnosticReport (diagnostic: FSharpGeneratorDiagnostic) : FSharpGeneratorDiagnosticReport =
        { Id = diagnostic.Id
          Message = diagnostic.Message
          Severity = string diagnostic.Severity
          FilePath = diagnostic.FilePath
          Range = diagnostic.Range }

    let write path (result: FSharpGeneratorDriverRunResult) =
        let report =
            { GeneratedSources =
                result.GeneratedSources
                |> Seq.map generatedSourceReport
                |> ImmutableArray.CreateRange
              Diagnostics = result.Diagnostics |> Seq.map diagnosticReport |> ImmutableArray.CreateRange
              UpdatedSourceFiles = result.UpdatedSourceFiles
              ElapsedMilliseconds = result.ElapsedMilliseconds
              CacheHit = result.CacheHit }

        let options = JsonSerializerOptions(WriteIndented = true)
        let json = JsonSerializer.Serialize(report, options)

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)))
        |> ignore

        File.WriteAllText(path, json, Encoding.UTF8)

module internal GeneratedSourceValidation =
    let private checker = lazy (FSharpChecker.Create())

    let private hasExplicitModuleOrNamespace (sourceText: FSharpSourceText) =
        sourceText.Text.Split([| "\r\n"; "\n" |], StringSplitOptions.None)
        |> Seq.map _.Trim()
        |> Seq.exists (fun line ->
            line.StartsWith("module ", StringComparison.Ordinal)
            || line.StartsWith("namespace ", StringComparison.Ordinal))

    let private tryPrefixedValue (prefix: string) (value: string) =
        if value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
            let suffix = value.Substring(prefix.Length)

            if String.IsNullOrWhiteSpace suffix then
                None
            else
                Some suffix
        else
            None

    let private parseOptionValues (names: Set<string>) (prefixes: string list) (otherOptions: ImmutableArray<string>) =
        let rec loop (remainingOptions: string list) values =
            match remainingOptions with
            | [] -> List.rev values
            | option :: value :: tail when
                names.Contains(option.ToUpperInvariant())
                && not (String.IsNullOrWhiteSpace value)
                ->
                loop tail (value :: values)
            | option :: tail ->
                let prefixedValue =
                    prefixes |> List.tryPick (fun prefix -> tryPrefixedValue prefix option)

                match prefixedValue with
                | Some value -> loop tail (value :: values)
                | None -> loop tail values

        loop (otherOptions |> Seq.toList) []

    let private conditionalDefines (projectOptions: FSharp.Compiler.SourceGeneration.FSharpProjectOptions) =
        let values =
            parseOptionValues
                (set [ "--DEFINE"; "-D"; "/DEFINE"; "/D" ])
                [ "--define:"; "--define="; "-d:"; "-d="; "/define:"; "/define="; "/d:"; "/d=" ]
                projectOptions.OtherOptions

        values
        |> List.collect (fun value ->
            value.Split([| ';'; ',' |], StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
            |> Array.toList)

    let private languageVersion (projectOptions: FSharp.Compiler.SourceGeneration.FSharpProjectOptions) =
        parseOptionValues
            (set [ "--LANGVERSION"; "/LANGVERSION" ])
            [ "--langversion:"; "--langversion="; "/langversion:"; "/langversion=" ]
            projectOptions.OtherOptions
        |> List.tryLast

    let private parsingOptions
        (projectOptions: FSharp.Compiler.SourceGeneration.FSharpProjectOptions)
        (sourcePath: string)
        =
        { FSharpParsingOptions.Default with
            SourceFiles = [| sourcePath |]
            ConditionalDefines = conditionalDefines projectOptions
            LangVersionText =
                languageVersion projectOptions
                |> Option.defaultValue FSharpParsingOptions.Default.LangVersionText
            IsExe = projectOptions.OutputKind = Application }

    let private diagnosticSeverity (severity: FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity) =
        match severity with
        | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error -> Error
        | FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Warning -> Warning
        | _ -> Info

    let private diagnosticRange (diagnostic: FSharp.Compiler.Diagnostics.FSharpDiagnostic) =
        Some
            { FilePath = diagnostic.FileName
              StartLine = diagnostic.StartLine
              StartColumn = diagnostic.StartColumn
              EndLine = diagnostic.EndLine
              EndColumn = diagnostic.EndColumn }

    let private parseDiagnostics
        (projectOptions: FSharp.Compiler.SourceGeneration.FSharpProjectOptions)
        (source: FSharpGeneratedSource)
        (cancellationToken: CancellationToken)
        =
        cancellationToken.ThrowIfCancellationRequested()

        let sourceText = SourceText.ofString source.SourceText.Text
        let parseOptions = parsingOptions projectOptions source.ResolvedPath

        checker.Value.ParseFile(source.ResolvedPath, sourceText, parseOptions)
        |> Async.RunSynchronously
        |> _.Diagnostics
        |> Seq.map (fun diagnostic ->
            let generatorDiagnostic: FSharpGeneratorDiagnostic =
                { Id = "FSG0005"
                  Message = sprintf "Generated source '%s' parse failed: %s" source.HintName diagnostic.Message
                  Severity = diagnosticSeverity diagnostic.Severity
                  Range = diagnosticRange diagnostic
                  FilePath = Some source.ResolvedPath }

            generatorDiagnostic)

    let validate
        (projectOptions: FSharp.Compiler.SourceGeneration.FSharpProjectOptions)
        (source: FSharpGeneratedSource)
        (cancellationToken: CancellationToken)
        =
        seq {
            cancellationToken.ThrowIfCancellationRequested()

            if String.IsNullOrWhiteSpace source.SourceText.Text then
                yield
                    Diagnostics.error "FSG0005" (sprintf "Generated source '%s' is empty." source.HintName)
                    |> Diagnostics.withPath source.ResolvedPath

            elif not (hasExplicitModuleOrNamespace source.SourceText) then
                yield
                    Diagnostics.error
                        "FSG0005"
                        (sprintf
                            "Generated source '%s' must include an explicit module or namespace declaration."
                            source.HintName)
                    |> Diagnostics.withPath source.ResolvedPath
            else
                yield! parseDiagnostics projectOptions source cancellationToken
        }

module internal Placement =
    type Unit =
        { Placement: FSharpGeneratedSourcePlacement
          Paths: string list
          Sources: FSharpGeneratedSource list }

    let private samePath left right =
        String.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase)

    let private insertAt index values sourceFiles =
        let before = sourceFiles |> List.take index
        let after = sourceFiles |> List.skip index
        before @ values @ after

    let private findPathIndex path sourceFiles =
        sourceFiles |> List.tryFindIndex (samePath path)

    let private finalOriginalImplementationIndex (originalSourceFiles: string list) =
        originalSourceFiles
        |> List.mapi (fun index path -> index, path)
        |> List.filter (fun (_, path) -> path.EndsWith(".fs", StringComparison.OrdinalIgnoreCase))
        |> List.tryLast
        |> Option.map fst

    let private hintList units =
        units |> List.collect _.Sources |> List.map _.HintName |> String.concat ", "

    let private normalizedHint hintName =
        GeneratedPaths.stripKnownExtension hintName |> _.ToUpperInvariant()

    let buildUnits
        (originalImplementationHints: Set<string>)
        (generatedSources: FSharpGeneratedSource list)
        (pending: PendingGeneratedSource list)
        =
        let signatures: FSharpGeneratedSource list =
            generatedSources |> List.filter (fun source -> source.Kind = Signature)

        let implementations: FSharpGeneratedSource list =
            generatedSources |> List.filter (fun source -> source.Kind = Implementation)

        let diagnostics = ResizeArray<FSharpGeneratorDiagnostic>()

        let implementationByGeneratorAndHint =
            implementations
            |> Seq.map (fun source -> (source.GeneratorName, source.HintName), source)
            |> dict

        let isOriginalImplementationHint hintName =
            originalImplementationHints.Contains(normalizedHint hintName)

        for pendingSignature in pending |> List.filter (fun source -> source.Kind = Signature) do
            match pendingSignature.CompanionImplementationHintName with
            | None ->
                diagnostics.Add(
                    Diagnostics.error
                        "FSG0008"
                        (sprintf
                            "Generated signature '%s' does not specify a generated implementation companion."
                            pendingSignature.HintName)
                )
            | Some companionHint ->
                if
                    not (implementationByGeneratorAndHint.ContainsKey((pendingSignature.GeneratorName, companionHint)))
                then
                    if
                        isOriginalImplementationHint companionHint
                        || isOriginalImplementationHint pendingSignature.HintName
                    then
                        diagnostics.Add(
                            Diagnostics.error
                                "FSG0014"
                                (sprintf
                                    "Generated signature '%s' targets user implementation '%s'. Generated signatures for user-authored implementations are not supported in V1."
                                    pendingSignature.HintName
                                    companionHint)
                        )
                    else
                        diagnostics.Add(
                            Diagnostics.error
                                "FSG0008"
                                (sprintf
                                    "Generated signature '%s' references missing implementation companion '%s'."
                                    pendingSignature.HintName
                                    companionHint)
                        )

        let duplicateSignatureCompanions =
            pending
            |> List.choose (fun source ->
                match source.Kind, source.CompanionImplementationHintName with
                | Signature, Some companion -> Some((source.GeneratorName, companion), source.HintName)
                | _ -> None)
            |> Seq.groupBy fst
            |> Seq.choose (fun ((generatorName, companion), signatures) ->
                let signatureHintNames = signatures |> Seq.map snd |> Seq.toList

                if signatureHintNames.Length > 1 then
                    Some(generatorName, companion, signatureHintNames)
                else
                    None)

        for (generatorName, companion, signatureHintNames) in duplicateSignatureCompanions do
            diagnostics.Add(
                Diagnostics.error
                    "FSG0008"
                    (sprintf
                        "Generator '%s' emitted multiple generated signatures for implementation companion '%s': %s."
                        generatorName
                        companion
                        (String.concat ", " signatureHintNames))
            )

        let signatureCompanionHints =
            pending
            |> List.choose (fun source ->
                match source.Kind, source.CompanionImplementationHintName with
                | Signature, Some companion -> Some(source.GeneratorName, companion)
                | _ -> None)
            |> Set.ofList

        let units =
            implementations
            |> List.map (fun implementation ->
                if signatureCompanionHints.Contains(implementation.GeneratorName, implementation.HintName) then
                    let companionSignatures =
                        signatures
                        |> List.filter (fun signature ->
                            pending
                            |> List.exists (fun pendingSource ->
                                pendingSource.GeneratorName = signature.GeneratorName
                                && pendingSource.HintName = signature.HintName
                                && pendingSource.CompanionImplementationHintName = Some implementation.HintName))

                    { Placement = implementation.Placement
                      Paths = (companionSignatures @ [ implementation ]) |> List.map _.ResolvedPath
                      Sources = companionSignatures @ [ implementation ] }
                else
                    { Placement = implementation.Placement
                      Paths = [ implementation.ResolvedPath ]
                      Sources = [ implementation ] })

        units, diagnostics |> Seq.toList

    let resolve outputKind originalSourceFiles generatedPaths units =
        let diagnostics = ResizeArray<FSharpGeneratorDiagnostic>()
        let original = originalSourceFiles |> List.map Path.GetFullPath
        let generatedPathSet = generatedPaths |> Seq.map Path.GetFullPath |> Set.ofSeq

        let prelude, rest = units |> List.partition (fun unit -> unit.Placement = Prelude)

        let beforeLast, rest =
            rest |> List.partition (fun unit -> unit.Placement = BeforeLastSourceFile)

        let endOfProject, anchored =
            rest |> List.partition (fun unit -> unit.Placement = EndOfProject)

        let mutable ordered = (prelude |> List.collect _.Paths) @ original

        match finalOriginalImplementationIndex original with
        | Some finalOriginalIndex ->
            let mutable adjustedIndex =
                finalOriginalIndex + (prelude |> List.sumBy (fun unit -> unit.Paths.Length))

            for unit in beforeLast do
                ordered <- insertAt adjustedIndex unit.Paths ordered
                adjustedIndex <- adjustedIndex + unit.Paths.Length
        | None ->
            diagnostics.Add(
                Diagnostics.error
                    "FSG0007"
                    (sprintf
                        "BeforeLastSourceFile placement for generated source '%s' could not be resolved because the project has no implementation source file."
                        (hintList beforeLast))
            )

        match outputKind, endOfProject with
        | Application, _ :: _ ->
            diagnostics.Add(
                Diagnostics.error
                    "FSG0012"
                    "EndOfProject placement is invalid for applications because it can break F# final-file rules."
            )
        | _ -> ordered <- ordered @ (endOfProject |> List.collect _.Paths)

        let mutable remaining = anchored
        let mutable progressed = true
        let afterFileOffsets = Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)

        while progressed && not remaining.IsEmpty do
            progressed <- false

            let nextRemaining = ResizeArray<Unit>()

            for unit in remaining do
                match unit.Placement with
                | BeforeFile anchorPath ->
                    match findPathIndex anchorPath ordered with
                    | Some index ->
                        ordered <- insertAt index unit.Paths ordered
                        progressed <- true
                    | None -> nextRemaining.Add unit
                | AfterFile anchorPath ->
                    match findPathIndex anchorPath ordered with
                    | Some index ->
                        let anchorKey = Path.GetFullPath(anchorPath)

                        let offset =
                            match afterFileOffsets.TryGetValue anchorKey with
                            | true, value -> value
                            | false, _ -> 0

                        ordered <- insertAt (index + 1 + offset) unit.Paths ordered
                        afterFileOffsets[anchorKey] <- offset + unit.Paths.Length
                        progressed <- true
                    | None -> nextRemaining.Add unit
                | _ -> ()

            remaining <- nextRemaining |> Seq.toList

        for unit in remaining do
            match unit.Placement with
            | BeforeFile anchorPath
            | AfterFile anchorPath ->
                let id =
                    if generatedPathSet.Contains(Path.GetFullPath(anchorPath)) then
                        "FSG0009"
                    else
                        "FSG0007"

                diagnostics.Add(
                    Diagnostics.error
                        id
                        (sprintf
                            "Generated source '%s' anchor '%s' could not be resolved."
                            (hintList [ unit ])
                            anchorPath)
                )
            | _ -> ()

        ordered |> ImmutableArray.CreateRange, diagnostics |> Seq.toList
