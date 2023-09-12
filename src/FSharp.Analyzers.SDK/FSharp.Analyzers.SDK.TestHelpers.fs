module FSharp.Analyzers.SDK.TestHelpers

#nowarn "57"

open FSharp.Compiler.Text
open Microsoft.Build.Logging.StructuredLogger
open CliWrap
open System
open System.IO
open FSharp.Compiler.CodeAnalysis

type DotNetVersion =
    | Six
    | Seven

    override this.ToString() =
        match this with
        | Six -> "net6.0"
        | Seven -> "net7.0"

type FSharpProjectOptions with

    static member zero =
        {
            ProjectFileName = ""
            ProjectId = None
            SourceFiles = [||]
            OtherOptions = [||]
            ReferencedProjects = [||]
            IsIncompleteTypeCheckEnvironment = false
            UseScriptResolutionRules = false
            LoadTime = DateTime.UtcNow
            UnresolvedReferences = None
            OriginalLoadReferences = []
            Stamp = None
        }

type Package = { Name: string; Version: string }

let fsharpFiles = set [| ".fs"; ".fsi"; ".fsx" |]

let isFSharpFile (file: string) =
    Seq.exists (fun (ext: string) -> file.EndsWith ext) fsharpFiles

let readCompilerArgsFromBinLog file =
    let build = BinaryLog.ReadBuild file

    let projectName =
        build.Children
        |> Seq.choose (
            function
            | :? Project as p -> Some p.Name
            | _ -> None
        )
        |> Seq.distinct
        |> Seq.exactlyOne

    let message (fscTask: FscTask) =
        fscTask.Children
        |> Seq.tryPick (
            function
            | :? Message as m when m.Text.Contains "fsc" -> Some m.Text
            | _ -> None
        )

    let mutable args = None

    build.VisitAllChildren<Task>(fun task ->
        match task with
        | :? FscTask as fscTask ->
            match fscTask.Parent.Parent with
            | :? Project as p when p.Name = projectName -> args <- message fscTask
            | _ -> ()
        | _ -> ()
    )

    match args with
    | None -> failwith $"Could not parse binlog at {file}, does it contain CoreCompile?"
    | Some args ->
        let idx = args.IndexOf "-o:"
        args.Substring(idx).Split [| '\n' |]

let mkOptions (compilerArgs: string array) =
    let sourceFiles =
        compilerArgs
        |> Array.filter (fun (line: string) -> isFSharpFile line && File.Exists line)

    let otherOptions =
        compilerArgs |> Array.filter (fun line -> not (isFSharpFile line))

    {
        ProjectFileName = "Project"
        ProjectId = None
        SourceFiles = sourceFiles
        OtherOptions = otherOptions
        ReferencedProjects = [||]
        IsIncompleteTypeCheckEnvironment = false
        UseScriptResolutionRules = false
        LoadTime = DateTime.Now
        UnresolvedReferences = None
        OriginalLoadReferences = []
        Stamp = None
    }

let mkOptionsFromBinaryLog binLogPath =
    let compilerArgs = readCompilerArgsFromBinLog binLogPath
    mkOptions compilerArgs

let mkOptionsFromProject (version: DotNetVersion) (additionalPkg: Package option) =
    let id = Guid.NewGuid().ToString("N")
    let tmpDir = Path.Combine(Path.GetTempPath(), id)
    let binLogPath = Path.Combine(tmpDir, $"{id}.binlog")

    Directory.CreateDirectory(tmpDir) |> ignore

    Cli
        .Wrap("dotnet")
        .WithWorkingDirectory(tmpDir)
        .WithArguments($"new classlib -f {version.ToString()} -lang F#")
        .WithValidation(CommandResultValidation.None)
        .ExecuteAsync()
        .Task.Result
    |> ignore

    additionalPkg
    |> Option.iter (fun p ->
        Cli
            .Wrap("dotnet")
            .WithWorkingDirectory(tmpDir)
            .WithArguments($"add package {p.Name} --version {p.Version}")
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync()
            .Task.Result
        |> ignore
    )

    Cli
        .Wrap("dotnet")
        .WithArguments($"build {tmpDir} -bl:{binLogPath}")
        .WithValidation(CommandResultValidation.None)
        .ExecuteAsync()
        .Task.Result
    |> ignore

    mkOptionsFromBinaryLog binLogPath

let getContext (opts: FSharpProjectOptions) source =
    let fileName = "A.fs"
    let files = Map.ofArray [| (fileName, SourceText.ofString source) |]

    let documentSource fileName =
        Map.tryFind fileName files |> async.Return

    let fcs = Utils.createFCS (Some documentSource)
    let printError (s: string) = Console.WriteLine(s)
    let pathToAnalyzerDlls = Path.GetFullPath(".")

    let foundDlls, registeredAnalyzers =
        Client.loadAnalyzers printError pathToAnalyzerDlls

    if foundDlls = 0 then
        failwith $"no Dlls found in {pathToAnalyzerDlls}"

    if registeredAnalyzers = 0 then
        failwith $"no Analyzers found in {pathToAnalyzerDlls}"

    let opts =
        { opts with
            SourceFiles = [| fileName |]
        }

    fcs.NotifyFileChanged(fileName, opts) |> Async.RunSynchronously // workaround for https://github.com/dotnet/fsharp/issues/15960
    let checkProjectResults = fcs.ParseAndCheckProject(opts) |> Async.RunSynchronously
    let allSymbolUses = checkProjectResults.GetAllUsesOfAllSymbols()

    if Array.isEmpty allSymbolUses then
        failwith "no symboluses"

    match Utils.typeCheckFile fcs (Utils.SourceOfSource.DiscreteSource source, fileName, opts) with
    | Some(file, text, parseRes, result) ->
        let ctx =
            Utils.createContext (checkProjectResults, allSymbolUses) (file, text, parseRes, result)

        match ctx with
        | Some c -> c
        | None -> failwith "Context creation failed"
    | None -> failwith "typechecking file failed"
