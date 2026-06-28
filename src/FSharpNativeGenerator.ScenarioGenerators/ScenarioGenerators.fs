namespace FSharpNativeGenerator.ScenarioGenerators

open System
open System.Text
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

    let pascal (value: string) =
        value.Split([| '_'; '-'; ' ' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.collect (fun part ->
            if String.IsNullOrWhiteSpace part then
                [||]
            else
                [| part.Substring(0, 1).ToUpperInvariant() + part.Substring(1) |])
        |> String.concat ""
        |> FSharpGeneratedNames.sanitizeIdentifier

    let typeNameFrom key fallback file =
        tryMetadata key file
        |> Option.orElse file.LogicalName
        |> Option.defaultValue fallback
        |> pascal

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

    let fsharpStringLiteral (value: string) =
        let builder = StringBuilder(value.Length + 2)
        builder.Append('"') |> ignore

        for ch in value do
            match ch with
            | '\\' -> builder.Append("\\\\") |> ignore
            | '"' -> builder.Append("\\\"") |> ignore
            | '\n' -> builder.Append("\\n") |> ignore
            | '\r' -> builder.Append("\\r") |> ignore
            | '\t' -> builder.Append("\\t") |> ignore
            | _ -> builder.Append(ch) |> ignore

        builder.Append('"').ToString()

[<FSharpGenerator>]
type JsonConfigGenerator() =
    let rec validate path (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object ->
            element.EnumerateObject()
            |> Seq.tryPick (fun property -> validate (path + "." + property.Name) property.Value)
        | JsonValueKind.String
        | JsonValueKind.Number
        | JsonValueKind.True
        | JsonValueKind.False -> None
        | unsupported -> Some(sprintf "Unsupported JSON value kind '%A' at '%s'." unsupported path)

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

    let runtimeReaders =
        """    let private requireProperty (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value -> value
        | false, _ -> invalidArg name ("Missing required JSON property '" + name + "'.")

    let private readString (name: string) (element: JsonElement) =
        let value = requireProperty name element
        if value.ValueKind = JsonValueKind.String then value.GetString() else invalidArg name ("Expected JSON string property '" + name + "'.")

    let private readBool (name: string) (element: JsonElement) =
        let value = requireProperty name element
        match value.ValueKind with
        | JsonValueKind.True -> true
        | JsonValueKind.False -> false
        | _ -> invalidArg name ("Expected JSON boolean property '" + name + "'.")

    let private readInt (name: string) (element: JsonElement) =
        let value = requireProperty name element
        match value.TryGetInt32() with
        | true, number -> number
        | false, _ -> invalidArg name ("Expected JSON int property '" + name + "'.")

    let private readFloat (name: string) (element: JsonElement) =
        let value = requireProperty name element
        match value.TryGetDouble() with
        | true, number -> number
        | false, _ -> invalidArg name ("Expected JSON number property '" + name + "'.")

    let private readObject (name: string) (element: JsonElement) =
        let value = requireProperty name element
        if value.ValueKind = JsonValueKind.Object then value else invalidArg name ("Expected JSON object property '" + name + "'.")
"""

    let rec emitParser typeName (element: JsonElement) =
        let nestedParsers =
            element.EnumerateObject()
            |> Seq.filter (fun p -> p.Value.ValueKind = JsonValueKind.Object)
            |> Seq.map (fun p -> emitParser (ScenarioGeneratorHelpers.pascal p.Name) p.Value)
            |> String.concat "\n"

        let assignments =
            element.EnumerateObject()
            |> Seq.map (fun p ->
                let field = ScenarioGeneratorHelpers.pascal p.Name
                let propertyName = ScenarioGeneratorHelpers.fsharpStringLiteral p.Name
                let expression =
                    match p.Value.ValueKind with
                    | JsonValueKind.Number when p.Value.TryGetInt32() |> fst -> sprintf "readInt %s element" propertyName
                    | JsonValueKind.Number -> sprintf "readFloat %s element" propertyName
                    | JsonValueKind.True
                    | JsonValueKind.False -> sprintf "readBool %s element" propertyName
                    | JsonValueKind.Object -> sprintf "parse%sElement (readObject %s element)" (ScenarioGeneratorHelpers.pascal p.Name) propertyName
                    | _ -> sprintf "readString %s element" propertyName

                sprintf "            %s = %s" field expression)
            |> String.concat "\n"

        let current = sprintf "    let rec private parse%sElement (element: JsonElement) : %s =\n        {\n%s\n        }\n" typeName typeName assignments
        nestedParsers + current

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
                            match validate "$" document.RootElement with
                            | Some message -> production.ReportDiagnostic(ScenarioGeneratorHelpers.diagnostic "FSGJSON0003" message file.Path)
                            | None ->
                                let ns = ScenarioGeneratorHelpers.namespaceFor "Generated.Config" file
                                let rootType = ScenarioGeneratorHelpers.typeNameFrom "JsonRootType" "Config" file
                                let source =
                                    sprintf
                                        "namespace %s\n\nopen System.Text.Json\n\n%s\n\nmodule %sLoader =\n%s\n%s\n    let parse (json: string) : %s =\n        use document = JsonDocument.Parse json\n        parse%sElement document.RootElement\n"
                                        ns
                                        (emitObject rootType document.RootElement)
                                        rootType
                                        runtimeReaders
                                        (emitParser rootType document.RootElement)
                                        rootType
                                        rootType

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

    let responseSchemaName (operation: JsonElement) =
        tryProperty "responses" operation
        |> Option.bind (tryProperty "200")
        |> Option.bind (tryProperty "content")
        |> Option.bind (tryProperty "application/json")
        |> Option.bind (tryProperty "schema")
        |> Option.bind (tryProperty "$ref")
        |> Option.bind (fun value -> if value.ValueKind = JsonValueKind.String then schemaNameFromRef (value.GetString()) else None)

    let pathParameter (path: string) =
        let m = Regex.Match(path, @"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}")
        if m.Success then m.Groups["name"].Value, FSharpGeneratedNames.sanitizeIdentifier m.Groups["name"].Value else "id", "id"

    let operationName (operation: JsonElement) =
        tryProperty "operationId" operation
        |> Option.bind (fun operationId -> if operationId.ValueKind = JsonValueKind.String then Some(operationId.GetString()) else None)

    let emitOperation (path: string) (operation: JsonElement) =
        match operationName operation with
        | None -> Error(sprintf "OpenAPI operation at path '%s' must declare a string operationId." path)
        | Some operationId ->
            match responseSchemaName operation with
            | None -> Error(sprintf "OpenAPI operation '%s' must declare a 200 application/json response schema reference." operationId)
            | Some returnType ->
                let methodName = operationId |> ScenarioGeneratorHelpers.pascal
                let originalParameterName, parameterName = pathParameter path
                let pathLiteral = ScenarioGeneratorHelpers.fsharpStringLiteral path
                let placeholderLiteral = ScenarioGeneratorHelpers.fsharpStringLiteral ("{" + originalParameterName + "}")
                Ok(
                    sprintf
                        "    member _.%s(%s: int64) : Async<%s> =\n        async {\n            let path = %s.Replace(%s, string %s)\n            let! json = send path\n            return deserialize<%s> json\n        }\n"
                        methodName
                        parameterName
                        returnType
                        pathLiteral
                        placeholderLiteral
                        parameterName
                        returnType
                )

    let collectOperationIds (document: JsonDocument) =
        let operationIds = ResizeArray<string>()

        match document.RootElement.TryGetProperty("paths") with
        | true, paths when paths.ValueKind = JsonValueKind.Object ->
            for path in paths.EnumerateObject() do
                if path.Value.ValueKind = JsonValueKind.Object then
                    for method in path.Value.EnumerateObject() do
                        match operationName method.Value with
                        | Some value -> operationIds.Add value
                        | None -> ()
        | _ -> ()

        operationIds

    let emitOperations (document: JsonDocument) =
        match tryProperty "paths" document.RootElement with
        | Some paths when paths.ValueKind = JsonValueKind.Object ->
            let operations = ResizeArray<string>()
            let errors = ResizeArray<string>()

            for path in paths.EnumerateObject() do
                if path.Value.ValueKind = JsonValueKind.Object then
                    for operation in path.Value.EnumerateObject() do
                        match emitOperation path.Name operation.Value with
                        | Ok source -> operations.Add source
                        | Error message -> errors.Add message

            if errors.Count > 0 then Error(String.concat " " errors) else Ok(String.concat "\n" operations)
        | _ -> Error("OpenAPI document must contain a paths object.")

    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            let files =
                context.AdditionalFilesProvider
                |> FSharpIncrementalValuesProvider.filter (ScenarioGeneratorHelpers.hasKindOrExtension "openapi" [ ".openapi.json" ])

            context.RegisterSourceOutput(
                files,
                Action<FSharpSourceProductionContext, FSharpAdditionalFileInput>(fun production file ->
                    if file.Path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || file.Path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) then
                        production.ReportDiagnostic(ScenarioGeneratorHelpers.diagnostic "FSGOPENAPI0003" "OpenAPI YAML input is not supported by this generator; use JSON input." file.Path)
                    else
                        try
                            use document = JsonDocument.Parse file.Text
                            let operationIds = collectOperationIds document

                            match operationIds |> Seq.groupBy id |> Seq.tryFind (fun (_, values) -> Seq.length values > 1) with
                            | Some(operationId, _) -> production.ReportDiagnostic(ScenarioGeneratorHelpers.diagnostic "FSGOPENAPI0002" (sprintf "Duplicate operationId '%s'." operationId) file.Path)
                            | None ->
                                match emitOperations document with
                                | Error message -> production.ReportDiagnostic(ScenarioGeneratorHelpers.diagnostic "FSGOPENAPI0004" message file.Path)
                                | Ok operations ->
                                    let ns = ScenarioGeneratorHelpers.namespaceFor "Generated.Api" file
                                    let clientName = ScenarioGeneratorHelpers.typeNameFrom "OpenApiClientName" "OpenApiClient" file
                                    let schemas =
                                        tryProperty "components" document.RootElement
                                        |> Option.bind (tryProperty "schemas")
                                        |> Option.map (fun (schemas: JsonElement) -> schemas.EnumerateObject() |> Seq.map (fun schema -> schema.Name, schema.Value) |> Seq.toList)
                                        |> Option.defaultValue []

                                    let dtos = schemas |> List.map (fun (name, schema) -> emitDto name schema) |> String.concat "\n\n"
                                    let source =
                                        sprintf
                                            "namespace %s\n\nopen System.Text.Json\n\n%s\n\ntype %s(send: string -> Async<string>) =\n    let serializerOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)\n\n    let deserialize<'T> (json: string) : 'T =\n        let value = JsonSerializer.Deserialize<'T>(json, serializerOptions)\n        if obj.ReferenceEquals(box value, null) then invalidOp \"OpenAPI response deserialized to null.\" else value\n\n%s"
                                            ns
                                            dtos
                                            clientName
                                            operations

                                    production.AddImplementationSource(FSharpGeneratedNames.stableHintName "OpenApiClientGenerator" file.Path clientName, source, BeforeLastSourceFile)
                        with ex ->
                            production.ReportDiagnostic(ScenarioGeneratorHelpers.diagnostic "FSGOPENAPI0001" ("Invalid OpenAPI JSON: " + ex.Message) file.Path))
            )

[<FSharpGenerator>]
type NativeHeaderBindingGenerator() =
    let functionPattern =
        Regex(@"^\s*(?<ret>void|char|short|int|long|float|double)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<args>[^)]*)\)\s*;\s*$", RegexOptions.Compiled)

    let mapPrimitive ctype =
        match ctype with
        | "void" -> Some "unit"
        | "char" -> Some "byte"
        | "short" -> Some "int16"
        | "int" -> Some "int"
        | "long" -> Some "int64"
        | "float" -> Some "float32"
        | "double" -> Some "double"
        | _ -> None

    let parseArg (arg: string) =
        let trimmed = arg.Trim()
        if String.IsNullOrWhiteSpace trimmed || trimmed = "void" then
            Ok None
        else
            let m = Regex.Match(trimmed, @"^(?<type>const\s+char\s*\*|char\s*\*|void\s*\*|char|short|int|long|float|double)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)$")
            if not m.Success then
                Error(sprintf "Unsupported C argument '%s'." trimmed)
            else
                let ctype = Regex.Replace(m.Groups["type"].Value, @"\s+", " ").Replace(" *", "*").Trim()
                let name = FSharpGeneratedNames.sanitizeIdentifier m.Groups["name"].Value
                match ctype with
                | "const char*"
                | "char*" -> Ok(Some("string", name))
                | "void*" -> Ok(Some("nativeint", name))
                | primitive ->
                    match mapPrimitive primitive with
                    | Some typ -> Ok(Some(typ, name))
                    | None -> Error(sprintf "Unsupported C argument type '%s'." ctype)

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
                        let errors = ResizeArray<string>()

                        for line in file.Text.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries) do
                            let m = functionPattern.Match line
                            if m.Success then
                                let parsedArgs = m.Groups["args"].Value.Split([| ',' |], StringSplitOptions.RemoveEmptyEntries) |> Array.map parseArg
                                let argErrors = parsedArgs |> Array.choose (function Error message -> Some message | Ok _ -> None)
                                if argErrors.Length > 0 then
                                    errors.AddRange argErrors
                                else
                                    let args = parsedArgs |> Array.choose (function Ok(Some arg) -> Some arg | _ -> None)
                                    let argsText = args |> Array.map (fun (typ, name) -> sprintf "%s %s" typ name) |> String.concat ", "
                                    let returnType = mapPrimitive m.Groups["ret"].Value |> Option.defaultValue "nativeint"
                                    let libraryLiteral = ScenarioGeneratorHelpers.fsharpStringLiteral library
                                    let entryPointLiteral = ScenarioGeneratorHelpers.fsharpStringLiteral m.Groups["name"].Value
                                    functions.Add(sprintf "    [<System.Runtime.InteropServices.DllImport(%s, EntryPoint = %s)>]\n    extern %s %s(%s)" libraryLiteral entryPointLiteral returnType (ScenarioGeneratorHelpers.pascal m.Groups["name"].Value) argsText)
                            else
                                errors.Add("Unsupported C declaration: " + line.Trim())

                        if errors.Count > 0 then
                            production.ReportDiagnostic(ScenarioGeneratorHelpers.diagnostic "FSGNATIVE0001" (String.concat " " errors) file.Path)
                        else
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

    let supportedTypePattern = Regex(@"^(int|int64|float|float32|bool|string|decimal)( option)?$", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

    let parseTypeMap metadataValue =
        metadataValue
        |> ScenarioGeneratorHelpers.splitCsv
        |> List.choose (fun entry ->
            match entry.Split([| ':' |], 2, StringSplitOptions.TrimEntries) with
            | [| name; typ |] when not (String.IsNullOrWhiteSpace name) && not (String.IsNullOrWhiteSpace typ) -> Some(name, typ)
            | _ -> None)

    let unsupportedType mappings =
        mappings
        |> List.tryFind (fun (_, typ) -> not (supportedTypePattern.IsMatch typ))
        |> Option.map snd

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
                            |> Option.map parseTypeMap
                            |> Option.defaultValue []

                        let parameterTypes =
                            ScenarioGeneratorHelpers.tryMetadata "SqlParameterTypes" file
                            |> Option.map parseTypeMap
                            |> Option.defaultValue []

                        let missingColumn = selectedColumns file.Text |> List.tryFind (fun column -> schema |> List.exists (fun (name, _) -> name.Equals(column, StringComparison.OrdinalIgnoreCase)) |> not)
                        let missingParameter = parameters file.Text |> List.tryFind (fun parameter -> parameterTypes |> List.exists (fun (name, _) -> name.Equals(parameter, StringComparison.OrdinalIgnoreCase)) |> not)

                        match missingColumn, missingParameter, unsupportedType schema, unsupportedType parameterTypes with
                        | Some column, _, _, _ -> production.ReportDiagnostic(ScenarioGeneratorHelpers.diagnostic "FSGSQL0002" (sprintf "Selected column '%s' is not present in SqlResultColumns metadata." column) file.Path)
                        | None, Some parameter, _, _ -> production.ReportDiagnostic(ScenarioGeneratorHelpers.diagnostic "FSGSQL0003" (sprintf "SQL parameter '@%s' is not present in SqlParameterTypes metadata." parameter) file.Path)
                        | None, None, Some typ, _
                        | None, None, None, Some typ -> production.ReportDiagnostic(ScenarioGeneratorHelpers.diagnostic "FSGSQL0004" (sprintf "SQL type '%s' is not supported by this generator." typ) file.Path)
                        | None, None, None, None ->
                            let ns = ScenarioGeneratorHelpers.namespaceFor "Generated.Data" file
                            let rowType = ScenarioGeneratorHelpers.pascal queryName + "Row"
                            let fields = schema |> List.map (fun (name, typ) -> sprintf "      %s: %s" (ScenarioGeneratorHelpers.pascal name) typ) |> String.concat "\n"
                            let parameterNames = parameters file.Text
                            let parameterText =
                                parameterNames
                                |> List.map (fun name ->
                                    let typ = parameterTypes |> List.find (fun (candidate, _) -> candidate.Equals(name, StringComparison.OrdinalIgnoreCase)) |> snd
                                    sprintf "(%s: %s)" (FSharpGeneratedNames.sanitizeIdentifier name) typ)
                                |> String.concat " "

                            let parameterPairs =
                                parameterNames
                                |> List.map (fun name -> sprintf "%s, box %s" (ScenarioGeneratorHelpers.fsharpStringLiteral name) (FSharpGeneratedNames.sanitizeIdentifier name))
                                |> String.concat "; "

                            let fieldAssignments =
                                schema
                                |> List.map (fun (name, typ) -> sprintf "                %s = read<%s> %s row" (ScenarioGeneratorHelpers.pascal name) typ (ScenarioGeneratorHelpers.fsharpStringLiteral name))
                                |> String.concat "\n"

                            let source =
                                sprintf
                                    "namespace %s\n\ntype %s =\n    {\n%s\n    }\n\ntype SqlParameters = (string * obj) list\ntype SqlRow = Map<string, obj>\ntype SqlQueryExecutor = string -> SqlParameters -> Async<SqlRow option>\n\nmodule Queries =\n    let private sqlText = %s\n\n    let private read<'T> name (row: SqlRow) : 'T =\n        match row.TryFind name with\n        | Some value -> unbox<'T> value\n        | None -> invalidArg name (\"SQL result row did not contain column '\" + name + \"'.\")\n\n    let %s (execute: SqlQueryExecutor) %s : Async<%s option> =\n        async {\n            let parameters: SqlParameters = [ %s ]\n            let! row = execute sqlText parameters\n            return row |> Option.map (fun row ->\n                {\n%s\n                })\n        }\n"
                                    ns
                                    rowType
                                    fields
                                    (ScenarioGeneratorHelpers.fsharpStringLiteral file.Text)
                                    (queryName |> ScenarioGeneratorHelpers.pascal |> ScenarioGeneratorHelpers.lowerFirst |> FSharpGeneratedNames.sanitizeIdentifier)
                                    parameterText
                                    rowType
                                    parameterPairs
                                    fieldAssignments

                            production.AddImplementationSource(FSharpGeneratedNames.stableHintName "SqlQueryGenerator" file.Path queryName, source, BeforeLastSourceFile))
            )
