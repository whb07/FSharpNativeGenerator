namespace FSharpNativeGenerator.Cli

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.IO
open System.Threading
open FSharp.Compiler.SourceGeneration

module Program =
    let private usage =
        """Usage:
  FSharpNativeGenerator.Cli [generator options] <source.fs|source.fsi>...

Generator options:
  --fsharp-source-generator:<path>
  --fsharp-generator-additional-file:<path>
  --emit-fsharp-generated-files[+|-]
  --fsharp-generated-files-output:<dir>
  --fsharp-source-generator-report:<path>
  --fsharp-source-generator-analyzer-config:<path>
"""

    let private isSourcePath (argument: string) =
        argument.EndsWith(".fs", StringComparison.OrdinalIgnoreCase)
        || argument.EndsWith(".fsi", StringComparison.OrdinalIgnoreCase)

    let private outputKindFromArgs (arguments: seq<string>) =
        if arguments |> Seq.exists (fun argument -> argument.Equals("--target:exe", StringComparison.OrdinalIgnoreCase)) then
            Application
        else
            Library

    let private sourceSnapshot path =
        let fullPath = Path.GetFullPath(path)

        if File.Exists fullPath then
            FSharpSourceFileSnapshot.create fullPath (File.ReadAllText fullPath)
        else
            let emptyText = FSharpSourceText.OfString ""

            {
                Path = fullPath
                SourceText = emptyText
                Checksum = FSharpSourceText.checksum emptyText
                IsSignatureFile = fullPath.EndsWith(".fsi", StringComparison.OrdinalIgnoreCase)
            }

    let private analyzerConfigOptions =
        {
            GlobalOptions = Dictionary<string, string>() :> IReadOnlyDictionary<string, string>
            GetOptionsForPath = fun _ -> Dictionary<string, string>() :> IReadOnlyDictionary<string, string>
        }

    let private printDiagnostic (diagnostic: FSharpGeneratorDiagnostic) =
        let location = diagnostic.FilePath |> Option.defaultValue ""
        eprintfn "%s %A %s %s" diagnostic.Id diagnostic.Severity location diagnostic.Message

    [<EntryPoint>]
    let main argv =
        let parsed = FSharpSourceGeneratorConfiguration.parseCommandLineArguments argv
        let sourceFiles = parsed.RemainingArguments |> Seq.filter isSourcePath |> Seq.map Path.GetFullPath |> ImmutableArray.CreateRange

        if sourceFiles.IsDefaultOrEmpty then
            eprintf "%s" usage
            2
        elif parsed.Diagnostics |> Seq.exists (fun diagnostic -> diagnostic.Severity = Error) then
            parsed.Diagnostics |> Seq.iter printDiagnostic
            1
        else
            let loadResult = FSharpSourceGeneratorConfiguration.loadGenerators parsed.Configuration
            let loadDiagnostics = loadResult.Diagnostics

            if loadDiagnostics |> Seq.exists (fun diagnostic -> diagnostic.Severity = Error) then
                loadDiagnostics |> Seq.iter printDiagnostic
                1
            else
                let projectOptions =
                    {
                        ProjectFilePath = Path.Combine(Environment.CurrentDirectory, "FSharpNativeGenerator.Cli.fsproj")
                        ProjectId = Some "FSharpNativeGenerator.Cli"
                        SourceFiles = sourceFiles
                        OtherOptions = parsed.RemainingArguments |> Seq.filter (isSourcePath >> not) |> ImmutableArray.CreateRange
                        OutputKind = outputKindFromArgs parsed.RemainingArguments
                        Stamp = None
                    }

                let snapshot =
                    {
                        ProjectOptions = projectOptions
                        SourceFiles = sourceFiles |> Seq.map sourceSnapshot |> ImmutableArray.CreateRange
                        AdditionalTexts = FSharpSourceGeneratorConfiguration.additionalTexts parsed.Configuration
                        AnalyzerConfigOptions = analyzerConfigOptions
                    }

                let driver = FSharpGeneratorDriver.Create(loadResult.Generators, parsed.Configuration.DriverOptions)
                let _, result = driver.RunGenerators(snapshot, CancellationToken.None)

                result.Diagnostics |> Seq.iter printDiagnostic

                if result.Diagnostics |> Seq.exists (fun diagnostic -> diagnostic.Severity = Error) then
                    1
                else
                    for sourceFile in result.UpdatedSourceFiles do
                        printfn "%s" sourceFile

                    0
