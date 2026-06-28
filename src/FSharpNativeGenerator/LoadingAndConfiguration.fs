namespace FSharp.Compiler.SourceGeneration

open System
open System.IO
open System.Reflection
open System.Runtime.Loader
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
