namespace FSharpNativeGenerator.TestGenerators

open System
open FSharp.Compiler.SourceGeneration

[<FSharpGenerator>]
type CliHarnessGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            let moduleNameProvider =
                context.AnalyzerConfigOptionsProvider
                |> FSharpIncrementalValueProvider.map (fun options ->
                    match options.GlobalOptions.TryGetValue("build_property.GeneratedModuleName") with
                    | true, moduleName -> moduleName
                    | false, _ -> "GeneratedPrelude")

            context.RegisterSourceOutput(
                moduleNameProvider,
                Action<FSharpSourceProductionContext, string>(fun productionContext moduleName ->
                    productionContext.AddImplementationSource(
                        moduleName,
                        FSharpSourceText.OfString("module " + moduleName + "\nlet answer = 42"),
                        Prelude
                    ))
            )

[<FSharpGenerator>]
type CliSourceTextGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            let markedSourceFiles =
                context.SourceFilesProvider
                |> FSharpIncrementalValuesProvider.choose (fun sourceFile ->
                    if sourceFile.SourceText.Text.Contains("SOURCE_TEXT_MARKER", StringComparison.Ordinal) then
                        Some "SawRealSourceText"
                    else
                        None)

            context.RegisterSourceOutput(
                markedSourceFiles,
                Action<FSharpSourceProductionContext, string>(fun productionContext moduleName ->
                    productionContext.AddImplementationSource(
                        moduleName,
                        FSharpSourceText.OfString("module " + moduleName + "\nlet value = 1"),
                        Prelude
                    ))
            )
