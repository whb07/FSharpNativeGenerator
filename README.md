# FSharpNativeGenerator

## Running Tests

This repo currently references a local F# compiler fork through
`$(FSharpRepoRoot)`, which defaults to `../fsharp`.

Run the test suite with the same build properties the fork expects when it is
used from an external solution:

```bash
MSBUILDDISABLENODEREUSE=1 dotnet test FSharpNativeGenerator.slnx -v minimal \
  -p:BUILDING_USING_DOTNET=true \
  -p:DISABLE_ARCADE=true
```

Without `BUILDING_USING_DOTNET=true` and `DISABLE_ARCADE=true`, restore can fail
before tests run because the fork's package-version properties are not imported.
The usual symptoms are `NU1015` errors for versionless package references and
`NU1101` errors for the optimization MIBC runtime packages.

Do not pass `/nr:false` to `dotnet test` for this suite. With xUnit v3 and
Microsoft.Testing.Platform it can be forwarded to the test application, which
then reports `Tool '/nr:false' not found` and runs zero tests. Use
`MSBUILDDISABLENODEREUSE=1` instead.
