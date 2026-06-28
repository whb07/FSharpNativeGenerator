namespace FSharpNativeGenerator.TestGenerators

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open FSharp.Compiler.SourceGeneration
open FSharp.Compiler.Diagnostics

module private ScenarioHelpers =
    let tryMetadata key (file: FSharpAdditionalFileInput) =
        FSharpAdditionalFileInput.tryGetMetadata key file

    let namespaceFor defaultNamespace file =
        tryMetadata "FSharpGeneratorNamespace" file
        |> Option.defaultValue defaultNamespace
        |> FSharpGeneratedNames.sanitizeModuleName

    let typeNameFrom key fallback file =
        tryMetadata key file
        |> Option.orElse file.LogicalName
        |> Option.defaultValue fallback
        |> FSharpGeneratedNames.sanitizeIdentifier
        |> fun value -> value.Substring(0, 1).ToUpperInvariant() + value.Substring(1)

    let camelOrSnakeToPascal (value: string) =
        value.Split([| '_'; '-'; ' ' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.collect (fun part ->
            if part.Length = 0 then
                [||]
            else
                [| part.Substring(0, 1).ToUpperInvariant() + part.Substring(1) |])
        |> String.concat ""
        |> FSharpGeneratedNames.sanitizeIdentifier

    let diagnostic id message path =
        { Id = id
          Message = message
          Severity = FSharpDiagnosticSeverity.Error
          Range = None
          FilePath = Some path }

    let hasKindOrExtension kind extensions (file: FSharpAdditionalFileInput) =
        let kindMatches =
            match file.Kind with
            | Some value -> value.Equals(kind, StringComparison.OrdinalIgnoreCase)
            | None -> false

        kindMatches
        || extensions
           |> List.exists (fun extension -> file.Path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))

    let optionDefault fallback value =
        match value with
        | Some v -> v
        | None -> fallback

    let splitCsv (value: string) =
        value.Split([| ',' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map _.Trim()
        |> Array.filter (String.IsNullOrWhiteSpace >> not)
        |> Array.toList

    let lowerFirst (value: string) =
        if String.IsNullOrWhiteSpace value then
            value
        else
            value.Substring(0, 1).ToLowerInvariant() + value.Substring(1)

[<FSharpGenerator>]
type CliHarnessGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            context.RegisterPostInitializationOutput(
                Action<FSharpPostInitializationContext>(fun post ->
                    post.AddImplementationSource("GeneratedPrelude", "module GeneratedPrelude\nlet answer = 42"))
            )

[<FSharpGenerator>]
type AdditionalFileGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            let moduleNames =
                context.AdditionalTextsProvider
                |> FSharpIncrementalValuesProvider.map (fun additional -> additional.Text.Trim())
                |> FSharpIncrementalValuesProvider.filter (String.IsNullOrWhiteSpace >> not)

            context.RegisterSourceOutput(
                moduleNames,
                Action<FSharpSourceProductionContext, string>(fun productionContext moduleName ->
                    productionContext.AddImplementationSource(moduleName, "module " + moduleName + "\nlet value = 1", Prelude))
            )

type MissingAttributeGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize _ = ()

[<FSharpGenerator(999)>]
type UnsupportedApiGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize _ = ()

[<FSharpGenerator>]
type ConstructorThrowsGenerator() =
    do invalidOp "constructor boom"

    interface IFSharpIncrementalGenerator with
        member _.Initialize _ = ()

[<FSharpGenerator>]
type NoPublicParameterlessConstructorGenerator private () =
    new(_: string) = NoPublicParameterlessConstructorGenerator()

    interface IFSharpIncrementalGenerator with
        member _.Initialize _ = ()

[<FSharpGenerator>]
type JsonConfigScenarioGenerator() =
    let inferType (property: JsonProperty) =
        match property.Value.ValueKind with
        | JsonValueKind.Number when property.Value.TryGetInt32() |> fst -> "int"
        | JsonValueKind.Number -> "float"
        | JsonValueKind.True
        | JsonValueKind.False -> "bool"
        | JsonValueKind.Object -> ScenarioHelpers.camelOrSnakeToPascal property.Name
        | _ -> "string"

    let rec emitObject typeName (element: JsonElement) =
        let nested =
            element.EnumerateObject()
            |> Seq.filter (fun p -> p.Value.ValueKind = JsonValueKind.Object)
            |> Seq.map (fun p -> emitObject (ScenarioHelpers.camelOrSnakeToPascal p.Name) p.Value)
            |> String.concat "\n\n"

        let fields =
            element.EnumerateObject()
            |> Seq.map (fun p -> sprintf "      %s: %s" (ScenarioHelpers.camelOrSnakeToPascal p.Name) (inferType p))
            |> String.concat "\n"

        let current = sprintf "type %s =\n    {\n%s\n    }" typeName fields

        if String.IsNullOrWhiteSpace nested then current else nested + "\n\n" + current

    let readerFunctions =
        """    let private afterKey (key: string) (json: string) =
        let marker = "\"" + key + "\""
        let keyIndex = json.IndexOf(marker, System.StringComparison.Ordinal)
        if keyIndex < 0 then "" else
            let colonIndex = json.IndexOf(":", keyIndex, System.StringComparison.Ordinal)
            if colonIndex < 0 then "" else json.Substring(colonIndex + 1).TrimStart()

    let private readString key json =
        let value = afterKey key json
        let start = value.IndexOf("\"", System.StringComparison.Ordinal)
        if start < 0 then "" else
            let finish = value.IndexOf("\"", start + 1, System.StringComparison.Ordinal)
            if finish < 0 then "" else value.Substring(start + 1, finish - start - 1)

    let private readBool key json =
        let value = afterKey key json
        value.StartsWith("true", System.StringComparison.OrdinalIgnoreCase)

    let private readInt key json =
        let value = afterKey key json
        let digits =
            value
            |> Seq.takeWhile (fun ch -> System.Char.IsDigit ch || ch = '-')
            |> Seq.toArray
            |> System.String
        if System.String.IsNullOrWhiteSpace digits then 0 else int digits

    let private readFloat key json =
        let value = afterKey key json
        let chars =
            value
            |> Seq.takeWhile (fun ch -> System.Char.IsDigit ch || ch = '-' || ch = '.')
            |> Seq.toArray
            |> System.String
        if System.String.IsNullOrWhiteSpace chars then 0.0 else float chars
"""

    let rec constructorExpression typeName (element: JsonElement) indent =
        let fieldLines =
            element.EnumerateObject()
            |> Seq.map (fun p ->
                let field = ScenarioHelpers.camelOrSnakeToPascal p.Name
                let expression =
                    match p.Value.ValueKind with
                    | JsonValueKind.Number when p.Value.TryGetInt32() |> fst -> sprintf "readInt \"%s\" json" p.Name
                    | JsonValueKind.Number -> sprintf "readFloat \"%s\" json" p.Name
                    | JsonValueKind.True
                    | JsonValueKind.False -> sprintf "readBool \"%s\" json" p.Name
                    | JsonValueKind.Object -> constructorExpression (ScenarioHelpers.camelOrSnakeToPascal p.Name) p.Value (indent + "  ")
                    | _ -> sprintf "readString \"%s\" json" p.Name

                sprintf "%s%s = %s" indent field expression)
            |> String.concat "\n"

        sprintf "{\n%s\n%s}" fieldLines (indent.Substring(0, max 0 (indent.Length - 2)))

    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            let jsonFiles =
                context.AdditionalFilesProvider
                |> FSharpIncrementalValuesProvider.filter (ScenarioHelpers.hasKindOrExtension "json" [ ".json" ])

            context.RegisterSourceOutput(
                jsonFiles,
                Action<FSharpSourceProductionContext, FSharpAdditionalFileInput>(fun production file ->
                    try
                        use document = JsonDocument.Parse file.Text

                        if document.RootElement.ValueKind <> JsonValueKind.Object then
                            production.ReportDiagnostic(ScenarioHelpers.diagnostic "FSGJSON0002" "JSON root must be an object." file.Path)
                        else
                            let ns = ScenarioHelpers.namespaceFor "Generated.Config" file
                            let rootType = ScenarioHelpers.typeNameFrom "JsonRootType" "Config" file
                            let types = emitObject rootType document.RootElement
                            let body = constructorExpression rootType document.RootElement "        "
                            let source = sprintf "namespace %s\n\n%s\n\nmodule %sLoader =\n%s\n    let parse (json: string) : %s =\n        %s\n" ns types rootType readerFunctions rootType body
                            production.AddImplementationSource(FSharpGeneratedNames.stableHintName "JsonConfigScenarioGenerator" file.Path rootType, source, BeforeLastSourceFile)
                    with ex ->
                        production.ReportDiagnostic(ScenarioHelpers.diagnostic "FSGJSON0001" ("Invalid JSON: " + ex.Message) file.Path))
            )

[<FSharpGenerator>]
type OpenApiScenarioGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            let files =
                context.AdditionalFilesProvider
                |> FSharpIncrementalValuesProvider.filter (ScenarioHelpers.hasKindOrExtension "openapi" [ ".openapi.json"; ".openapi.yaml"; ".openapi.yml" ])

            context.RegisterSourceOutput(
                files,
                Action<FSharpSourceProductionContext, FSharpAdditionalFileInput>(fun production file ->
                    try
                        use document = JsonDocument.Parse file.Text
                        let operationIds = ResizeArray<string>()

                        match document.RootElement.TryGetProperty("paths") with
                        | true, paths when paths.ValueKind = JsonValueKind.Object ->
                            for path in paths.EnumerateObject() do
                                if path.Value.ValueKind = JsonValueKind.Object then
                                    for method in path.Value.EnumerateObject() do
                                        match method.Value.TryGetProperty("operationId") with
                                        | true, op when op.ValueKind = JsonValueKind.String -> operationIds.Add(op.GetString())
                                        | _ -> ()
                        | _ -> ()

                        let duplicate =
                            operationIds
                            |> Seq.groupBy id
                            |> Seq.tryFind (fun (_, values) -> Seq.length values > 1)

                        match duplicate with
                        | Some(operationId, _) -> production.ReportDiagnostic(ScenarioHelpers.diagnostic "FSGOPENAPI0002" (sprintf "Duplicate operationId '%s'." operationId) file.Path)
                        | None ->
                            let ns = ScenarioHelpers.namespaceFor "Generated.Api" file
                            let clientName = ScenarioHelpers.typeNameFrom "OpenApiClientName" "OpenApiClient" file
                            let source =
                                sprintf
                                    """namespace %s

type Pet =
    { Id: int64
      Name: string }

type %s(send: string -> Async<string>) =
    let readString key (json: string) =
        let marker = "\"" + key + "\""
        let keyIndex = json.IndexOf(marker, System.StringComparison.Ordinal)
        if keyIndex < 0 then "" else
            let colonIndex = json.IndexOf(":", keyIndex, System.StringComparison.Ordinal)
            let value = json.Substring(colonIndex + 1).TrimStart()
            let start = value.IndexOf("\"", System.StringComparison.Ordinal)
            let finish = value.IndexOf("\"", start + 1, System.StringComparison.Ordinal)
            if start < 0 || finish < 0 then "" else value.Substring(start + 1, finish - start - 1)

    member _.GetPetById(petId: int64) : Async<Pet> =
        async {
            let! json = send ("/pets/" + string petId)
            return { Id = petId; Name = readString "name" json }
        }
"""
                                    ns
                                    clientName

                            production.AddImplementationSource(FSharpGeneratedNames.stableHintName "OpenApiScenarioGenerator" file.Path clientName, source, BeforeLastSourceFile)
                    with ex ->
                        production.ReportDiagnostic(ScenarioHelpers.diagnostic "FSGOPENAPI0001" ("Invalid OpenAPI JSON: " + ex.Message) file.Path))
            )

[<FSharpGenerator>]
type NativeHeaderScenarioGenerator() =
    let functionPattern =
        Regex(@"^\s*(?<ret>void|char|short|int|long|float|double)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<args>[^)]*)\)\s*;\s*$", RegexOptions.Compiled)

    let mapType ctype =
        match ctype with
        | "void" -> "unit"
        | "char" -> "byte"
        | "short" -> "int16"
        | "int" -> "int"
        | "long" -> "int64"
        | "float" -> "float32"
        | "double" -> "double"
        | _ -> "nativeint"

    let parseArg (arg: string) =
        let parts = arg.Trim().Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)

        if parts.Length = 2 then
            Some(mapType parts[0], FSharpGeneratedNames.sanitizeIdentifier parts[1])
        elif arg.Trim() = "void" || String.IsNullOrWhiteSpace arg then
            None
        else
            None

    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            let files =
                context.AdditionalFilesProvider
                |> FSharpIncrementalValuesProvider.filter (ScenarioHelpers.hasKindOrExtension "c-header" [ ".h" ])

            context.RegisterSourceOutput(
                files,
                Action<FSharpSourceProductionContext, FSharpAdditionalFileInput>(fun production file ->
                    let libraryName = ScenarioHelpers.tryMetadata "NativeLibraryName" file

                    match libraryName with
                    | None -> production.ReportDiagnostic(ScenarioHelpers.diagnostic "FSGNATIVE0002" "NativeLibraryName metadata is required." file.Path)
                    | Some library ->
                        let functions = ResizeArray<string>()
                        let mutable unsupported = None

                        for line in file.Text.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries) do
                            let m = functionPattern.Match line

                            if m.Success then
                                let args =
                                    m.Groups["args"].Value.Split([| ',' |], StringSplitOptions.RemoveEmptyEntries)
                                    |> Array.choose parseArg

                                let argsText = args |> Array.map (fun (typ, name) -> sprintf "%s %s" typ name) |> String.concat ", "
                                let returnType = mapType m.Groups["ret"].Value
                                let name = ScenarioHelpers.camelOrSnakeToPascal m.Groups["name"].Value
                                functions.Add(sprintf "    [<System.Runtime.InteropServices.DllImport(\"%s\", EntryPoint = \"%s\")>]\n    extern %s %s(%s)" library m.Groups["name"].Value returnType name argsText)
                            elif unsupported.IsNone then
                                unsupported <- Some line

                        match unsupported with
                        | Some line -> production.ReportDiagnostic(ScenarioHelpers.diagnostic "FSGNATIVE0001" ("Unsupported C declaration: " + line.Trim()) file.Path)
                        | None ->
                            let ns = ScenarioHelpers.namespaceFor "Generated.Native" file
                            let moduleName = ScenarioHelpers.typeNameFrom "NativeModuleName" "Native" file
                            let source = sprintf "namespace %s\n\nmodule %s =\n%s\n" ns moduleName (String.concat "\n\n" functions)
                            production.AddImplementationSource(FSharpGeneratedNames.stableHintName "NativeHeaderScenarioGenerator" file.Path moduleName, source, BeforeLastSourceFile))
            )

[<FSharpGenerator>]
type SqlScenarioGenerator() =
    let tryCommentValue key (text: string) =
        text.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.tryPick (fun line ->
            let prefix = "-- " + key + ":"
            let trimmed = line.Trim()

            if trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
                Some(trimmed.Substring(prefix.Length).Trim())
            else
                None)

    let selectedColumns (text: string) =
        let normalized = text.Replace("\r", " ").Replace("\n", " ")
        let selectIndex = normalized.IndexOf("select", StringComparison.OrdinalIgnoreCase)
        let fromIndex = normalized.IndexOf("from", StringComparison.OrdinalIgnoreCase)

        if selectIndex >= 0 && fromIndex > selectIndex then
            normalized.Substring(selectIndex + 6, fromIndex - selectIndex - 6)
            |> ScenarioHelpers.splitCsv
            |> List.map (fun column ->
                column.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.last
                |> fun value -> value.Trim('"', '[', ']'))
        else
            []

    let parameters (text: string) =
        Regex.Matches(text, @"@[A-Za-z_][A-Za-z0-9_]*")
        |> Seq.cast<Match>
        |> Seq.map (fun m -> m.Value.Substring(1))
        |> Seq.distinct
        |> Seq.toList

    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            let files =
                context.AdditionalFilesProvider
                |> FSharpIncrementalValuesProvider.filter (ScenarioHelpers.hasKindOrExtension "sql" [ ".sql" ])

            context.RegisterSourceOutput(
                files,
                Action<FSharpSourceProductionContext, FSharpAdditionalFileInput>(fun production file ->
                    match tryCommentValue "name" file.Text with
                    | None -> production.ReportDiagnostic(ScenarioHelpers.diagnostic "FSGSQL0001" "SQL file must declare '-- name: QueryName'." file.Path)
                    | Some queryName ->
                        let schema =
                            ScenarioHelpers.tryMetadata "SqlResultColumns" file
                            |> Option.map ScenarioHelpers.splitCsv
                            |> ScenarioHelpers.optionDefault []
                            |> List.choose (fun entry ->
                                match entry.Split([| ':' |], 2, StringSplitOptions.TrimEntries) with
                                | [| name; typ |] -> Some(name, typ)
                                | _ -> None)

                        let selected = selectedColumns file.Text
                        let missing = selected |> List.tryFind (fun column -> schema |> List.exists (fun (name, _) -> name.Equals(column, StringComparison.OrdinalIgnoreCase)) |> not)

                        match missing with
                        | Some column -> production.ReportDiagnostic(ScenarioHelpers.diagnostic "FSGSQL0002" (sprintf "Selected column '%s' is not present in SqlResultColumns metadata." column) file.Path)
                        | None ->
                            let ns = ScenarioHelpers.namespaceFor "Generated.Data" file
                            let rowType = ScenarioHelpers.camelOrSnakeToPascal queryName + "Row"
                            let functionName = queryName |> ScenarioHelpers.camelOrSnakeToPascal |> ScenarioHelpers.lowerFirst |> FSharpGeneratedNames.sanitizeIdentifier
                            let parameterText =
                                parameters file.Text
                                |> List.map (fun name -> sprintf "(%s: int)" (FSharpGeneratedNames.sanitizeIdentifier name))
                                |> String.concat " "

                            let fields =
                                schema
                                |> List.map (fun (name, typ) -> sprintf "      %s: %s" (ScenarioHelpers.camelOrSnakeToPascal name) typ)
                                |> String.concat "\n"

                            let source =
                                sprintf
                                    "namespace %s\n\ntype %s =\n    {\n%s\n    }\n\nmodule Queries =\n    let %s (_: obj) %s : Async<%s option> = async { return None }\n"
                                    ns
                                    rowType
                                    fields
                                    functionName
                                    parameterText
                                    rowType

                            production.AddImplementationSource(FSharpGeneratedNames.stableHintName "SqlScenarioGenerator" file.Path queryName, source, BeforeLastSourceFile))
            )
