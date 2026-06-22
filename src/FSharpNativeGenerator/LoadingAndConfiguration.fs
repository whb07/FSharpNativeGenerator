namespace FSharp.Compiler.SourceGeneration

open System
open System.Collections.Immutable
open System.IO
open System.Reflection

type FSharpGeneratorAssemblyLoadResult =
    {
        Generators: ImmutableArray<IFSharpIncrementalGenerator>
        Diagnostics: ImmutableArray<FSharpGeneratorDiagnostic>
    }

module FSharpGeneratorAssemblyLoader =
    let private error id message =
        FSharpGeneratorDiagnostic.create id message Error

    let private isPublicConcrete (candidate: Type) =
        (candidate.IsPublic || candidate.IsNestedPublic) && not candidate.IsAbstract

    let private hasGeneratorAttribute (candidate: Type) =
        candidate.GetCustomAttributes(typeof<FSharpGeneratorAttribute>, false).Length > 0

    let private implementsGenerator (candidate: Type) =
        typeof<IFSharpIncrementalGenerator>.IsAssignableFrom(candidate)

    let loadFromPath (path: string) =
        let diagnostics = ResizeArray<FSharpGeneratorDiagnostic>()
        let generators = ResizeArray<IFSharpIncrementalGenerator>()

        try
            let assembly = Assembly.LoadFrom(Path.GetFullPath(path))

            for candidate in assembly.GetTypes() |> Array.filter isPublicConcrete do
                let hasAttribute = hasGeneratorAttribute candidate
                let hasInterface = implementsGenerator candidate

                if hasAttribute && hasInterface then
                    match candidate.GetConstructor(Type.EmptyTypes) with
                    | null ->
                        diagnostics.Add(error "FSG0002" (sprintf "Generator type '%s' must have a public parameterless constructor." candidate.FullName))
                    | _ ->
                        try
                            generators.Add(Activator.CreateInstance(candidate) :?> IFSharpIncrementalGenerator)
                        with ex ->
                            diagnostics.Add(error "FSG0001" (sprintf "Generator type '%s' could not be created: %s" candidate.FullName ex.Message))
                elif hasAttribute <> hasInterface then
                    diagnostics.Add(error "FSG0002" (sprintf "Generator type '%s' must have both FSharpGeneratorAttribute and IFSharpIncrementalGenerator." candidate.FullName))
        with ex ->
            diagnostics.Add({ error "FSG0001" (sprintf "Generator assembly load failed: %s" ex.Message) with FilePath = Some path })

        {
            Generators = ImmutableArray.CreateRange generators
            Diagnostics = ImmutableArray.CreateRange diagnostics
        }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module FSharpSourceGeneratorConfiguration =
    let private error id message =
        FSharpGeneratorDiagnostic.create id message Error

    let private normalizePath (path: string) =
        Path.GetFullPath path

    let private afterPrefix (prefix: string) (argument: string) =
        argument.Substring(prefix.Length)

    let private parseBoolSwitch (value: string) =
        match value with
        | "" -> Some true
        | "+" -> Some true
        | "-" -> Some false
        | _ when value.Equals(":true", StringComparison.OrdinalIgnoreCase) -> Some true
        | _ when value.Equals(":false", StringComparison.OrdinalIgnoreCase) -> Some false
        | _ -> None

    let private addMissingValueDiagnostic (diagnostics: ResizeArray<FSharpGeneratorDiagnostic>) argument =
        diagnostics.Add(error "FSG0011" (sprintf "Compiler option '%s' requires a value." argument))

    let parseCommandLineArguments (arguments: seq<string>) =
        let diagnostics = ResizeArray<FSharpGeneratorDiagnostic>()
        let generatorPaths = ResizeArray<string>()
        let additionalFilePaths = ResizeArray<string>()
        let analyzerConfigPaths = ResizeArray<string>()
        let remainingArguments = ResizeArray<string>()
        let mutable emitGeneratedFiles = FSharpGeneratorDriverOptions.defaults.EmitGeneratedFiles
        let mutable generatedFilesOutputPath = FSharpGeneratorDriverOptions.defaults.GeneratedFilesOutputPath
        let mutable reportPath = FSharpGeneratorDriverOptions.defaults.ReportPath

        for argument in arguments do
            if argument.StartsWith("--fsharp-source-generator:", StringComparison.Ordinal) then
                let value = afterPrefix "--fsharp-source-generator:" argument
                if String.IsNullOrWhiteSpace value then addMissingValueDiagnostic diagnostics argument else generatorPaths.Add(normalizePath value)
            elif argument.StartsWith("--fsharp-generator-additional-file:", StringComparison.Ordinal) then
                let value = afterPrefix "--fsharp-generator-additional-file:" argument
                if String.IsNullOrWhiteSpace value then addMissingValueDiagnostic diagnostics argument else additionalFilePaths.Add(normalizePath value)
            elif argument.StartsWith("--fsharp-source-generator-analyzer-config:", StringComparison.Ordinal) then
                let value = afterPrefix "--fsharp-source-generator-analyzer-config:" argument
                if String.IsNullOrWhiteSpace value then addMissingValueDiagnostic diagnostics argument else analyzerConfigPaths.Add(normalizePath value)
            elif argument.StartsWith("--fsharp-generated-files-output:", StringComparison.Ordinal) then
                let value = afterPrefix "--fsharp-generated-files-output:" argument
                if String.IsNullOrWhiteSpace value then addMissingValueDiagnostic diagnostics argument else generatedFilesOutputPath <- Some(normalizePath value)
            elif argument.StartsWith("--fsharp-source-generator-report:", StringComparison.Ordinal) then
                let value = afterPrefix "--fsharp-source-generator-report:" argument
                if String.IsNullOrWhiteSpace value then addMissingValueDiagnostic diagnostics argument else reportPath <- Some(normalizePath value)
            elif argument.StartsWith("--emit-fsharp-generated-files", StringComparison.Ordinal) then
                let value = afterPrefix "--emit-fsharp-generated-files" argument

                match parseBoolSwitch value with
                | Some parsed -> emitGeneratedFiles <- parsed
                | None -> diagnostics.Add(error "FSG0011" (sprintf "Compiler option '%s' has an invalid boolean suffix." argument))
            else
                remainingArguments.Add argument

        let driverOptions =
            {
                FSharpGeneratorDriverOptions.defaults with
                    EmitGeneratedFiles = emitGeneratedFiles
                    GeneratedFilesOutputPath = generatedFilesOutputPath
                    ReportPath = reportPath
                    HostKind = CommandLine
                    GeneratedRoot = defaultArg generatedFilesOutputPath FSharpGeneratorDriverOptions.defaults.GeneratedRoot
            }

        {
            Configuration =
                {
                    GeneratorPaths = ImmutableArray.CreateRange generatorPaths
                    AdditionalFilePaths = ImmutableArray.CreateRange additionalFilePaths
                    AnalyzerConfigPaths = ImmutableArray.CreateRange analyzerConfigPaths
                    DriverOptions = driverOptions
                }
            Diagnostics = ImmutableArray.CreateRange diagnostics
            RemainingArguments = ImmutableArray.CreateRange remainingArguments
        }

    let fromMSBuildItems (generatorItems: seq<FSharpMSBuildSourceGeneratorItem>) (additionalFileItems: seq<FSharpMSBuildAdditionalFileItem>) (properties: FSharpMSBuildSourceGeneratorProperties) =
        let parseBoolOption (value: string option) =
            match value with
            | None -> None
            | Some text when text.Equals("true", StringComparison.OrdinalIgnoreCase) -> Some true
            | Some text when text.Equals("false", StringComparison.OrdinalIgnoreCase) -> Some false
            | Some text when text.Equals("", StringComparison.Ordinal) -> None
            | Some _ -> None

        let diagnostics = ResizeArray<FSharpGeneratorDiagnostic>()

        let emitGeneratedFiles =
            match parseBoolOption properties.EmitFSharpGeneratedFiles with
            | Some value -> value
            | None ->
                if properties.EmitFSharpGeneratedFiles.IsSome then
                    diagnostics.Add(error "FSG0011" "MSBuild property EmitFSharpGeneratedFiles must be 'true' or 'false'.")

                FSharpGeneratorDriverOptions.defaults.EmitGeneratedFiles

        let outputPath = properties.FSharpGeneratedFilesOutputPath |> Option.map normalizePath
        let reportPath = properties.FSharpSourceGeneratorReportPath |> Option.map normalizePath

        {
            Configuration =
                {
                    GeneratorPaths = generatorItems |> Seq.map _.Include |> Seq.map normalizePath |> ImmutableArray.CreateRange
                    AdditionalFilePaths = additionalFileItems |> Seq.map _.Include |> Seq.map normalizePath |> ImmutableArray.CreateRange
                    AnalyzerConfigPaths = ImmutableArray<string>.Empty
                    DriverOptions =
                        {
                            FSharpGeneratorDriverOptions.defaults with
                                EmitGeneratedFiles = emitGeneratedFiles
                                GeneratedFilesOutputPath = outputPath
                                ReportPath = reportPath
                                HostKind = MSBuild
                                GeneratedRoot = defaultArg outputPath FSharpGeneratorDriverOptions.defaults.GeneratedRoot
                        }
                }
            Diagnostics = ImmutableArray.CreateRange diagnostics
            RemainingArguments = ImmutableArray<string>.Empty
        }

    let additionalTexts (configuration: FSharpSourceGeneratorConfiguration) =
        configuration.AdditionalFilePaths
        |> Seq.map FSharpAdditionalText.fromFile
        |> ImmutableArray.CreateRange

    let loadGenerators (configuration: FSharpSourceGeneratorConfiguration) =
        let loadResults =
            configuration.GeneratorPaths
            |> Seq.map FSharpGeneratorAssemblyLoader.loadFromPath
            |> Seq.toList

        {
            Generators = loadResults |> Seq.collect _.Generators |> ImmutableArray.CreateRange
            Diagnostics = loadResults |> Seq.collect _.Diagnostics |> ImmutableArray.CreateRange
        }
