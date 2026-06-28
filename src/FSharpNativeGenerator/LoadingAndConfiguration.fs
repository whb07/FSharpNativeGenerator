namespace FSharp.Compiler.SourceGeneration

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Runtime.Loader
open System.Security.Cryptography
open System.Text
open FSharp.Compiler.Diagnostics

module internal FSharpSourceGeneratorDiagnostics =
    let error id message =
        { Id = id
          Message = message
          Severity = FSharpDiagnosticSeverity.Error
          Range = None }

type LoadedFSharpGenerator =
    { Generator: IFSharpIncrementalGenerator
      GeneratorId: string
      AssemblyPath: string
      TypeName: string }

type FSharpGeneratorAssemblyLoadResult =
    { Generators: LoadedFSharpGenerator list
      Diagnostics: FSharpSourceGeneratorDiagnostic list }

module FSharpGeneratorAssemblyLoader =
    let private generatorId (assembly: Assembly) (typ: Type) =
        sprintf "%s/%s" (assembly.GetName().Name) typ.FullName

    let private loadTypes (assembly: Assembly) =
        try
            assembly.GetTypes(), []
        with
        | :? ReflectionTypeLoadException as ex ->
            let types = ex.Types |> Array.choose (fun typ -> if isNull typ then None else Some typ)
            let diagnostics =
                ex.LoaderExceptions
                |> Array.choose (fun loaderException -> if isNull loaderException then None else Some loaderException)
                |> Array.map (fun loaderException ->
                    FSharpSourceGeneratorDiagnostics.error "FSG0001" (sprintf "Generator assembly type load failed: %s" loaderException.Message))
                |> Array.toList

            types, diagnostics

    let loadFromPath (path: string) : FSharpGeneratorAssemblyLoadResult =
        try
            let fullPath = Path.GetFullPath path
            let resolver = AssemblyDependencyResolver fullPath
            let loadContext = AssemblyLoadContext("FSharpGenerator:" + fullPath, false)

            let resolving =
                Func<AssemblyLoadContext, AssemblyName, Assembly>(fun context assemblyName ->
                    let resolved = resolver.ResolveAssemblyToPath assemblyName

                    if isNull resolved then
                        null
                    else
                        context.LoadFromAssemblyPath resolved)

            loadContext.add_Resolving resolving

            let assembly = loadContext.LoadFromAssemblyPath fullPath
            let types, loadDiagnostics = loadTypes assembly
            let generators = ResizeArray<LoadedFSharpGenerator>()
            let diagnostics = ResizeArray<FSharpSourceGeneratorDiagnostic>(loadDiagnostics)

            for typ in types do
                if typ.IsClass && typ.IsPublic && not typ.IsAbstract && not typ.ContainsGenericParameters then
                    let attribute = FSharpGeneratorAttributeHelpers.tryGet typ
                    let implementsIncremental = typeof<IFSharpIncrementalGenerator>.IsAssignableFrom typ

                    match attribute, implementsIncremental with
                    | Some attr, true when not (FSharpGeneratorAttributeHelpers.isSupportedApiVersion attr) ->
                        diagnostics.Add(
                            FSharpSourceGeneratorDiagnostics.error
                                "FSG0015"
                                (sprintf "Generator type '%s' references unsupported source-generation API version %d." typ.FullName attr.ApiVersion)
                        )
                    | Some _, true ->
                        match typ.GetConstructor(Type.EmptyTypes) with
                        | null ->
                            diagnostics.Add(
                                FSharpSourceGeneratorDiagnostics.error
                                    "FSG0002"
                                    (sprintf "Generator type '%s' must have a public parameterless constructor." typ.FullName)
                            )
                        | ctor ->
                            try
                                let instance = ctor.Invoke [||] :?> IFSharpIncrementalGenerator
                                let id =
                                    match instance with
                                    | :? IFSharpIncrementalGeneratorWithId as stable -> stable.GeneratorId
                                    | _ -> generatorId assembly typ

                                generators.Add
                                    { Generator = instance
                                      GeneratorId = id
                                      AssemblyPath = fullPath
                                      TypeName = typ.FullName }
                            with ex ->
                                let message =
                                    match ex with
                                    | :? TargetInvocationException as invocation when not (isNull invocation.InnerException) -> invocation.InnerException.Message
                                    | _ -> ex.Message

                                diagnostics.Add(
                                    FSharpSourceGeneratorDiagnostics.error
                                        "FSG0002"
                                        (sprintf "Generator type '%s' could not be constructed: %s" typ.FullName message)
                                )
                    | Some _, false
                    | None, true ->
                        diagnostics.Add(
                            FSharpSourceGeneratorDiagnostics.error
                                "FSG0002"
                                (sprintf "Generator type '%s' is missing FSharpGeneratorAttribute or IFSharpIncrementalGenerator." typ.FullName)
                        )
                    | None, false -> ()

            { Generators = Seq.toList generators
              Diagnostics = Seq.toList diagnostics }
        with ex ->
            { Generators = []
              Diagnostics = [ FSharpSourceGeneratorDiagnostics.error "FSG0001" (sprintf "Generator assembly load failed for '%s': %s" path ex.Message) ] }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module FSharpSourceGeneratorConfiguration =
    let empty =
        { GeneratorPaths = []
          AdditionalFilePaths = []
          AnalyzerConfigPaths = [] }

    let private tryTakeValue prefix (arg: string) =
        if arg.StartsWith(prefix, StringComparison.Ordinal) then
            Some(arg.Substring(prefix.Length))
        else
            None

    let parseCommandLineLikeArguments (args: string list) : FSharpSourceGeneratorConfiguration * string list * FSharpSourceGeneratorDiagnostic list =
        let generators = ResizeArray<string>()
        let additionalFiles = ResizeArray<string>()
        let analyzerConfigs = ResizeArray<string>()
        let remaining = ResizeArray<string>()

        for arg in args do
            match tryTakeValue "--fsharp-source-generator:" arg with
            | Some path -> generators.Add path
            | None ->
                match tryTakeValue "--fsharp-generator-additional-file:" arg with
                | Some path -> additionalFiles.Add path
                | None ->
                    match tryTakeValue "--fsharp-source-generator-analyzer-config:" arg with
                    | Some path -> analyzerConfigs.Add path
                    | None -> remaining.Add arg

        { GeneratorPaths = Seq.toList generators
          AdditionalFilePaths = Seq.toList additionalFiles
          AnalyzerConfigPaths = Seq.toList analyzerConfigs },
        Seq.toList remaining,
        []

