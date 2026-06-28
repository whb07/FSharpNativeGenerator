namespace FSharpNativeGenerator.TestGenerators

open System
open FSharp.Compiler.SourceGeneration

[<FSharpGenerator>]
type CliHarnessGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            context.RegisterPostInitializationOutput(
                Action<FSharpPostInitializationContext>(fun post ->
                    post.AddImplementationSource("GeneratedPrelude", "module GeneratedPrelude\nlet answer = 42"))
            )

[<FSharpGenerator>]
type AdditionalFileGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            let moduleNames =
                context.AdditionalTextsProvider
                |> FSharpIncrementalValuesProvider.map (fun additional -> additional.Text.Trim())
                |> FSharpIncrementalValuesProvider.filter (String.IsNullOrWhiteSpace >> not)

            context.RegisterSourceOutput(
                moduleNames,
                Action<FSharpSourceProductionContext, string>(fun productionContext moduleName ->
                    productionContext.AddImplementationSource(moduleName, "module " + moduleName + "\nlet value = 1", Prelude))
            )

type MissingAttributeGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize _ = ()

[<FSharpGenerator(999)>]
type UnsupportedApiGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize _ = ()
