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
        if String.IsNullOrWhiteSpace value then
            value
        else
            value.Substring(0, 1).ToLowerInvariant() + value.Substring(1)

    let diagnostic id message path =
        { Id = id
          Message = message
          Severity = FSharpDiagnosticSeverity.Error
          Range = None
          FilePath = Some path }

    let hasKindOrExtension kind extensions (file: FSharpAdditionalFileInput) =
        match file.Kind with
        | Some value when value.Equals(kind, StringComparison.OrdinalIgnoreCase) -> true
        | _ ->
            extensions
            |> List.exists (fun extension -> file.Path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))

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

        if String.IsNullOrWhiteSpace nested then
            current
        else
            nested + "\n\n" + current

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
                    | JsonValueKind.Number when p.Value.TryGetInt32() |> fst ->
                        sprintf "readInt %s element" propertyName
                    | JsonValueKind.Number -> sprintf "readFloat %s element" propertyName
                    | JsonValueKind.True
                    | JsonValueKind.False -> sprintf "readBool %s element" propertyName
                    | JsonValueKind.Object ->
                        sprintf
                            "parse%sElement (readObject %s element)"
                            (ScenarioGeneratorHelpers.pascal p.Name)
                            propertyName
                    | _ -> sprintf "readString %s element" propertyName

                sprintf "            %s = %s" field expression)
            |> String.concat "\n"

        let current =
            sprintf
                "    let rec private parse%sElement (element: JsonElement) : %s =\n        {\n%s\n        }\n"
                typeName
                typeName
                assignments

        nestedParsers + current

    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            let files =
                context.AdditionalFilesProvider
                |> FSharpIncrementalValuesProvider.filter (
                    ScenarioGeneratorHelpers.hasKindOrExtension "json" [ ".json" ]
                )

            context.RegisterSourceOutput(
                files,
                Action<FSharpSourceProductionContext, FSharpAdditionalFileInput>(fun production file ->
                    try
                        use document = JsonDocument.Parse file.Text

                        if document.RootElement.ValueKind <> JsonValueKind.Object then
                            production.ReportDiagnostic(
                                ScenarioGeneratorHelpers.diagnostic
                                    "FSGJSON0002"
                                    "JSON root must be an object."
                                    file.Path
                            )
                        else
                            match validate "$" document.RootElement with
                            | Some message ->
                                production.ReportDiagnostic(
                                    ScenarioGeneratorHelpers.diagnostic "FSGJSON0003" message file.Path
                                )
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

                                production.AddImplementationSource(
                                    FSharpGeneratedNames.stableHintName "JsonConfigGenerator" file.Path rootType,
                                    source,
                                    BeforeLastSourceFile
                                )
                    with ex ->
                        production.ReportDiagnostic(
                            ScenarioGeneratorHelpers.diagnostic "FSGJSON0001" ("Invalid JSON: " + ex.Message) file.Path
                        ))
            )

type private OpenApiDtoField =
    { JsonName: string
      FieldName: string
      TypeName: string }

type private OpenApiDto =
    { TypeName: string
      Fields: OpenApiDtoField list }

type private OpenApiPathParameter =
    { Placeholder: string
      ParameterName: string }

