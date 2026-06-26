namespace FSharp.Compiler.SourceGeneration

open System
open System.Collections.Immutable
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Threading

module internal CacheIdentityParts =
    let private fileContentHash location =
        if String.IsNullOrWhiteSpace location || not (File.Exists location) then
            "<missing>"
        else
            File.ReadAllBytes location |> SHA256.HashData |> Convert.ToHexString

    let private projectDirectory (projectOptions: FSharpProjectOptions) =
        let projectPath = Path.GetFullPath projectOptions.ProjectFilePath
        let directory = Path.GetDirectoryName projectPath

        if String.IsNullOrWhiteSpace directory then
            Environment.CurrentDirectory
        else
            directory

    let private normalizeProjectPath (projectOptions: FSharpProjectOptions) (path: string) =
        let trimmedPath = path.Trim().Trim('"')

        if Path.IsPathRooted trimmedPath then
            Path.GetFullPath trimmedPath
        else
            Path.GetFullPath(Path.Combine(projectDirectory projectOptions, trimmedPath))

    let private tryPrefixedValue (prefix: string) (value: string) =
        if value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
            let candidate = value.Substring(prefix.Length)

            if String.IsNullOrWhiteSpace candidate then
                None
            else
                Some candidate
        else
            None

    let private referencePaths (projectOptions: FSharpProjectOptions) =
        let isSplitReferenceOption option =
            String.Equals(option, "--reference", StringComparison.OrdinalIgnoreCase)
            || String.Equals(option, "-r", StringComparison.OrdinalIgnoreCase)
            || String.Equals(option, "/reference", StringComparison.OrdinalIgnoreCase)
            || String.Equals(option, "/r", StringComparison.OrdinalIgnoreCase)

        let rec loop remainingOptions paths =
            match remainingOptions with
            | [] -> List.rev paths
            | option :: value :: tail when isSplitReferenceOption option ->
                loop tail (normalizeProjectPath projectOptions value :: paths)
            | option :: tail ->
                let referencePath =
                    [ "--reference:"
                      "--reference="
                      "-r:"
                      "-r="
                      "/reference:"
                      "/reference="
                      "/r:"
                      "/r=" ]
                    |> List.tryPick (fun prefix -> tryPrefixedValue prefix option)

                match referencePath with
                | Some path -> loop tail (normalizeProjectPath projectOptions path :: paths)
                | None -> loop tail paths

        loop (projectOptions.OtherOptions |> Seq.toList) []

    let dictionaryParts (values: IReadOnlyDictionary<string, string>) =
        values
        |> Seq.map (fun pair -> pair.Key, pair.Value)
        |> Seq.sortBy fst
        |> Seq.collect (fun (key, value) ->
            seq {
                key
                value
            })

    let additionalTextChecksumParts (additionalText: FSharpAdditionalText) (cancellationToken: CancellationToken) =
        seq {
            yield additionalText.Path

            match additionalText.Checksum with
            | Some checksum -> yield Hashing.toHex checksum
            | None ->
                cancellationToken.ThrowIfCancellationRequested()

                match additionalText.GetText cancellationToken with
                | Some sourceText when not (obj.ReferenceEquals(sourceText, null)) ->
                    yield Hashing.toHex (FSharpSourceText.checksum sourceText)
                | Some _ -> yield "<null>"
                | None -> yield "<missing>"
        }

    let compilerReferenceParts (projectOptions: FSharpProjectOptions) =
        seq {
            let fsharpCoreAssembly = typeof<unit>.Assembly
            yield "fsharp-core"
            yield fsharpCoreAssembly.Location
            yield string fsharpCoreAssembly.ManifestModule.ModuleVersionId
            yield fileContentHash fsharpCoreAssembly.Location

            yield "references"

            for referencePath in referencePaths projectOptions do
                yield referencePath
                yield fileContentHash referencePath
        }

module FSharpProjectCacheIdentity =
    let compute (snapshot: FSharpGeneratorProjectSnapshot) (generatedSources: seq<FSharpGeneratedSource>) =
        seq {
            yield snapshot.ProjectOptions.ProjectFilePath
            yield defaultArg snapshot.ProjectOptions.ProjectId ""
            yield! snapshot.ProjectOptions.SourceFiles
            yield! snapshot.ProjectOptions.OtherOptions
            yield string snapshot.ProjectOptions.OutputKind
            yield! CacheIdentityParts.compilerReferenceParts snapshot.ProjectOptions

            for sourceFile in snapshot.SourceFiles do
                yield sourceFile.Path
                yield string sourceFile.IsSignatureFile
                yield Hashing.toHex sourceFile.Checksum

            for additionalText in snapshot.AdditionalTexts do
                yield! CacheIdentityParts.additionalTextChecksumParts additionalText CancellationToken.None

            yield! CacheIdentityParts.dictionaryParts snapshot.AnalyzerConfigOptions.GlobalOptions

            for sourceFile in snapshot.SourceFiles do
                yield sourceFile.Path

                yield!
                    snapshot.AnalyzerConfigOptions.GetOptionsForPath sourceFile.Path
                    |> CacheIdentityParts.dictionaryParts

            for additionalText in snapshot.AdditionalTexts do
                yield additionalText.Path

                yield!
                    snapshot.AnalyzerConfigOptions.GetOptionsForPath additionalText.Path
                    |> CacheIdentityParts.dictionaryParts

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
            File.ReadAllBytes location |> SHA256.HashData |> Convert.ToHexString

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
    let compute
        (generators: ImmutableArray<IFSharpIncrementalGenerator>)
        (options: FSharpGeneratorDriverOptions)
        (snapshot: FSharpGeneratorProjectSnapshot)
        (cancellationToken: CancellationToken)
        =
        seq {
            yield Hashing.toHex (FSharpGeneratorDriverIdentity.compute generators options)
            yield snapshot.ProjectOptions.ProjectFilePath
            yield defaultArg snapshot.ProjectOptions.ProjectId ""
            yield! snapshot.ProjectOptions.SourceFiles
            yield! snapshot.ProjectOptions.OtherOptions
            yield string snapshot.ProjectOptions.OutputKind
            yield defaultArg snapshot.ProjectOptions.Stamp ""
            yield! CacheIdentityParts.compilerReferenceParts snapshot.ProjectOptions

            for sourceFile in snapshot.SourceFiles do
                yield sourceFile.Path
                yield string sourceFile.IsSignatureFile
                yield Hashing.toHex sourceFile.Checksum

            for additionalText in snapshot.AdditionalTexts do
                yield! CacheIdentityParts.additionalTextChecksumParts additionalText cancellationToken

            yield! CacheIdentityParts.dictionaryParts snapshot.AnalyzerConfigOptions.GlobalOptions

            for sourceFile in snapshot.SourceFiles do
                yield sourceFile.Path

                yield!
                    snapshot.AnalyzerConfigOptions.GetOptionsForPath sourceFile.Path
                    |> CacheIdentityParts.dictionaryParts

            for additionalText in snapshot.AdditionalTexts do
                yield additionalText.Path

                yield!
                    snapshot.AnalyzerConfigOptions.GetOptionsForPath additionalText.Path
                    |> CacheIdentityParts.dictionaryParts
        }
        |> Hashing.sha256Many
