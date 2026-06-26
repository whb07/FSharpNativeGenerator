namespace FSharp.Compiler.SourceGeneration

open System
open System.Collections.Immutable
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Threading

module internal CacheIdentityParts =
    let dictionaryParts (values: IReadOnlyDictionary<string, string>) =
        values
        |> Seq.map (fun pair -> pair.Key, pair.Value)
        |> Seq.sortBy fst
        |> Seq.collect (fun (key, value) -> seq { key; value })

    let additionalTextChecksumParts (additionalText: FSharpAdditionalText) (cancellationToken: CancellationToken) =
        seq {
            yield additionalText.Path

            match additionalText.Checksum with
            | Some checksum -> yield Hashing.toHex checksum
            | None ->
                cancellationToken.ThrowIfCancellationRequested()

                match additionalText.GetText cancellationToken with
                | Some sourceText -> yield Hashing.toHex (FSharpSourceText.checksum sourceText)
                | None -> yield "<missing>"
        }

module FSharpProjectCacheIdentity =
    let compute (snapshot: FSharpGeneratorProjectSnapshot) (generatedSources: seq<FSharpGeneratedSource>) =
        seq {
            yield snapshot.ProjectOptions.ProjectFilePath
            yield defaultArg snapshot.ProjectOptions.ProjectId ""
            yield! snapshot.ProjectOptions.SourceFiles
            yield! snapshot.ProjectOptions.OtherOptions
            yield string snapshot.ProjectOptions.OutputKind

            for sourceFile in snapshot.SourceFiles do
                yield sourceFile.Path
                yield string sourceFile.IsSignatureFile
                yield Hashing.toHex sourceFile.Checksum

            for additionalText in snapshot.AdditionalTexts do
                yield! CacheIdentityParts.additionalTextChecksumParts additionalText CancellationToken.None

            yield! CacheIdentityParts.dictionaryParts snapshot.AnalyzerConfigOptions.GlobalOptions

            for sourceFile in snapshot.SourceFiles do
                yield sourceFile.Path
                yield! snapshot.AnalyzerConfigOptions.GetOptionsForPath sourceFile.Path |> CacheIdentityParts.dictionaryParts

            for additionalText in snapshot.AdditionalTexts do
                yield additionalText.Path
                yield! snapshot.AnalyzerConfigOptions.GetOptionsForPath additionalText.Path |> CacheIdentityParts.dictionaryParts

            for generatedSource in generatedSources do
                yield generatedSource.ResolvedPath
                yield Hashing.toHex generatedSource.Checksum
                yield string generatedSource.Placement
        }
        |> Hashing.sha256Many

module internal FSharpGeneratorDriverIdentity =
    let private assemblyContentHash location =
        if String.IsNullOrWhiteSpace location || not (File.Exists location) then
            "<missing>"
        else
            File.ReadAllBytes location
            |> SHA256.HashData
            |> Convert.ToHexString

    let compute (generators: ImmutableArray<IFSharpIncrementalGenerator>) (options: FSharpGeneratorDriverOptions) =
        seq {
            yield string options.EmitGeneratedFiles
            yield defaultArg options.GeneratedFilesOutputPath ""
            yield defaultArg options.ReportPath ""
            yield string options.MaxGenerationPasses
            yield string options.HostKind
            yield options.GeneratedRoot

            for generator in generators do
                let generatorType = generator.GetType()
                let apiVersion =
                    FSharpGeneratorAttributeHelpers.tryGet generatorType
                    |> Option.map _.ApiVersion
                    |> Option.defaultValue 0

                yield generatorType.FullName
                yield string apiVersion
                yield generatorType.Assembly.Location
                yield string generatorType.Assembly.ManifestModule.ModuleVersionId
                yield assemblyContentHash generatorType.Assembly.Location
        }
        |> Hashing.sha256Many

module internal FSharpGeneratorRunCacheKey =
    let compute (generators: ImmutableArray<IFSharpIncrementalGenerator>) (options: FSharpGeneratorDriverOptions) (snapshot: FSharpGeneratorProjectSnapshot) (cancellationToken: CancellationToken) =
        seq {
            yield Hashing.toHex (FSharpGeneratorDriverIdentity.compute generators options)
            yield snapshot.ProjectOptions.ProjectFilePath
            yield defaultArg snapshot.ProjectOptions.ProjectId ""
            yield! snapshot.ProjectOptions.SourceFiles
            yield! snapshot.ProjectOptions.OtherOptions
            yield string snapshot.ProjectOptions.OutputKind
            yield defaultArg snapshot.ProjectOptions.Stamp ""

            for sourceFile in snapshot.SourceFiles do
                yield sourceFile.Path
                yield string sourceFile.IsSignatureFile
                yield Hashing.toHex sourceFile.Checksum

            for additionalText in snapshot.AdditionalTexts do
                yield! CacheIdentityParts.additionalTextChecksumParts additionalText cancellationToken

            yield! CacheIdentityParts.dictionaryParts snapshot.AnalyzerConfigOptions.GlobalOptions

            for sourceFile in snapshot.SourceFiles do
                yield sourceFile.Path
                yield! snapshot.AnalyzerConfigOptions.GetOptionsForPath sourceFile.Path |> CacheIdentityParts.dictionaryParts

            for additionalText in snapshot.AdditionalTexts do
                yield additionalText.Path
                yield! snapshot.AnalyzerConfigOptions.GetOptionsForPath additionalText.Path |> CacheIdentityParts.dictionaryParts
        }
        |> Hashing.sha256Many