module internal FSharpAnalyzerConfigSupport =
    let emptyOptions =
        { GlobalOptions = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) :> IReadOnlyDictionary<string, string>
          GetOptionsForPath = fun _ -> Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) :> IReadOnlyDictionary<string, string> }

    type private ParsedAnalyzerConfig =
        { GlobalOptions: Dictionary<string, string>
          Sections: (string * Dictionary<string, string>) list }

    let private normalizePath (path: string) =
        if String.IsNullOrWhiteSpace path then
            path
        else
            try Path.GetFullPath path with _ -> path

    let private trimSection (line: string) =
        line.Trim().TrimStart('[').TrimEnd(']').Trim()

    let private trySplitKeyValue (line: string) =
        let index = line.IndexOf('=')

        if index < 0 then
            None
        else
            let key = line.Substring(0, index).Trim()
            let value = line.Substring(index + 1).Trim()

            if String.IsNullOrWhiteSpace key then None else Some(key, value)

    let private wildcardMatches (pattern: string) (value: string) =
        let comparison = StringComparison.OrdinalIgnoreCase
        let normalizedPattern = pattern.Replace('\\', '/').Trim()
        let normalizedValue = value.Replace('\\', '/')

        if normalizedPattern = "*" then
            true
        elif not (normalizedPattern.Contains("*", StringComparison.Ordinal)) then
            normalizedValue.EndsWith(normalizedPattern, comparison)
            || Path.GetFileName(normalizedValue).Equals(normalizedPattern, comparison)
        else
            let parts = normalizedPattern.Split([| '*' |], StringSplitOptions.RemoveEmptyEntries)
            let mutable index = 0
            let mutable matched = true

            for part in parts do
                if matched then
                    let next = normalizedValue.IndexOf(part, index, comparison)

                    if next < 0 then
                        matched <- false
                    else
                        index <- next + part.Length

            matched

    let private parseFile path =
        let globalOptions = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        let sections = ResizeArray<string * Dictionary<string, string>>()
        let mutable current: Dictionary<string, string> option = None
        let mutable currentPattern = ""

        if File.Exists path then
            for rawLine in File.ReadAllLines path do
                let line = rawLine.Trim()

                if
                    not (String.IsNullOrWhiteSpace line)
                    && not (line.StartsWith("#", StringComparison.Ordinal))
                    && not (line.StartsWith(";", StringComparison.Ordinal))
                then
                    if line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal) then
                        let section = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        currentPattern <- trimSection line
                        sections.Add(currentPattern, section)
                        current <- Some section
                    else
                        match trySplitKeyValue line with
                        | Some(key, value) ->
                            match current with
                            | Some section -> section[key] <- value
                            | None -> globalOptions[key] <- value
                        | None -> ()

        { GlobalOptions = globalOptions
          Sections = Seq.toList sections }

    let parseFiles paths =
        let parsed = paths |> List.map parseFile
        let globalOptions = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        let sections = ResizeArray<string * Dictionary<string, string>>()

        for file in parsed do
            for kvp in file.GlobalOptions do
                globalOptions[kvp.Key] <- kvp.Value

            for section in file.Sections do
                sections.Add section

        { GlobalOptions = globalOptions :> IReadOnlyDictionary<string, string>
          GetOptionsForPath =
            fun path ->
                let fullPath = normalizePath path
                let options = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

                for pattern, section in sections do
                    if wildcardMatches pattern fullPath then
                        for kvp in section do
                            options[kvp.Key] <- kvp.Value

                options :> IReadOnlyDictionary<string, string> }

    let private registry = ConcurrentDictionary<string, FSharpAnalyzerConfigOptions>(StringComparer.OrdinalIgnoreCase)

    let registerForProjectDirectory projectDirectory analyzerConfigPaths =
        let key = normalizePath projectDirectory
        registry[key] <- parseFiles analyzerConfigPaths

    let getForProjectDirectory projectDirectory =
        let key = normalizePath projectDirectory

        match registry.TryGetValue key with
        | true, options -> options
        | _ -> emptyOptions

    let contentIdentityPath analyzerConfigPaths =
        use sha = SHA256.Create()
        let add (value: string) =
            let bytes = Encoding.UTF8.GetBytes value
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0) |> ignore

        for path in analyzerConfigPaths |> List.map normalizePath |> List.sortWith (fun left right -> StringComparer.OrdinalIgnoreCase.Compare(left, right)) do
            add path
            add "\n"

            if File.Exists path then
                add (File.ReadAllText path)

            add "\n"

        sha.TransformFinalBlock(Array.empty, 0, 0) |> ignore
        let hash = sha.Hash |> Array.map (fun b -> b.ToString("x2")) |> String.concat ""
        "__fsharp_generator_analyzer_config_content_" + hash + ".identity"
