namespace FSharp.Compiler.SourceGeneration

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Reflection
open System.Runtime.Loader

type FSharpGeneratorAssemblyLoadResult =
    {
        Generators: ImmutableArray<IFSharpIncrementalGenerator>
        Diagnostics: ImmutableArray<FSharpGeneratorDiagnostic>
    }

module FSharpGeneratorAssemblyLoader =
    let private error id message =
        FSharpGeneratorDiagnostic.create id message Error

    type private GeneratorAssemblyLoadContext(generatorAssemblyPath: string) =
        inherit AssemblyLoadContext("FSharpGenerator:" + Path.GetFileName(generatorAssemblyPath), isCollectible = false)

        let generatorDirectory = Path.GetDirectoryName(Path.GetFullPath(generatorAssemblyPath))
        let sharedAbstractionsAssembly = typeof<IFSharpIncrementalGenerator>.Assembly
        let generatorAssemblyName = Path.GetFileNameWithoutExtension(generatorAssemblyPath)

        override this.Load(assemblyName: AssemblyName) =
            if AssemblyName.ReferenceMatchesDefinition(assemblyName, sharedAbstractionsAssembly.GetName()) then
                sharedAbstractionsAssembly
            else
                let localCandidate = Path.Combine(generatorDirectory, assemblyName.Name + ".dll")

                if File.Exists localCandidate then
                    this.LoadFromAssemblyPath localCandidate
                else
                    AppDomain.CurrentDomain.GetAssemblies()
                    |> Array.tryFind (fun assembly ->
                        assembly.GetName().Name <> generatorAssemblyName
                        && AssemblyName.ReferenceMatchesDefinition(assemblyName, assembly.GetName()))
                    |> Option.defaultValue null

    let private isPublicConcrete (candidate: Type) =
        (candidate.IsPublic || candidate.IsNestedPublic) && not candidate.IsAbstract

    let private implementsGenerator (candidate: Type) =
        typeof<IFSharpIncrementalGenerator>.IsAssignableFrom(candidate)

    let loadFromPath (path: string) =
        let diagnostics = ResizeArray<FSharpGeneratorDiagnostic>()
        let generators = ResizeArray<IFSharpIncrementalGenerator>()

        try
            let loadContext = GeneratorAssemblyLoadContext(path)
            let assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(path))

            for candidate in assembly.GetTypes() |> Array.filter isPublicConcrete do
                let hasInterface = implementsGenerator candidate

                match FSharpGeneratorAttributeHelpers.tryGet candidate, hasInterface with
                | Some generatorAttribute, true ->
                    if not (FSharpGeneratorAttributeHelpers.isSupportedApiVersion generatorAttribute) then
                        diagnostics.Add(error "FSG0015" (sprintf "Generator type '%s' references unsupported F# source-generation API version %d. Supported version is %d." candidate.FullName generatorAttribute.ApiVersion FSharpGeneratorApiVersion.Current))
                    else
                        match candidate.GetConstructor(Type.EmptyTypes) with
                        | null ->
                            diagnostics.Add(error "FSG0002" (sprintf "Generator type '%s' must have a public parameterless constructor." candidate.FullName))
                        | _ ->
                            try
                                generators.Add(Activator.CreateInstance(candidate) :?> IFSharpIncrementalGenerator)
                            with ex ->
                                diagnostics.Add(error "FSG0001" (sprintf "Generator type '%s' could not be created: %s" candidate.FullName ex.Message))
                | Some _, false
                | None, true ->
                    diagnostics.Add(error "FSG0002" (sprintf "Generator type '%s' must have both FSharpGeneratorAttribute and IFSharpIncrementalGenerator." candidate.FullName))
                | None, false ->
                    ()
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

    let additionalTextsWithDiagnostics (configuration: FSharpSourceGeneratorConfiguration) =
        let diagnostics = ResizeArray<FSharpGeneratorDiagnostic>()

        let additionalTexts =
            configuration.AdditionalFilePaths
            |> Seq.map (fun path ->
                if not (File.Exists path) then
                    diagnostics.Add({ error "FSG0011" (sprintf "Additional file '%s' does not exist." path) with FilePath = Some path })

                FSharpAdditionalText.fromFile path)
            |> ImmutableArray.CreateRange

        {
            AdditionalTexts = additionalTexts
            Diagnostics = ImmutableArray.CreateRange diagnostics
        }

    let additionalTexts (configuration: FSharpSourceGeneratorConfiguration) =
        (additionalTextsWithDiagnostics configuration).AdditionalTexts

    let private emptyAnalyzerConfigOptions =
        {
            GlobalOptions = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) :> IReadOnlyDictionary<string, string>
            GetOptionsForPath = fun _ -> Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) :> IReadOnlyDictionary<string, string>
        }

    let private trimSection (line: string) =
        line.Substring(1, line.Length - 2).Trim()

    let private tryKeyValue (line: string) =
        let index = line.IndexOf('=')

        if index < 0 then
            None
        else
            let key = line.Substring(0, index).Trim()
            let value = line.Substring(index + 1).Trim()

            if String.IsNullOrWhiteSpace key then
                None
            else
                Some(key, value)

    let private sectionMatchesPath (section: string) (path: string) =
        let fileName = Path.GetFileName(path)
        let normalizedPath = Path.GetFullPath(path)
        let normalizedSection = section.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)

        section = "*"
        || (section.StartsWith("*.", StringComparison.Ordinal) && fileName.EndsWith(section.Substring(1), StringComparison.OrdinalIgnoreCase))
        || fileName.Equals(section, StringComparison.OrdinalIgnoreCase)
        || normalizedPath.EndsWith(normalizedSection, StringComparison.OrdinalIgnoreCase)

    let analyzerConfigOptions (configuration: FSharpSourceGeneratorConfiguration) =
        if configuration.AnalyzerConfigPaths.IsDefaultOrEmpty then
            {
                Options = emptyAnalyzerConfigOptions
                Diagnostics = ImmutableArray<FSharpGeneratorDiagnostic>.Empty
            }
        else
            let diagnostics = ResizeArray<FSharpGeneratorDiagnostic>()
            let globalOptions = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            let sectionOptions = ResizeArray<string * Dictionary<string, string>>()

            for configPath in configuration.AnalyzerConfigPaths do
                if not (File.Exists configPath) then
                    diagnostics.Add({ error "FSG0011" (sprintf "Analyzer config file '%s' does not exist." configPath) with FilePath = Some configPath })
                else
                    let mutable currentSection: string option = None

                    for rawLine in File.ReadAllLines configPath do
                        let line = rawLine.Trim()

                        if String.IsNullOrWhiteSpace line || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal) then
                            ()
                        elif line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal) then
                            currentSection <- Some(trimSection line)
                        else
                            match tryKeyValue line with
                            | None ->
                                diagnostics.Add({ error "FSG0011" (sprintf "Invalid analyzer config line '%s'." rawLine) with FilePath = Some configPath })
                            | Some(key, value) ->
                                match currentSection with
                                | None -> globalOptions[key] <- value
                                | Some section ->
                                    let existing =
                                        sectionOptions
                                        |> Seq.tryFind (fun (candidate, _) -> candidate.Equals(section, StringComparison.OrdinalIgnoreCase))

                                    let values =
                                        match existing with
                                        | Some(_, options) -> options
                                        | None ->
                                            let options = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                                            sectionOptions.Add(section, options)
                                            options

                                    values[key] <- value

            let optionsForPath path =
                let merged = Dictionary<string, string>(globalOptions, StringComparer.OrdinalIgnoreCase)

                for section, options in sectionOptions do
                    if sectionMatchesPath section path then
                        for pair in options do
                            merged[pair.Key] <- pair.Value

                merged :> IReadOnlyDictionary<string, string>

            {
                Options =
                    {
                        GlobalOptions = globalOptions :> IReadOnlyDictionary<string, string>
                        GetOptionsForPath = optionsForPath
                    }
                Diagnostics = ImmutableArray.CreateRange diagnostics
            }

    let loadGenerators (configuration: FSharpSourceGeneratorConfiguration) =
        let loadResults =
            configuration.GeneratorPaths
            |> Seq.map FSharpGeneratorAssemblyLoader.loadFromPath
            |> Seq.toList

        {
            Generators = loadResults |> Seq.collect _.Generators |> ImmutableArray.CreateRange
            Diagnostics = loadResults |> Seq.collect _.Diagnostics |> ImmutableArray.CreateRange
        }

    let generatorPathsFromNuGetPackage packageRoot =
        let analyzerRoot = Path.Combine(Path.GetFullPath(packageRoot), "analyzers", "dotnet", "fs")

        if Directory.Exists analyzerRoot then
            Directory.EnumerateFiles(analyzerRoot, "*.dll", SearchOption.AllDirectories)
            |> Seq.sort
            |> ImmutableArray.CreateRange
        else
            ImmutableArray<string>.Empty

    let loadGeneratorsFromNuGetPackage packageRoot =
        let generatorPaths = generatorPathsFromNuGetPackage packageRoot

        if generatorPaths.IsDefaultOrEmpty then
            {
                Generators = ImmutableArray<IFSharpIncrementalGenerator>.Empty
                Diagnostics =
                    ImmutableArray.Create(
                        { error "FSG0001" (sprintf "NuGet analyzer folder '%s' does not contain F# generator assemblies." (Path.Combine(Path.GetFullPath(packageRoot), "analyzers", "dotnet", "fs"))) with
                            FilePath = Some(Path.GetFullPath(packageRoot)) })
            }
        else
            loadGenerators
                {
                    GeneratorPaths = generatorPaths
                    AdditionalFilePaths = ImmutableArray<string>.Empty
                    AnalyzerConfigPaths = ImmutableArray<string>.Empty
                    DriverOptions = FSharpGeneratorDriverOptions.defaults
                }
