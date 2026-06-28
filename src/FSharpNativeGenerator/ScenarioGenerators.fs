namespace FSharp.Compiler.SourceGeneration.Examples

open System
open System.Text.Json
open System.Text.RegularExpressions
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.SourceGeneration

module private ScenarioGeneratorHelpers =
    let tryMetadata key (file: FSharpAdditionalFileInput) =
        FSharpAdditionalFileInput.tryGetMetadata key file

    let namespaceFor defaultNamespace file =
        FSharpAdditionalFileInput.namespaceOrDefault defaultNamespace file
        |> FSharpGeneratedNames.sanitizeModuleName

    let typeNameFrom key fallback file =
        tryMetadata key file
        |> Option.orElse file.LogicalName
        |> Option.defaultValue fallback
        |> FSharpGeneratedNames.sanitizeIdentifier
        |> fun value -> value.Substring(0, 1).ToUpperInvariant() + value.Substring(1)

    let pascal (value: string) =
        value.Split([| '_'; '-'; ' ' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun part -> part.Substring(0, 1).ToUpperInvariant() + part.Substring(1))
        |> String.concat ""
        |> FSharpGeneratedNames.sanitizeIdentifier

    let lowerFirst (value: string) =
        if String.IsNullOrWhiteSpace value then value else value.Substring(0, 1).ToLowerInvariant() + value.Substring(1)

    let diagnostic id message path =
        { Id = id
          Message = message
          Severity = FSharpDiagnosticSeverity.Error
          Range = None
          FilePath = Some path }

    let hasKindOrExtension kind extensions (file: FSharpAdditionalFileInput) =
        match file.Kind with
        | Some value when value.Equals(kind, StringComparison.OrdinalIgnoreCase) -> true
        | _ -> extensions |> List.exists (fun extension -> file.Path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))

    let splitCsv (value: string) =
        value.Split([| ',' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map _.Trim()
        |> Array.filter (String.IsNullOrWhiteSpace >> not)
        |> Array.toList

[<FSharpGenerator>]
type JsonConfigGenerator() =
    let inferType (property: JsonProperty) =
        match property.Value.ValueKind with
        | JsonValueKind.Number when property.Value.TryGetInt32() |> fst -> "int"
        | JsonValueKind.Number -> "float"
        | JsonValueKind.True
        | JsonValueKind.False -> "bool"
        | JsonValueKind.Object -> ScenarioGeneratorHelpers.pascal property.Name
        | _ -> "string"

    let rec emitObject typeName (element: JsonElement) =
        let nested =
            element.EnumerateObject()
            |> Seq.filter (fun p -> p.Value.ValueKind = JsonValueKind.Object)
            |> Seq.map (fun p -> emitObject (ScenarioGeneratorHelpers.pascal p.Name) p.Value)
            |> String.concat "\n\n"

        let fields =
            element.EnumerateObject()
            |> Seq.map (fun p -> sprintf "      %s: %s" (ScenarioGeneratorHelpers.pascal p.Name) (inferType p))
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
        let digits = value |> Seq.takeWhile (fun ch -> System.Char.IsDigit ch || ch = '-') |> Seq.toArray |> System.String
        if System.String.IsNullOrWhiteSpace digits then 0 else int digits

    let private readFloat key json =
        let value = afterKey key json
        let chars = value |> Seq.takeWhile (fun ch -> System.Char.IsDigit ch || ch = '-' || ch = '.') |> Seq.toArray |> System.String
        if System.String.IsNullOrWhiteSpace chars then 0.0 else float chars
"""

    let rec constructorExpression (element: JsonElement) indent =
        let fieldLines =
            element.EnumerateObject()
            |> Seq.map (fun p ->
                let expression =
                    match p.Value.ValueKind with
                    | JsonValueKind.Number when p.Value.TryGetInt32() |> fst -> sprintf "readInt \"%s\" json" p.Name
                    | JsonValueKind.Number -> sprintf "readFloat \"%s\" json" p.Name
                    | JsonValueKind.True
                    | JsonValueKind.False -> sprintf "readBool \"%s\" json" p.Name
                    | JsonValueKind.Object -> constructorExpression p.Value (indent + "  ")
                    | _ -> sprintf "readString \"%s\" json" p.Name

                sprintf "%s%s = %s" indent (ScenarioGeneratorHelpers.pascal p.Name) expression)
            |> String.concat "\n"

        sprintf "{\n%s\n%s}" fieldLines (indent.Substring(0, max 0 (indent.Length - 2)))

    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            let files =
                context.AdditionalFilesProvider
                |> FSharpIncrementalValuesProvider.filter (ScenarioGeneratorHelpers.hasKindOrExtension "json" [ ".json" ])

            context.RegisterSourceOutput(
                files,
                Action<FSharpSourceProductionContext, FSharpAdditionalFileInput>(fun production file ->
                    try
                        use document = JsonDocument.Parse file.Text

                        if document.RootElement.ValueKind <> JsonValueKind.Object then
                            production.ReportDiagnostic(ScenarioGeneratorHelpers.diagnostic "FSGJSON0002" "JSON root must be an object." file.Path)
                        else
                            let ns = ScenarioGeneratorHelpers.namespaceFor "Generated.Config" file
                            let rootType = ScenarioGeneratorHelpers.typeNameFrom "JsonRootType" "Config" file
                            let source =
                                sprintf
                                    "namespace %s\n\n%s\n\nmodule %sLoader =\n%s\n    let parse (json: string) : %s =\n        %s\n"
                                    ns
                                    (emitObject rootType document.RootElement)
                                    rootType
                                    readerFunctions
                                    rootType
                                    (constructorExpression document.RootElement "        ")

                            production.AddImplementationSource(FSharpGeneratedNames.stableHintName "JsonConfigGenerator" file.Path rootType, source, BeforeLastSourceFile)
                    with ex ->
                        production.ReportDiagnostic(ScenarioGeneratorHelpers.diagnostic "FSGJSON0001" ("Invalid JSON: " + ex.Message) file.Path))
            )

[<FSharpGenerator>]
type OpenApiClientGenerator() =
    let tryProperty (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value -> Some value
        | _ -> None

    let schemaType (schema: JsonElement) =
        match tryProperty "type" schema with
        | Some typ when typ.ValueKind = JsonValueKind.String ->
            match typ.GetString() with
            | "integer" ->
                match tryProperty "format" schema with
                | Some format when format.ValueKind = JsonValueKind.String && format.GetString() = "int64" -> "int64"
                | _ -> "int"
            | "number" -> "float"
            | "boolean" -> "bool"
            | _ -> "string"
        | _ -> "string"

    let schemaNameFromRef (refValue: string) =
        let marker = "#/components/schemas/"
        if refValue.StartsWith(marker, StringComparison.Ordinal) then
            Some(refValue.Substring(marker.Length) |> ScenarioGeneratorHelpers.pascal)
        else
            None

    let schemaFields (schema: JsonElement) =
        match tryProperty "properties" schema with
        | Some properties when properties.ValueKind = JsonValueKind.Object ->
            properties.EnumerateObject()
            |> Seq.map (fun property -> property.Name, schemaType property.Value)
            |> Seq.toList
        | _ -> []

    let emitDto (schemaName: string) (schema: JsonElement) =
        let fields =
            schemaFields schema
            |> List.map (fun (name, typ) -> sprintf "      %s: %s" (ScenarioGeneratorHelpers.pascal name) typ)
            |> String.concat "\n"

        sprintf "type %s =\n    {\n%s\n    }" (ScenarioGeneratorHelpers.pascal schemaName) fields

    let emitParser (schemaName: string) (schema: JsonElement) =
        let assignments =
            schemaFields schema
            |> List.map (fun (name, typ) ->
                let field = ScenarioGeneratorHelpers.pascal name
                let reader =
                    match typ with
                    | "int" -> sprintf "readInt \"%s\" json" name
                    | "int64" -> sprintf "int64 (readInt \"%s\" json)" name
                    | "float" -> sprintf "readFloat \"%s\" json" name
                    | "bool" -> sprintf "readBool \"%s\" json" name
                    | _ -> sprintf "readString \"%s\" json" name

                sprintf "            %s = %s" field reader)
            |> String.concat "\n"

        sprintf "    let parse%s json : %s =\n        {\n%s\n        }\n" (ScenarioGeneratorHelpers.pascal schemaName) (ScenarioGeneratorHelpers.pascal schemaName) assignments

    let readerFunctions =
        """    let afterKey (key: string) (json: string) =
        let marker = "\"" + key + "\""
        let keyIndex = json.IndexOf(marker, System.StringComparison.Ordinal)
        if keyIndex < 0 then "" else
            let colonIndex = json.IndexOf(":", keyIndex, System.StringComparison.Ordinal)
            if colonIndex < 0 then "" else json.Substring(colonIndex + 1).TrimStart()

    let readString key json =
        let value = afterKey key json
        let start = value.IndexOf("\"", System.StringComparison.Ordinal)
        if start < 0 then "" else
            let finish = value.IndexOf("\"", start + 1, System.StringComparison.Ordinal)
            if finish < 0 then "" else value.Substring(start + 1, finish - start - 1)

    let readBool key json =
        let value = afterKey key json
        value.StartsWith("true", System.StringComparison.OrdinalIgnoreCase)

    let readInt key json =
        let value = afterKey key json
        let digits = value |> Seq.takeWhile (fun ch -> System.Char.IsDigit ch || ch = '-') |> Seq.toArray |> System.String
        if System.String.IsNullOrWhiteSpace digits then 0 else int digits

    let readFloat key json =
        let value = afterKey key json
        let chars = value |> Seq.takeWhile (fun ch -> System.Char.IsDigit ch || ch = '-' || ch = '.') |> Seq.toArray |> System.String
        if System.String.IsNullOrWhiteSpace chars then 0.0 else float chars
"""

    let responseSchemaName (operation: JsonElement) =
        tryProperty "responses" operation
        |> Option.bind (tryProperty "200")
        |> Option.bind (tryProperty "content")
        |> Option.bind (tryProperty "application/json")
        |> Option.bind (tryProperty "schema")
        |> Option.bind (tryProperty "$ref")
        |> Option.bind (fun value -> if value.ValueKind = JsonValueKind.String then schemaNameFromRef (value.GetString()) else None)

    let pathParameterName (path: string) =
        let m = Regex.Match(path, @"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}")
        if m.Success then FSharpGeneratedNames.sanitizeIdentifier m.Groups["name"].Value else "id"

    let emitOperation (path: string) (operation: JsonElement) =
        match tryProperty "operationId" operation, responseSchemaName operation with
        | Some operationId, Some returnType when operationId.ValueKind = JsonValueKind.String ->
            let methodName = operationId.GetString() |> ScenarioGeneratorHelpers.pascal
            let parameterName = pathParameterName path
            sprintf
                "    member _.%s(%s: int64) : Async<%s> =\n        async {\n            let! json = send (\"%s\".Replace(\"{%s}\", string %s))\n            return parse%s json\n        }\n"
                methodName
                parameterName
                returnType
                path
                parameterName
                parameterName
                returnType
        | _ -> ""

    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            let files =
                context.AdditionalFilesProvider
                |> FSharpIncrementalValuesProvider.filter (ScenarioGeneratorHelpers.hasKindOrExtension "openapi" [ ".openapi.json"; ".openapi.yaml"; ".openapi.yml" ])

            context.RegisterSourceOutput(
                files,
                Action<FSharpSourceProductionContext, FSharpAdditionalFileInput>(fun production file ->
                    try
                        use document = JsonDocument.Parse file.Text
                        let operationIds = ResizeArray<string>()

                        match document.RootElement.TryGetProperty("paths") with
                        | true, paths when paths.ValueKind = JsonValueKind.Object ->
                            for path in paths.EnumerateObject() do
                                for method in path.Value.EnumerateObject() do
                                    match method.Value.TryGetProperty("operationId") with
                                    | true, op when op.ValueKind = JsonValueKind.String -> operationIds.Add(op.GetString())
                                    | _ -> ()
                        | _ -> ()

                        match operationIds |> Seq.groupBy id |> Seq.tryFind (fun (_, values) -> Seq.length values > 1) with
                        | Some(operationId, _) -> production.ReportDiagnostic(ScenarioGeneratorHelpers.diagnostic "FSGOPENAPI0002" (sprintf "Duplicate operationId '%s'." operationId) file.Path)
                        | None ->
                            let ns = ScenarioGeneratorHelpers.namespaceFor "Generated.Api" file
                            let clientName = ScenarioGeneratorHelpers.typeNameFrom "OpenApiClientName" "OpenApiClient" file
                            let schemas =
                                tryProperty "components" document.RootElement
                                |> Option.bind (tryProperty "schemas")
                                |> Option.map (fun (schemas: JsonElement) -> schemas.EnumerateObject() |> Seq.map (fun schema -> schema.Name, schema.Value) |> Seq.toList)
                                |> Option.defaultValue []

                            let dtos = schemas |> List.map (fun (name, schema) -> emitDto name schema) |> String.concat "\n\n"
                            let parsers = schemas |> List.map (fun (name, schema) -> emitParser name schema) |> String.concat "\n"

                            let operations =
                                match tryProperty "paths" document.RootElement with
                                | Some paths when paths.ValueKind = JsonValueKind.Object ->
                                    paths.EnumerateObject()
                                    |> Seq.collect (fun path ->
                                        path.Value.EnumerateObject()
                                        |> Seq.map (fun operation -> emitOperation path.Name operation.Value))
                                    |> String.concat "\n"
                                | _ -> ""

                            let source =
                                sprintf
                                    """namespace %s

%s

type %s(send: string -> Async<string>) =
%s
%s
%s
"""
                                    ns
                                    dtos
                                    clientName
                                    readerFunctions
                                    parsers
                                    operations

                            production.AddImplementationSource(FSharpGeneratedNames.stableHintName "OpenApiClientGenerator" file.Path clientName, source, BeforeLastSourceFile)
                    with ex ->
                        production.ReportDiagnostic(ScenarioGeneratorHelpers.diagnostic "FSGOPENAPI0001" ("Invalid OpenAPI JSON: " + ex.Message) file.Path))
            )

[<FSharpGenerator>]
type NativeHeaderBindingGenerator() =
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
        if parts.Length = 2 then Some(mapType parts[0], FSharpGeneratedNames.sanitizeIdentifier parts[1]) else None

    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            let files =
                context.AdditionalFilesProvider
                |> FSharpIncrementalValuesProvider.filter (ScenarioGeneratorHelpers.hasKindOrExtension "c-header" [ ".h" ])

            context.RegisterSourceOutput(
                files,
                Action<FSharpSourceProductionContext, FSharpAdditionalFileInput>(fun production file ->
                    match FSharpAdditionalFileInput.requireMetadata "NativeLibraryName" file with
                    | Error message -> production.ReportDiagnostic(ScenarioGeneratorHelpers.diagnostic "FSGNATIVE0002" message file.Path)
                    | Ok library ->
                        let functions = ResizeArray<string>()
                        let mutable unsupported = None

                        for line in file.Text.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries) do
                            let m = functionPattern.Match line
                            if m.Success then
                                let args = m.Groups["args"].Value.Split([| ',' |], StringSplitOptions.RemoveEmptyEntries) |> Array.choose parseArg
                                let argsText = args |> Array.map (fun (typ, name) -> sprintf "%s %s" typ name) |> String.concat ", "
                                functions.Add(sprintf "    [<System.Runtime.InteropServices.DllImport(\"%s\", EntryPoint = \"%s\")>]\n    extern %s %s(%s)" library m.Groups["name"].Value (mapType m.Groups["ret"].Value) (ScenarioGeneratorHelpers.pascal m.Groups["name"].Value) argsText)
                            elif unsupported.IsNone then
                                unsupported <- Some line

                        match unsupported with
                        | Some line -> production.ReportDiagnostic(ScenarioGeneratorHelpers.diagnostic "FSGNATIVE0001" ("Unsupported C declaration: " + line.Trim()) file.Path)
                        | None ->
                            let ns = ScenarioGeneratorHelpers.namespaceFor "Generated.Native" file
                            let moduleName = ScenarioGeneratorHelpers.typeNameFrom "NativeModuleName" "Native" file
                            let source = sprintf "namespace %s\n\nmodule %s =\n%s\n" ns moduleName (String.concat "\n\n" functions)
                            production.AddImplementationSource(FSharpGeneratedNames.stableHintName "NativeHeaderBindingGenerator" file.Path moduleName, source, BeforeLastSourceFile))
            )

[<FSharpGenerator>]
type SqlQueryGenerator() =
    let tryCommentValue key (text: string) =
        text.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.tryPick (fun line ->
            let prefix = "-- " + key + ":"
            let trimmed = line.Trim()
            if trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then Some(trimmed.Substring(prefix.Length).Trim()) else None)

    let selectedColumns (text: string) =
        let normalized = text.Replace("\r", " ").Replace("\n", " ")
        let selectIndex = normalized.IndexOf("select", StringComparison.OrdinalIgnoreCase)
        let fromIndex = normalized.IndexOf("from", StringComparison.OrdinalIgnoreCase)
        if selectIndex >= 0 && fromIndex > selectIndex then
            normalized.Substring(selectIndex + 6, fromIndex - selectIndex - 6)
            |> ScenarioGeneratorHelpers.splitCsv
            |> List.map (fun column -> column.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries) |> Array.last |> fun value -> value.Trim('"', '[', ']'))
        else
            []

    let parameters text =
        Regex.Matches(text, @"@[A-Za-z_][A-Za-z0-9_]*")
        |> Seq.cast<Match>
        |> Seq.map (fun m -> m.Value.Substring(1))
        |> Seq.distinct
        |> Seq.toList

    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            let files =
                context.AdditionalFilesProvider
                |> FSharpIncrementalValuesProvider.filter (ScenarioGeneratorHelpers.hasKindOrExtension "sql" [ ".sql" ])

            context.RegisterSourceOutput(
                files,
                Action<FSharpSourceProductionContext, FSharpAdditionalFileInput>(fun production file ->
                    match tryCommentValue "name" file.Text with
                    | None -> production.ReportDiagnostic(ScenarioGeneratorHelpers.diagnostic "FSGSQL0001" "SQL file must declare '-- name: QueryName'." file.Path)
                    | Some queryName ->
                        let schema =
                            ScenarioGeneratorHelpers.tryMetadata "SqlResultColumns" file
                            |> Option.map ScenarioGeneratorHelpers.splitCsv
                            |> Option.defaultValue []
                            |> List.choose (fun entry ->
                                match entry.Split([| ':' |], 2, StringSplitOptions.TrimEntries) with
                                | [| name; typ |] -> Some(name, typ)
                                | _ -> None)

                        match selectedColumns file.Text |> List.tryFind (fun column -> schema |> List.exists (fun (name, _) -> name.Equals(column, StringComparison.OrdinalIgnoreCase)) |> not) with
                        | Some column -> production.ReportDiagnostic(ScenarioGeneratorHelpers.diagnostic "FSGSQL0002" (sprintf "Selected column '%s' is not present in SqlResultColumns metadata." column) file.Path)
                        | None ->
                            let ns = ScenarioGeneratorHelpers.namespaceFor "Generated.Data" file
                            let rowType = ScenarioGeneratorHelpers.pascal queryName + "Row"
                            let fields = schema |> List.map (fun (name, typ) -> sprintf "      %s: %s" (ScenarioGeneratorHelpers.pascal name) typ) |> String.concat "\n"
                            let queryText = file.Text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n")
                            let parameterNames = parameters file.Text
                            let parameterText = parameterNames |> List.map (fun name -> sprintf "(%s: int)" (FSharpGeneratedNames.sanitizeIdentifier name)) |> String.concat " "
                            let parameterValues = parameterNames |> List.map (fun name -> sprintf "\"%s\", box %s" name (FSharpGeneratedNames.sanitizeIdentifier name)) |> String.concat "; "
                            let fieldAssignments =
                                schema
                                |> List.map (fun (name, typ) ->
                                    let field = ScenarioGeneratorHelpers.pascal name
                                    let value =
                                        match typ with
                                        | "int" -> sprintf "unbox<int> row[\"%s\"]" name
                                        | "int64" -> sprintf "unbox<int64> row[\"%s\"]" name
                                        | "string" -> sprintf "unbox<string> row[\"%s\"]" name
                                        | "string option" -> sprintf "unbox<string option> row[\"%s\"]" name
                                        | "bool" -> sprintf "unbox<bool> row[\"%s\"]" name
                                        | _ -> sprintf "unbox<%s> row[\"%s\"]" typ name

                                    sprintf "                    %s = %s" field value)
                                |> String.concat "\n"
                            let source =
                                sprintf
                                    "namespace %s\n\ntype %s =\n    {\n%s\n    }\n\nmodule Queries =\n    type QueryExecutor = string -> (string * obj) list -> Async<Map<string, obj> option>\n\n    let %s (execute: QueryExecutor) %s : Async<%s option> =\n        async {\n            let! result = execute \"%s\" [ %s ]\n            return\n                result\n                |> Option.map (fun row ->\n                    {\n%s\n                    })\n        }\n"
                                    ns
                                    rowType
                                    fields
                                    (queryName |> ScenarioGeneratorHelpers.pascal |> ScenarioGeneratorHelpers.lowerFirst |> FSharpGeneratedNames.sanitizeIdentifier)
                                    parameterText
                                    rowType
                                    queryText
                                    parameterValues
                                    fieldAssignments

                            production.AddImplementationSource(FSharpGeneratedNames.stableHintName "SqlQueryGenerator" file.Path queryName, source, BeforeLastSourceFile))
            )