type private OpenApiOperation =
    { OperationId: string
      MethodName: string
      Path: string
      PathParameters: OpenApiPathParameter list
      ReturnType: string }

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
                | Some format when format.ValueKind = JsonValueKind.String && format.GetString() = "int64" -> Ok "int64"
                | _ -> Ok "int"
            | "number" -> Ok "float"
            | "boolean" -> Ok "bool"
            | "string" -> Ok "string"
            | other -> Error(sprintf "Unsupported OpenAPI schema type '%s'." other)
        | _ -> Error "OpenAPI schemas must declare a string type."

    let schemaNameFromRef (refValue: string) =
        let marker = "#/components/schemas/"

        if refValue.StartsWith(marker, StringComparison.Ordinal) then
            Some(refValue.Substring(marker.Length) |> ScenarioGeneratorHelpers.pascal)
        else
            None

    let responseSchemaName (operation: JsonElement) =
        tryProperty "responses" operation
        |> Option.bind (tryProperty "200")
        |> Option.bind (tryProperty "content")
        |> Option.bind (tryProperty "application/json")
        |> Option.bind (tryProperty "schema")
        |> Option.bind (tryProperty "$ref")
        |> Option.bind (fun value ->
            if value.ValueKind = JsonValueKind.String then
                schemaNameFromRef (value.GetString())
            else
                None)

    let operationName (operation: JsonElement) =
        tryProperty "operationId" operation
        |> Option.bind (fun operationId ->
            if operationId.ValueKind = JsonValueKind.String then
                Some(operationId.GetString())
            else
                None)

    let pathParameters (path: string) =
        Regex.Matches(path, @"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}")
        |> Seq.cast<Match>
        |> Seq.map (fun m ->
            let name = m.Groups["name"].Value

            { Placeholder = "{" + name + "}"
              ParameterName = FSharpGeneratedNames.sanitizeIdentifier name })
        |> Seq.toList

    let parseDto (schemaName: string, schema: JsonElement) =
        match tryProperty "properties" schema with
        | Some properties when properties.ValueKind = JsonValueKind.Object ->
            let fields = ResizeArray<OpenApiDtoField>()
            let errors = ResizeArray<string>()

            for property in properties.EnumerateObject() do
                match schemaType property.Value with
                | Ok typ ->
                    fields.Add
                        { JsonName = property.Name
                          FieldName = ScenarioGeneratorHelpers.pascal property.Name
                          TypeName = typ }
                | Error message -> errors.Add(sprintf "Schema '%s' property '%s': %s" schemaName property.Name message)

            if errors.Count > 0 then
                Error(Seq.toList errors)
            else
                Ok
                    { TypeName = ScenarioGeneratorHelpers.pascal schemaName
                      Fields = Seq.toList fields }
        | _ -> Error [ sprintf "Schema '%s' must be an object schema with properties." schemaName ]

    let parseOperation (path: string) (operation: JsonElement) =
        match operationName operation with
        | None -> Error(sprintf "OpenAPI operation at path '%s' must declare a string operationId." path)
        | Some operationId ->
            if tryProperty "requestBody" operation |> Option.isSome then
                Error(
                    sprintf
                        "Operation '%s' declares requestBody, but request body generation is not supported. Supported operations are path-parameter GET-style calls returning 200 application/json."
                        operationId
                )
            else
                match responseSchemaName operation with
                | None ->
                    Error(
                        sprintf
                            "Operation '%s' must declare a 200 application/json response schema reference."
                            operationId
                    )
                | Some returnType ->
                    Ok
                        { OperationId = operationId
                          MethodName = operationId |> ScenarioGeneratorHelpers.pascal
                          Path = path
                          PathParameters = pathParameters path
                          ReturnType = returnType }

    let parseDocument (document: JsonDocument) =
        let errors = ResizeArray<string>()
        let dtos = ResizeArray<OpenApiDto>()
        let operations = ResizeArray<OpenApiOperation>()
        let operationIds = ResizeArray<string>()

        match
            tryProperty "components" document.RootElement
            |> Option.bind (tryProperty "schemas")
        with
        | Some schemas when schemas.ValueKind = JsonValueKind.Object ->
            for schema in schemas.EnumerateObject() do
                match parseDto (schema.Name, schema.Value) with
                | Ok dto -> dtos.Add dto
                | Error messages -> errors.AddRange messages
        | _ -> errors.Add "OpenAPI document must contain components.schemas object."

        match tryProperty "paths" document.RootElement with
        | Some paths when paths.ValueKind = JsonValueKind.Object ->
            for path in paths.EnumerateObject() do
                if path.Value.ValueKind = JsonValueKind.Object then
                    for method in path.Value.EnumerateObject() do
                        match operationName method.Value with
                        | Some operationId -> operationIds.Add operationId
                        | None -> ()

                        match parseOperation path.Name method.Value with
                        | Ok operation -> operations.Add operation
                        | Error message -> errors.Add message
                else
                    errors.Add(sprintf "OpenAPI path '%s' must contain operation objects." path.Name)
        | _ -> errors.Add "OpenAPI document must contain a paths object."

        match
            operationIds
            |> Seq.groupBy id
            |> Seq.tryFind (fun (_, values) -> Seq.length values > 1)
        with
        | Some(operationId, _) -> errors.Add(sprintf "Duplicate operationId '%s'." operationId)
        | None -> ()

        if errors.Count > 0 then
            Error(Seq.toList errors)
        else
            Ok(Seq.toList dtos, Seq.toList operations)

    let emitDto dto =
        let fields =
            dto.Fields
            |> List.map (fun field -> sprintf "      %s: %s" field.FieldName field.TypeName)
            |> String.concat "\n"

        sprintf "type %s =\n    {\n%s\n    }" dto.TypeName fields

    let emitOperation operation =
        let parameters =
            match operation.PathParameters with
            | [] -> "()"
            | values ->
                values
                |> List.map (fun parameter -> sprintf "(%s: int64)" parameter.ParameterName)
                |> String.concat " "

        let replacements =
            operation.PathParameters
            |> List.map (fun parameter ->
                sprintf
                    "                |> replacePathParameter %s %s"
                    (ScenarioGeneratorHelpers.fsharpStringLiteral parameter.Placeholder)
                    parameter.ParameterName)
            |> String.concat "\n"

        let pathExpression =
            if String.IsNullOrWhiteSpace replacements then
                sprintf "let path = %s" (ScenarioGeneratorHelpers.fsharpStringLiteral operation.Path)
            else
                sprintf
                    "let path =\n                %s\n%s"
                    (ScenarioGeneratorHelpers.fsharpStringLiteral operation.Path)
                    replacements

        sprintf
            "    member this.%s %s : Async<%s> =\n        async {\n            %s\n            let! json = send path\n            return this.Deserialize<%s> json\n        }\n"
            operation.MethodName
            parameters
            operation.ReturnType
            pathExpression
            operation.ReturnType

    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            let files =
                context.AdditionalFilesProvider
                |> FSharpIncrementalValuesProvider.filter (
                    ScenarioGeneratorHelpers.hasKindOrExtension "openapi" [ ".openapi.json" ]
                )

            context.RegisterSourceOutput(
                files,
                Action<FSharpSourceProductionContext, FSharpAdditionalFileInput>(fun production file ->
                    if
                        file.Path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                        || file.Path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                    then
                        production.ReportDiagnostic(
                            ScenarioGeneratorHelpers.diagnostic
                                "FSGOPENAPI0003"
                                "OpenAPI YAML input is not supported by this generator; use JSON input."
                                file.Path
                        )
                    else
                        try
                            use document = JsonDocument.Parse file.Text

                            match parseDocument document with
                            | Error messages ->
                                let id =
                                    if
                                        messages
                                        |> List.exists (fun message ->
                                            message.StartsWith("Duplicate operationId", StringComparison.Ordinal))
                                    then
                                        "FSGOPENAPI0002"
                                    else
                                        "FSGOPENAPI0004"

                                production.ReportDiagnostic(
                                    ScenarioGeneratorHelpers.diagnostic id (String.concat " " messages) file.Path
                                )
                            | Ok(dtos, operations) ->
                                let ns = ScenarioGeneratorHelpers.namespaceFor "Generated.Api" file

                                let clientName =
                                    ScenarioGeneratorHelpers.typeNameFrom "OpenApiClientName" "OpenApiClient" file

                                let dtoSource = dtos |> List.map emitDto |> String.concat "\n\n"
                                let operationSource = operations |> List.map emitOperation |> String.concat "\n"

                                let source =
                                    sprintf
                                        "namespace %s\n\nopen System\nopen System.Text.Json\n\n%s\n\ntype %s(send: string -> Async<string>) =\n    static let serializerOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)\n\n    let replacePathParameter placeholder value (path: string) =\n        path.Replace(placeholder, Uri.EscapeDataString(string value))\n\n    member private _.Deserialize<'T>(json: string) : 'T =\n        let value = JsonSerializer.Deserialize<'T>(json, serializerOptions)\n        if obj.ReferenceEquals(box value, null) then invalidOp \"OpenAPI response deserialized to null.\" else value\n\n%s"
                                        ns
                                        dtoSource
                                        clientName
                                        operationSource

                                production.AddImplementationSource(
                                    FSharpGeneratedNames.stableHintName "OpenApiClientGenerator" file.Path clientName,
                                    source,
                                    BeforeLastSourceFile
                                )
                        with ex ->
                            production.ReportDiagnostic(
                                ScenarioGeneratorHelpers.diagnostic
                                    "FSGOPENAPI0001"
                                    ("Invalid OpenAPI JSON: " + ex.Message)
                                    file.Path
                            ))
            )

