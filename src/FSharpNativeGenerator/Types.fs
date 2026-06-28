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

type FSharpAnalyzerConfigOptions =
    { GlobalOptions: IReadOnlyDictionary<string, string>
      GetOptionsForPath: string -> IReadOnlyDictionary<string, string> }

type FSharpSourceGeneratorConfiguration =
    { GeneratorPaths: string list
      AdditionalFilePaths: string list
      AnalyzerConfigPaths: string list }

type IFSharpIncrementalGeneratorWithId =
    abstract GeneratorId: string
