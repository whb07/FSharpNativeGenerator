namespace FSharp.Compiler.SourceGeneration

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading

type FSharpDiagnosticSeverity =
    | Info
    | Warning
    | Error

type SourceRange =
    {
        FilePath: string
        StartLine: int
        StartColumn: int
        EndLine: int
        EndColumn: int
    }

type FSharpGeneratorDiagnostic =
    {
        Id: string
        Message: string
        Severity: FSharpDiagnosticSeverity
        Range: SourceRange option
        FilePath: string option
    }

module FSharpGeneratorDiagnostic =
    let create id message severity =
        {
            Id = id
            Message = message
            Severity = severity
            Range = None
            FilePath = None
        }

type FSharpGeneratedSourceKind =
    | Implementation
    | Signature

type FSharpGeneratedSourcePlacement =
    | Prelude
    | BeforeFile of anchorPath: string
    | AfterFile of anchorPath: string
    | BeforeLastSourceFile
    | EndOfProject

type FSharpGeneratorHostKind =
    | CommandLine
    | MSBuild
    | IDE

type FSharpOutputKind =
    | Library
    | Application

type FSharpSourceText =
    private
        {
            Content: string
        }

    member this.Text = this.Content
    override this.ToString() = this.Content

    static member OfString(value: string) =
        {
            Content = if isNull value then "" else value
        }

module internal Hashing =
    let sha256Bytes (text: string) =
        use sha = SHA256.Create()
        text
        |> Encoding.UTF8.GetBytes
        |> sha.ComputeHash
        |> ImmutableArray.CreateRange

    let sha256Many (parts: string seq) =
        parts
        |> String.concat "\u001f"
        |> sha256Bytes

    let toHex (bytes: ImmutableArray<byte>) =
        bytes |> Seq.toArray |> Convert.ToHexString

module FSharpSourceText =
    let checksum (sourceText: FSharpSourceText) =
        Hashing.sha256Bytes sourceText.Text

type FSharpProjectOptions =
    {
        ProjectFilePath: string
        ProjectId: string option
        SourceFiles: ImmutableArray<string>
        OtherOptions: ImmutableArray<string>
        OutputKind: FSharpOutputKind
        Stamp: string option
    }

type FSharpSourceFileSnapshot =
    {
        Path: string
        SourceText: FSharpSourceText
        Checksum: ImmutableArray<byte>
        IsSignatureFile: bool
    }

module FSharpSourceFileSnapshot =
    let create path text =
        let normalizedPath = Path.GetFullPath(path)
        let sourceText = FSharpSourceText.OfString text

        {
            Path = normalizedPath
            SourceText = sourceText
            Checksum = FSharpSourceText.checksum sourceText
            IsSignatureFile = normalizedPath.EndsWith(".fsi", StringComparison.OrdinalIgnoreCase)
        }

type FSharpAdditionalText =
    {
        Path: string
        GetText: CancellationToken -> FSharpSourceText option
        Checksum: ImmutableArray<byte> option
    }

module FSharpAdditionalText =
    let fromFile path =
        let fullPath = Path.GetFullPath(path)

        {
            Path = fullPath
            GetText =
                fun cancellationToken ->
                    if File.Exists fullPath then
                        cancellationToken.ThrowIfCancellationRequested()
                        File.ReadAllText fullPath |> FSharpSourceText.OfString |> Some
                    else
                        None
            Checksum =
                if File.Exists fullPath then
                    File.ReadAllText fullPath |> Hashing.sha256Bytes |> Some
                else
                    None
        }

type FSharpAnalyzerConfigOptions =
    {
        GlobalOptions: IReadOnlyDictionary<string, string>
        GetOptionsForPath: string -> IReadOnlyDictionary<string, string>
    }

type FSharpAnalyzerConfigOptionsLoadResult =
    {
        Options: FSharpAnalyzerConfigOptions
        Diagnostics: ImmutableArray<FSharpGeneratorDiagnostic>
    }

