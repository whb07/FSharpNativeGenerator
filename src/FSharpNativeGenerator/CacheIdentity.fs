namespace FSharp.Compiler.SourceGeneration

open System.Collections.Immutable

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
                yield generatorType.FullName
                yield generatorType.Assembly.Location
                yield string generatorType.Assembly.ManifestModule.ModuleVersionId
        }
        |> Hashing.sha256Many
