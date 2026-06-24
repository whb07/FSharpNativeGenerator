namespace FSharpNativeGenerator.TestGenerators

open System
open FSharp.Compiler.SourceGeneration

[<FSharpGenerator>]
type CliHarnessGenerator() =
    interface IFSharpIncrementalGenerator with
        member _.Initialize context =
            context.RegisterSourceOutput(
                context.ProjectOptionsProvider,
                Action<FSharpSourceProductionContext, FSharpProjectOptions>(fun productionContext _ ->
                    productionContext.AddImplementationSource("GeneratedPrelude", FSharpSourceText.OfString "module GeneratedPrelude\nlet answer = 42", Prelude)))