[<FSharpGenerator>]
type NativeHeaderBindingGenerator() =
    let functionPattern =
        Regex(
            @"^\s*(?<ret>void|char|short|int|long|float|double)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<args>[^)]*)\)\s*;\s*$",
            RegexOptions.Compiled
        )

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
            let m =
                Regex.Match(
                    trimmed,
                    @"^(?<type>const\s+char\s*\*|char\s*\*|void\s*\*|char|short|int|long|float|double)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)$"
                )

            if not m.Success then
                Error(
                    sprintf
                        "Unsupported C argument '%s'. Supported arguments are primitive scalars, const char*, char*, and void*."
                        trimmed
                )
            else
                let ctype =
                    Regex.Replace(m.Groups["type"].Value, @"\s+", " ").Replace(" *", "*").Trim()

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
                |> FSharpIncrementalValuesProvider.filter (
                    ScenarioGeneratorHelpers.hasKindOrExtension "c-header" [ ".h" ]
                )

            context.RegisterSourceOutput(
                files,
                Action<FSharpSourceProductionContext, FSharpAdditionalFileInput>(fun production file ->
                    match FSharpAdditionalFileInput.requireMetadata "NativeLibraryName" file with
                    | Error message ->
                        production.ReportDiagnostic(
                            ScenarioGeneratorHelpers.diagnostic "FSGNATIVE0002" message file.Path
                        )
                    | Ok library ->
                        let functions = ResizeArray<string>()
                        let errors = ResizeArray<string>()

                        for line in file.Text.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries) do
                            let m = functionPattern.Match line

                            if m.Success then
                                let parsedArgs =
                                    m.Groups["args"].Value.Split([| ',' |], StringSplitOptions.RemoveEmptyEntries)
                                    |> Array.map parseArg

                                let argErrors =
                                    parsedArgs
                                    |> Array.choose (function
                                        | Error message -> Some message
                                        | Ok _ -> None)

                                if argErrors.Length > 0 then
                                    errors.AddRange argErrors
                                else
                                    let args =
                                        parsedArgs
                                        |> Array.choose (function
                                            | Ok(Some arg) -> Some arg
                                            | _ -> None)

                                    let argsText =
                                        args
                                        |> Array.map (fun (typ, name) -> sprintf "%s %s" typ name)
                                        |> String.concat ", "

                                    let returnType =
                                        mapPrimitive m.Groups["ret"].Value |> Option.defaultValue "nativeint"

                                    let libraryLiteral = ScenarioGeneratorHelpers.fsharpStringLiteral library

                                    let entryPointLiteral =
                                        ScenarioGeneratorHelpers.fsharpStringLiteral m.Groups["name"].Value

                                    functions.Add(
                                        sprintf
                                            "    [<System.Runtime.InteropServices.DllImport(%s, EntryPoint = %s)>]\n    extern %s %s(%s)"
                                            libraryLiteral
                                            entryPointLiteral
                                            returnType
                                            (ScenarioGeneratorHelpers.pascal m.Groups["name"].Value)
                                            argsText
                                    )
                            else
                                errors.Add(
                                    "Unsupported C declaration: "
                                    + line.Trim()
                                    + ". Supported declarations are simple extern-style function prototypes with primitive scalar return types."
                                )

                        if errors.Count > 0 then
                            production.ReportDiagnostic(
                                ScenarioGeneratorHelpers.diagnostic
                                    "FSGNATIVE0001"
                                    (String.concat " " errors)
                                    file.Path
                            )
                        else
                            let ns = ScenarioGeneratorHelpers.namespaceFor "Generated.Native" file

                            let moduleName =
                                ScenarioGeneratorHelpers.typeNameFrom "NativeModuleName" "Native" file

                            let source =
                                sprintf
                                    "namespace %s\n\nmodule %s =\n%s\n"
                                    ns
                                    moduleName
                                    (String.concat "\n\n" functions)

                            production.AddImplementationSource(
                                FSharpGeneratedNames.stableHintName "NativeHeaderBindingGenerator" file.Path moduleName,
                                source,
                                BeforeLastSourceFile
                            ))
            )

