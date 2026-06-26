namespace FSharpNativeGenerator.Cli

open System
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
            let additionalTextsResult = FSharpSourceGeneratorConfiguration.additionalTextsWithDiagnostics parsed.Configuration
            let analyzerConfigResult = FSharpSourceGeneratorConfiguration.analyzerConfigOptions parsed.Configuration

            let configurationDiagnostics =
                Seq.concat
                    [
                        loadDiagnostics :> seq<_>
                        additionalTextsResult.Diagnostics :> seq<_>
                        analyzerConfigResult.Diagnostics :> seq<_>
                    ]

            if configurationDiagnostics |> Seq.exists (fun diagnostic -> diagnostic.Severity = Error) then
                loadDiagnostics |> Seq.iter printDiagnostic
                additionalTextsResult.Diagnostics |> Seq.iter printDiagnostic
                analyzerConfigResult.Diagnostics |> Seq.iter printDiagnostic
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

                let sourceFilesResult =
                    FSharpSourceFileSnapshot.loadProjectSourceFiles
                        projectOptions
                        (fun _ -> None)
                        CancellationToken.None

                if sourceFilesResult.Diagnostics |> Seq.exists (fun diagnostic -> diagnostic.Severity = Error) then
                    sourceFilesResult.Diagnostics |> Seq.iter printDiagnostic
                    1
                else
                    let snapshot =
                        {
                            ProjectOptions = projectOptions
                            SourceFiles = sourceFilesResult.SourceFiles
                            AdditionalTexts = additionalTextsResult.AdditionalTexts
                            AnalyzerConfigOptions = analyzerConfigResult.Options
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
