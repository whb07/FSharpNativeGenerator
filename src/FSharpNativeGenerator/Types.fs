namespace FSharp.Compiler.SourceGeneration

open System
open System.Collections.Generic
open System.Threading
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text

module FSharpGeneratorApiVersion =
    [<Literal>]
    let Current = 1

[<AttributeUsage(AttributeTargets.Class, AllowMultiple = false)>]
type FSharpGeneratorAttribute(apiVersion: int) =
    inherit Attribute()

    new() = FSharpGeneratorAttribute(FSharpGeneratorApiVersion.Current)

    member _.ApiVersion = apiVersion

module internal FSharpGeneratorAttributeHelpers =
    let tryGet (candidate: Type) =
        candidate.GetCustomAttributes(typeof<FSharpGeneratorAttribute>, false)
        |> Seq.cast<FSharpGeneratorAttribute>
        |> Seq.tryHead

    let isSupportedApiVersion (attribute: FSharpGeneratorAttribute) =
        attribute.ApiVersion = FSharpGeneratorApiVersion.Current

type FSharpGeneratedSourcePlacement =
    | Prelude
    | BeforeFile of anchorPath: string
    | AfterFile of anchorPath: string
    | BeforeLastSourceFile
    | EndOfProject

type FSharpGeneratorDiagnostic =
    { Id: string
      Message: string
      Severity: FSharpDiagnosticSeverity
      Range: range option
      FilePath: string option }

module FSharpGeneratorDiagnostic =
    let create id message severity =
        { Id = id
          Message = message
          Severity = severity
          Range = None
          FilePath = None }

    let toSourceGeneratorDiagnostic diagnostic : FSharpSourceGeneratorDiagnostic =
        { Id = diagnostic.Id
          Message = diagnostic.Message
          Severity = diagnostic.Severity
          Range = diagnostic.Range }

type FSharpGeneratorProjectSnapshot =
    { ProjectFileName: string option
      ProjectDirectory: string
      SourceFiles: string list
      OtherOptions: string list
      References: string list
      DefineConstants: string list
      OutputFile: string option
      AssemblyName: string option }

type FSharpSourceFileInput =
    { Path: string
      IsSignatureFile: bool }

type FSharpAdditionalTextInput =
    { Path: string
      Text: string }

type FSharpAdditionalFileInput =
    { Path: string
      Text: string
      LogicalName: string option
      Kind: string option
      Metadata: IReadOnlyDictionary<string, string>
      Options: IReadOnlyDictionary<string, string> }

[<RequireQualifiedAccess>]
module FSharpAdditionalFileInput =
    let tryGetMetadata key (file: FSharpAdditionalFileInput) =
        match file.Metadata.TryGetValue key with
        | true, value when not (String.IsNullOrWhiteSpace value) -> Some value
        | _ -> None

    let requireMetadata key (file: FSharpAdditionalFileInput) =
        match tryGetMetadata key file with
        | Some value -> Ok value
        | None -> Error(sprintf "Additional file '%s' is missing required metadata '%s'." file.Path key)

    let tryGetOption key (file: FSharpAdditionalFileInput) =
        match file.Options.TryGetValue key with
        | true, value when not (String.IsNullOrWhiteSpace value) -> Some value
        | _ -> None

    let logicalNameOrFileName (file: FSharpAdditionalFileInput) =
        file.LogicalName
        |> Option.defaultWith (fun () ->
            let name = System.IO.Path.GetFileNameWithoutExtension file.Path
            if String.IsNullOrWhiteSpace name then "Generated" else name)

    let namespaceOrDefault defaultNamespace (file: FSharpAdditionalFileInput) =
        tryGetMetadata "FSharpGeneratorNamespace" file
        |> Option.defaultValue defaultNamespace

    let moduleOrDefault defaultModuleName (file: FSharpAdditionalFileInput) =
        tryGetMetadata "FSharpGeneratorModule" file
        |> Option.defaultValue defaultModuleName

type FSharpAnalyzerConfigOptions =
    { GlobalOptions: IReadOnlyDictionary<string, string>
      GetOptionsForPath: string -> IReadOnlyDictionary<string, string> }

type FSharpSourceGeneratorConfiguration =
    { GeneratorPaths: string list
      AdditionalFilePaths: string list
      AnalyzerConfigPaths: string list }

type IFSharpIncrementalGeneratorWithId =
    abstract GeneratorId: string

[<RequireQualifiedAccess>]
module FSharpGeneratedNames =
    let private keywords =
        set
            [ "abstract"; "and"; "as"; "assert"; "base"; "begin"; "class"; "default"; "delegate"; "do"; "done"; "downcast"; "downto"
              "elif"; "else"; "end"; "exception"; "extern"; "false"; "finally"; "fixed"; "for"; "fun"; "function"; "global"; "if"
              "in"; "inherit"; "inline"; "interface"; "internal"; "lazy"; "let"; "match"; "member"; "module"; "mutable"; "namespace"
              "new"; "not"; "null"; "of"; "open"; "or"; "override"; "private"; "public"; "rec"; "return"; "sig"; "static"; "struct"
              "then"; "to"; "true"; "try"; "type"; "upcast"; "use"; "val"; "void"; "when"; "while"; "with"; "yield" ]

    let private isIdentifierStart ch =
        Char.IsLetter ch || ch = '_'

    let private isIdentifierPart ch =
        Char.IsLetterOrDigit ch || ch = '_' || ch = '\''

    let sanitizeIdentifier (value: string) =
        let source = if String.IsNullOrWhiteSpace value then "_" else value.Trim()

        let chars =
            source
            |> Seq.mapi (fun index ch ->
                if index = 0 then
                    if isIdentifierStart ch then ch else '_'
                elif isIdentifierPart ch then
                    ch
                else
                    '_')
            |> Seq.toArray

        let identifier = String(chars)

        if keywords.Contains identifier then
            identifier + "_"
        else
            identifier

    let sanitizeModuleName (value: string) =
        let parts =
            value.Split([| '.'; '-'; ' '; '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map sanitizeIdentifier
            |> Array.filter (String.IsNullOrWhiteSpace >> not)

        if parts.Length = 0 then "Generated" else String.concat "." parts

    let stableHintName (generatorId: string) (inputPath: string) (suffix: string) =
        use sha = System.Security.Cryptography.SHA256.Create()
        let input = generatorId + "\n" + inputPath
        let hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes input)
        let shortHash = hash |> Array.take 8 |> Array.map (fun b -> b.ToString("x2")) |> String.concat ""
        let baseName =
            let name = System.IO.Path.GetFileNameWithoutExtension inputPath
            if String.IsNullOrWhiteSpace name then "Generated" else sanitizeIdentifier name

        let sanitizedSuffix = if String.IsNullOrWhiteSpace suffix then "Generated" else sanitizeIdentifier suffix
        sprintf "%s_%s_%s" baseName sanitizedSuffix shortHash