type private SqlType =
    | SqlInt
    | SqlInt64
    | SqlFloat
    | SqlFloat32
    | SqlBool
    | SqlString
    | SqlDecimal
    | SqlOption of SqlType

type private SqlColumn =
    { SqlName: string
      FSharpName: string
      Type: SqlType }

type private SqlQueryModel =
    { QueryName: string
      ModuleName: string
      RowTypeName: string
      CommandText: string
      Columns: SqlColumn list
      Parameters: SqlColumn list }

[<FSharpGenerator>]
type SqlQueryGenerator() =
    let tryCommentValue key (text: string) =
        text.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.tryPick (fun line ->
            let prefix = "-- " + key + ":"
            let trimmed = line.Trim()

            if trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
                Some(trimmed.Substring(prefix.Length).Trim())
            else
                None)

    let rec parseSqlType (value: string) =
        let normalized = value.Trim().ToLowerInvariant()

        if normalized.EndsWith(" option", StringComparison.Ordinal) then
            parseSqlType (normalized.Substring(0, normalized.Length - " option".Length))
            |> Result.map SqlOption
        else
            match normalized with
            | "int" -> Ok SqlInt
            | "int64" -> Ok SqlInt64
            | "float" -> Ok SqlFloat
            | "float32" -> Ok SqlFloat32
            | "bool" -> Ok SqlBool
            | "string" -> Ok SqlString
            | "decimal" -> Ok SqlDecimal
            | _ -> Error(sprintf "SQL type '%s' is not supported by this generator." value)

    let rec renderSqlType sqlType =
        match sqlType with
        | SqlInt -> "int"
        | SqlInt64 -> "int64"
        | SqlFloat -> "float"
        | SqlFloat32 -> "float32"
        | SqlBool -> "bool"
        | SqlString -> "string"
        | SqlDecimal -> "decimal"
        | SqlOption inner -> renderSqlType inner + " option"

    let rec renderSqlValue expression sqlType =
        match sqlType with
        | SqlInt -> sprintf "SqlValue.Int %s" expression
        | SqlInt64 -> sprintf "SqlValue.Int64 %s" expression
        | SqlFloat -> sprintf "SqlValue.Float %s" expression
        | SqlFloat32 -> sprintf "SqlValue.Float32 %s" expression
        | SqlBool -> sprintf "SqlValue.Bool %s" expression
        | SqlString -> sprintf "SqlValue.String %s" expression
        | SqlDecimal -> sprintf "SqlValue.Decimal %s" expression
        | SqlOption inner ->
            sprintf "(match %s with Some value -> %s | None -> SqlValue.Null)" expression (renderSqlValue "value" inner)

    let parseTypeMap metadataName metadataValue =
        let columns = ResizeArray<SqlColumn>()
        let errors = ResizeArray<string>()

        for entry in ScenarioGeneratorHelpers.splitCsv metadataValue do
            match entry.Split([| ':' |], 2, StringSplitOptions.TrimEntries) with
            | [| name; typ |] when not (String.IsNullOrWhiteSpace name) && not (String.IsNullOrWhiteSpace typ) ->
                match parseSqlType typ with
                | Ok sqlType ->
                    columns.Add
                        { SqlName = name
                          FSharpName = ScenarioGeneratorHelpers.pascal name
                          Type = sqlType }
                | Error message -> errors.Add message
            | _ -> errors.Add(sprintf "%s entry '%s' must use name:type syntax." metadataName entry)

        if errors.Count > 0 then
            Error(Seq.toList errors)
        else
            Ok(Seq.toList columns)

    let selectedColumns (text: string) =
        let normalized = text.Replace("\r", " ").Replace("\n", " ")
        let selectIndex = normalized.IndexOf("select", StringComparison.OrdinalIgnoreCase)
        let fromIndex = normalized.IndexOf("from", StringComparison.OrdinalIgnoreCase)

        if selectIndex >= 0 && fromIndex > selectIndex then
            normalized.Substring(selectIndex + 6, fromIndex - selectIndex - 6)
            |> ScenarioGeneratorHelpers.splitCsv
            |> List.map (fun column ->
                column.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.last
                |> fun value -> value.Trim('"', '[', ']'))
        else
            []

    let parameters text =
        Regex.Matches(text, @"@[A-Za-z_][A-Za-z0-9_]*")
        |> Seq.cast<Match>
        |> Seq.map (fun m -> m.Value.Substring(1))
        |> Seq.distinct
        |> Seq.toList

    let parseQuery file =
        match tryCommentValue "name" file.Text with
        | None -> Error("FSGSQL0001", "SQL file must declare '-- name: QueryName'.")
        | Some queryName ->
            match tryCommentValue "result" file.Text with
            | Some result when not (result.Equals("one", StringComparison.OrdinalIgnoreCase)) ->
                Error(
                    "FSGSQL0005",
                    sprintf "SQL result cardinality '%s' is not supported. Supported cardinality: one." result
                )
            | _ ->
                let schemaResult =
                    ScenarioGeneratorHelpers.tryMetadata "SqlResultColumns" file
                    |> Option.map (parseTypeMap "SqlResultColumns")
                    |> Option.defaultValue (Ok [])

                let parameterResult =
                    ScenarioGeneratorHelpers.tryMetadata "SqlParameterTypes" file
                    |> Option.map (parseTypeMap "SqlParameterTypes")
                    |> Option.defaultValue (Ok [])

                match schemaResult, parameterResult with
                | Error messages, _
                | _, Error messages -> Error("FSGSQL0004", String.concat " " messages)
                | Ok schema, Ok parameterTypes ->
                    let missingColumn =
                        selectedColumns file.Text
                        |> List.tryFind (fun column ->
                            schema
                            |> List.exists (fun known ->
                                known.SqlName.Equals(column, StringComparison.OrdinalIgnoreCase))
                            |> not)

                    let missingParameter =
                        parameters file.Text
                        |> List.tryFind (fun parameter ->
                            parameterTypes
                            |> List.exists (fun known ->
                                known.SqlName.Equals(parameter, StringComparison.OrdinalIgnoreCase))
                            |> not)

                    match missingColumn, missingParameter with
                    | Some column, _ ->
                        Error(
                            "FSGSQL0002",
                            sprintf "Selected column '%s' is not present in SqlResultColumns metadata." column
                        )
                    | None, Some parameter ->
                        Error(
                            "FSGSQL0003",
                            sprintf "SQL parameter '@%s' is not present in SqlParameterTypes metadata." parameter
                        )
                    | None, None ->
                        Ok
                            { QueryName = queryName
                              ModuleName = ScenarioGeneratorHelpers.pascal queryName
                              RowTypeName = "Row"
                              CommandText = file.Text
                              Columns = schema
                              Parameters =
                                parameters file.Text
                                |> List.map (fun name ->
                                    parameterTypes
                                    |> List.find (fun known ->
                                        known.SqlName.Equals(name, StringComparison.OrdinalIgnoreCase))) }

    let emitQuery model =
        let fields =
            model.Columns
            |> List.map (fun column -> sprintf "          %s: %s" column.FSharpName (renderSqlType column.Type))
            |> String.concat "\n"

        let parameterFields =
            model.Parameters
            |> List.map (fun parameter ->
                sprintf "          %s: %s" parameter.FSharpName (renderSqlType parameter.Type))
            |> String.concat "\n"

        let parameterValues =
            model.Parameters
            |> List.map (fun parameter ->
                sprintf
                    "                { Name = %s\n                  Value = %s }"
                    (ScenarioGeneratorHelpers.fsharpStringLiteral parameter.SqlName)
                    (renderSqlValue ("parameters." + parameter.FSharpName) parameter.Type))
            |> String.concat "\n"

        let rowAssignments =
            model.Columns
            |> List.map (fun column ->
                sprintf
                    "              %s = read<%s> %s row"
                    column.FSharpName
                    (renderSqlType column.Type)
                    (ScenarioGeneratorHelpers.fsharpStringLiteral column.SqlName))
            |> String.concat "\n"

        sprintf
            "module %s =\n    [<RequireQualifiedAccess>]\n    type Parameters =\n        {\n%s\n        }\n\n    type %s =\n        {\n%s\n        }\n\n    let commandText =\n        %s\n\n    let private read<'T> name (row: SqlRowReader) : 'T =\n        match row.TryFind name with\n        | Some value -> unbox<'T> value\n        | None -> invalidArg name (\"SQL result row did not contain column '\" + name + \"'.\")\n\n    let private mapRow (row: SqlRowReader) : %s =\n        {\n%s\n        }\n\n    let execute (database: SqlExecutor) (parameters: Parameters) : Async<%s option> =\n        database.QuerySingle(\n            commandText,\n            [\n%s\n            ],\n            mapRow)\n"
            model.ModuleName
            parameterFields
            model.RowTypeName
            fields
            (ScenarioGeneratorHelpers.fsharpStringLiteral model.CommandText)
            model.RowTypeName
            rowAssignments
            model.RowTypeName
            parameterValues

    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            let files =
                context.AdditionalFilesProvider
                |> FSharpIncrementalValuesProvider.filter (ScenarioGeneratorHelpers.hasKindOrExtension "sql" [ ".sql" ])

            context.RegisterSourceOutput(
                files,
                Action<FSharpSourceProductionContext, FSharpAdditionalFileInput>(fun production file ->
                    match parseQuery file with
                    | Error(id, message) ->
                        production.ReportDiagnostic(ScenarioGeneratorHelpers.diagnostic id message file.Path)
                    | Ok model ->
                        let ns = ScenarioGeneratorHelpers.namespaceFor "Generated.Data" file

                        let source =
                            sprintf
                                "namespace %s\n\n[<RequireQualifiedAccess>]\ntype SqlValue =\n    | Int of int\n    | Int64 of int64\n    | Float of float\n    | Float32 of float32\n    | Bool of bool\n    | String of string\n    | Decimal of decimal\n    | Null\n\ntype SqlParameter =\n    { Name: string\n      Value: SqlValue }\n\ntype SqlRowReader = Map<string, obj>\n\ntype SqlExecutor =\n    abstract QuerySingle<'Row> : commandText: string * parameters: SqlParameter list * map: (SqlRowReader -> 'Row) -> Async<'Row option>\n\n%s"
                                ns
                                (emitQuery model)

                        production.AddImplementationSource(
                            FSharpGeneratedNames.stableHintName "SqlQueryGenerator" file.Path model.QueryName,
                            source,
                            BeforeLastSourceFile
                        ))
            )
