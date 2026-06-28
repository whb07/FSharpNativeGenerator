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
                let expression =
                    match p.Value.ValueKind with
                    | JsonValueKind.Number when p.Value.TryGetInt32() |> fst -> sprintf "readInt \"%s\" element" p.Name
                    | JsonValueKind.Number -> sprintf "readFloat \"%s\" element" p.Name
                    | JsonValueKind.True
                    | JsonValueKind.False -> sprintf "readBool \"%s\" element" p.Name
                    | JsonValueKind.Object -> sprintf "parse%sElement (readObject \"%s\" element)" (ScenarioGeneratorHelpers.pascal p.Name) p.Name
                    | _ -> sprintf "readString \"%s\" element" p.Name

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

    let pathParameterName (path: string) =
        let m = Regex.Match(path, @"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}")
        if m.Success then FSharpGeneratedNames.sanitizeIdentifier m.Groups["name"].Value else "id"

    let emitOperation (path: string) (operation: JsonElement) =
        match tryProperty "operationId" operation, responseSchemaName operation with
        | Some operationId, Some returnType when operationId.ValueKind = JsonValueKind.String ->
            let methodName = operationId.GetString() |> ScenarioGeneratorHelpers.pascal
            let parameterName = pathParameterName path
            sprintf
                "    member _.%s(%s: int64) : Async<%s> =\n        async {\n            let! _json = send (\"%s\".Replace(\"{%s}\", string %s))\n            return Unchecked.defaultof<%s>\n        }\n"
                methodName parameterName returnType path parameterName parameterName returnType
        | _ -> ""

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
                                let operations =
                                    match tryProperty "paths" document.RootElement with
                                    | Some paths when paths.ValueKind = JsonValueKind.Object ->
                                        paths.EnumerateObject()
                                        |> Seq.collect (fun path -> path.Value.EnumerateObject() |> Seq.map (fun operation -> emitOperation path.Name operation.Value))
                                        |> String.concat "\n"
                                    | _ -> ""

                                let source = sprintf "namespace %s\n\n%s\n\ntype %s(send: string -> Async<string>) =\n%s" ns dtos clientName operations
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
                                    functions.Add(sprintf "    [<System.Runtime.InteropServices.DllImport(\"%s\", EntryPoint = \"%s\")>]\n    extern %s %s(%s)" library m.Groups["name"].Value returnType (ScenarioGeneratorHelpers.pascal m.Groups["name"].Value) argsText)
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

    let parseTypeMap metadataValue =
        metadataValue
        |> ScenarioGeneratorHelpers.splitCsv
        |> List.choose (fun entry ->
            match entry.Split([| ':' |], 2, StringSplitOptions.TrimEntries) with
            | [| name; typ |] when not (String.IsNullOrWhiteSpace name) && not (String.IsNullOrWhiteSpace typ) -> Some(name, typ)
            | _ -> None)

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

                        match missingColumn, missingParameter with
                        | Some column, _ -> production.ReportDiagnostic(ScenarioGeneratorHelpers.diagnostic "FSGSQL0002" (sprintf "Selected column '%s' is not present in SqlResultColumns metadata." column) file.Path)
                        | None, Some parameter -> production.ReportDiagnostic(ScenarioGeneratorHelpers.diagnostic "FSGSQL0003" (sprintf "SQL parameter '@%s' is not present in SqlParameterTypes metadata." parameter) file.Path)
                        | None, None ->
                            let ns = ScenarioGeneratorHelpers.namespaceFor "Generated.Data" file
                            let rowType = ScenarioGeneratorHelpers.pascal queryName + "Row"
                            let fields = schema |> List.map (fun (name, typ) -> sprintf "      %s: %s" (ScenarioGeneratorHelpers.pascal name) typ) |> String.concat "\n"
                            let parameterText =
                                parameters file.Text
                                |> List.map (fun name ->
                                    let typ = parameterTypes |> List.find (fun (candidate, _) -> candidate.Equals(name, StringComparison.OrdinalIgnoreCase)) |> snd
                                    sprintf "(%s: %s)" (FSharpGeneratedNames.sanitizeIdentifier name) typ)
                                |> String.concat " "

                            let source =
                                sprintf
                                    "namespace %s\n\ntype %s =\n    {\n%s\n    }\n\nmodule Queries =\n    let %s (_: obj) %s : Async<%s option> = async { return None }\n"
                                    ns
                                    rowType
                                    fields
                                    (queryName |> ScenarioGeneratorHelpers.pascal |> ScenarioGeneratorHelpers.lowerFirst |> FSharpGeneratedNames.sanitizeIdentifier)
                                    parameterText
                                    rowType

                            production.AddImplementationSource(FSharpGeneratedNames.stableHintName "SqlQueryGenerator" file.Path queryName, source, BeforeLastSourceFile))
            )
