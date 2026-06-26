namespace FSharp.Compiler.SourceGeneration

open System.Collections.Immutable
open System.Collections.Generic

module FSharpProjectCacheIdentity =
    let compute (snapshot: FSharpGeneratorProjectSnapshot) (generatedSources: seq<FSharpGeneratedSource>) =
        seq {
            yield snapshot.ProjectOptions.ProjectFilePath
            yield! snapshot.ProjectOptions.SourceFiles
            yield! snapshot.ProjectOptions.OtherOptions
            yield string snapshot.ProjectOptions.OutputKind

            for sourceFile in snapshot.SourceFiles do
                yield sourceFile.Path
                yield Hashing.toHex sourceFile.Checksum

            for generatedSource in generatedSources do
                yield generatedSource.ResolvedPath
                yield Hashing.toHex generatedSource.Checksum
                yield string generatedSource.Placement
        }
        |> Hashing.sha256Many

module internal FSharpGeneratorDriverIdentity =
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
        }
        |> Hashing.sha256Many

module internal FSharpGeneratorRunCacheKey =
    let private dictionaryParts (values: IReadOnlyDictionary<string, string>) =
        values
        |> Seq.map (fun pair -> pair.Key, pair.Value)
        |> Seq.sortBy fst
        |> Seq.collect (fun (key, value) -> seq { key; value })

    let compute (generators: ImmutableArray<IFSharpIncrementalGenerator>) (options: FSharpGeneratorDriverOptions) (snapshot: FSharpGeneratorProjectSnapshot) =
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
                yield additionalText.Path

                match additionalText.Checksum with
                | Some checksum -> yield Hashing.toHex checksum
                | None -> yield "<missing>"

            yield! dictionaryParts snapshot.AnalyzerConfigOptions.GlobalOptions

            for sourceFile in snapshot.SourceFiles do
                yield sourceFile.Path
                yield! snapshot.AnalyzerConfigOptions.GetOptionsForPath sourceFile.Path |> dictionaryParts
        }
        |> Hashing.sha256Many