type FSharpGeneratorProjectSnapshot =
    {
        ProjectOptions: FSharpProjectOptions
        SourceFiles: ImmutableArray<FSharpSourceFileSnapshot>
        AdditionalTexts: ImmutableArray<FSharpAdditionalText>
        AnalyzerConfigOptions: FSharpAnalyzerConfigOptions
    }

type FSharpGeneratedSource =
    {
        GeneratorName: string
        HintName: string
        ResolvedPath: string
        Kind: FSharpGeneratedSourceKind
        SourceText: FSharpSourceText
        Placement: FSharpGeneratedSourcePlacement
        Checksum: ImmutableArray<byte>
    }

[<AttributeUsage(AttributeTargets.Class, AllowMultiple = false)>]
type FSharpGeneratorAttribute() =
    inherit Attribute()

type FSharpGeneratorDriverOptions =
    {
        EmitGeneratedFiles: bool
        GeneratedFilesOutputPath: string option
        ReportPath: string option
        MaxGenerationPasses: int
        HostKind: FSharpGeneratorHostKind
        GeneratedRoot: string
    }

module FSharpGeneratorDriverOptions =
    let defaults =
        {
            EmitGeneratedFiles = false
            GeneratedFilesOutputPath = None
            ReportPath = None
            MaxGenerationPasses = 1
            HostKind = CommandLine
            GeneratedRoot = Path.Combine("obj", "Generated", "FSharp")
        }

type FSharpGeneratedSourceStore =
    private
        {
            Sources: ImmutableDictionary<string, FSharpSourceText>
        }

    member this.TryGetText(path: string) =
        match this.Sources.TryGetValue(Path.GetFullPath(path)) with
        | true, value -> Some value
        | false, _ -> None

    member this.Paths = this.Sources.Keys

    static member Empty =
        {
            Sources = ImmutableDictionary<string, FSharpSourceText>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase)
        }

    member this.Add(source: FSharpGeneratedSource) =
        {
            Sources = this.Sources.SetItem(Path.GetFullPath(source.ResolvedPath), source.SourceText)
        }

type FSharpGeneratorDriverRunResult =
    {
        GeneratedSources: ImmutableArray<FSharpGeneratedSource>
        Diagnostics: ImmutableArray<FSharpGeneratorDiagnostic>
        UpdatedSourceFiles: ImmutableArray<string>
        GeneratedSourceStore: FSharpGeneratedSourceStore
        ElapsedMilliseconds: int64
        CacheHit: bool
    }

type FSharpGeneratedSourceReport =
    {
        GeneratorName: string
        HintName: string
        ResolvedPath: string
        Kind: string
        Placement: string
        Checksum: string
    }

type FSharpGeneratorDiagnosticReport =
    {
        Id: string
        Message: string
        Severity: string
        FilePath: string option
    }

type FSharpGeneratorRunReport =
    {
        GeneratedSources: ImmutableArray<FSharpGeneratedSourceReport>
        Diagnostics: ImmutableArray<FSharpGeneratorDiagnosticReport>
        UpdatedSourceFiles: ImmutableArray<string>
        ElapsedMilliseconds: int64
        CacheHit: bool
    }

type FSharpSourceGeneratorConfiguration =
    {
        GeneratorPaths: ImmutableArray<string>
        AdditionalFilePaths: ImmutableArray<string>
        AnalyzerConfigPaths: ImmutableArray<string>
        DriverOptions: FSharpGeneratorDriverOptions
    }

type FSharpSourceGeneratorConfigurationResult =
    {
        Configuration: FSharpSourceGeneratorConfiguration
        Diagnostics: ImmutableArray<FSharpGeneratorDiagnostic>
        RemainingArguments: ImmutableArray<string>
    }

type FSharpMSBuildSourceGeneratorItem =
    {
        Include: string
    }

type FSharpMSBuildAdditionalFileItem =
    {
        Include: string
    }

type FSharpMSBuildSourceGeneratorProperties =
    {
        EmitFSharpGeneratedFiles: string option
        FSharpGeneratedFilesOutputPath: string option
        FSharpSourceGeneratorReportPath: string option
    }
