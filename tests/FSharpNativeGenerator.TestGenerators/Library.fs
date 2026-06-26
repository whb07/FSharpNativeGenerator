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
                    productionContext.AddImplementationSource(moduleName, FSharpSourceText.OfString("module " + moduleName + "\nlet answer = 42"), Prelude)))
