namespace FsAutoComplete

open System
open System.IO

open Microsoft.FSharp.Compiler.SourceCodeServices

module MSBuildPrj = Dotnet.ProjInfo.Inspect

exception ProjectInspectException of GetProjectOptionsErrors

type NavigateProjectSM =
    | NoCrossTargeting of NoCrossTargetingData
    | CrossTargeting of string list
and NoCrossTargetingData = { FscArgs: string list; P2PRefs: MSBuildPrj.ResolvedP2PRefsInfo list; Properties: Map<string,string> }

module ProjectCrackerDotnetSdk =

  let runProcess (log: string -> unit) (workingDir: string) (exePath: string) (args: string) =
      let psi = System.Diagnostics.ProcessStartInfo()
      psi.FileName <- exePath
      psi.WorkingDirectory <- workingDir
      psi.RedirectStandardOutput <- true
      psi.RedirectStandardError <- true
      psi.Arguments <- args
      psi.CreateNoWindow <- true
      psi.UseShellExecute <- false

      use p = new System.Diagnostics.Process()
      p.StartInfo <- psi

      p.OutputDataReceived.Add(fun ea -> log (ea.Data))

      p.ErrorDataReceived.Add(fun ea -> log (ea.Data))

      p.Start() |> ignore
      p.BeginOutputReadLine()
      p.BeginErrorReadLine()
      p.WaitForExit()

      let exitCode = p.ExitCode

      exitCode, (workingDir, exePath, args)

  let msbuildPropBool (s: string) =
    match s.Trim() with
    | "" -> None
    | MSBuildPrj.MSBuild.ConditionEquals "True" -> Some true
    | _ -> Some false

  let msbuildPropStringList (s: string) =
    match s.Trim() with
    | "" -> []
    | MSBuildPrj.MSBuild.StringList list  -> list
    | _ -> []

  let msbuildPropProjectOutputType (s: string) =
    match s.Trim() with
    | MSBuildPrj.MSBuild.ConditionEquals "Exe" -> ProjectOutputType.Exe
    | MSBuildPrj.MSBuild.ConditionEquals "Library" -> ProjectOutputType.Library
    | x -> ProjectOutputType.Custom x

  let getExtraInfo props =
    let msbuildPropBool prop =
        props |> Map.tryFind prop |> Option.bind msbuildPropBool
    let msbuildPropStringList prop =
        props |> Map.tryFind prop |> Option.map msbuildPropStringList
    let msbuildPropString prop =
        props |> Map.tryFind prop

    { ProjectSdkTypeDotnetSdk.IsTestProject = msbuildPropBool "IsTestProject" |> Option.getOrElse false
      Configuration = msbuildPropString "Configuration" |> Option.getOrElse ""
      IsPackable = msbuildPropBool "IsPackable" |> Option.getOrElse false
      TargetFramework = msbuildPropString "TargetFramework" |> Option.getOrElse ""
      TargetFrameworkIdentifier = msbuildPropString "TargetFrameworkIdentifier" |> Option.getOrElse ""
      TargetFrameworkVersion = msbuildPropString "TargetFrameworkVersion" |> Option.getOrElse ""

      MSBuildAllProjects = msbuildPropStringList "MSBuildAllProjects" |> Option.getOrElse []
      MSBuildToolsVersion = msbuildPropString "MSBuildToolsVersion" |> Option.getOrElse ""

      ProjectAssetsFile = msbuildPropString "ProjectAssetsFile" |> Option.getOrElse ""
      RestoreSuccess = msbuildPropBool "RestoreSuccess" |> Option.getOrElse false

      Configurations = msbuildPropStringList "Configurations" |> Option.getOrElse []
      TargetFrameworks = msbuildPropStringList "TargetFrameworks" |> Option.getOrElse []

      RunArguments = msbuildPropString "RunArguments"
      RunCommand = msbuildPropString "RunCommand"

      IsPublishable = msbuildPropBool "IsPublishable" }

  let getProjectOptionsFromProjectFile (file : string) =

    let rec projInfo additionalMSBuildProps file =
        let projDir = Path.GetDirectoryName file

        let projectAssetsJsonPath = Path.Combine(projDir, "obj", "project.assets.json")
        if not(File.Exists(projectAssetsJsonPath)) then
            raise (ProjectInspectException (ProjectNotRestored file))

        let getFscArgs = Dotnet.ProjInfo.Inspect.getFscArgs
        let getP2PRefs = Dotnet.ProjInfo.Inspect.getResolvedP2PRefs
        let additionalInfo = //needed for extra
            [ "OutputType"
              "IsTestProject"
              "Configuration"
              "IsPackable"
              "TargetFramework"
              "TargetFrameworkIdentifier"
              "TargetFrameworkVersion"
              "MSBuildAllProjects"
              "ProjectAssetsFile"
              "RestoreSuccess"
              "Configurations"
              "TargetFrameworks"
              "RunArguments"
              "RunCommand"
              "IsPublishable"
            ]
        let gp () = Dotnet.ProjInfo.Inspect.getProperties (["TargetPath"; "IsCrossTargetingBuild"; "TargetFrameworks"] @ additionalInfo)

        let results, log =
            let loggedMessages = System.Collections.Concurrent.ConcurrentQueue<string>()

            let runCmd exePath args = runProcess loggedMessages.Enqueue projDir exePath (args |> String.concat " ")

            let msbuildExec = Dotnet.ProjInfo.Inspect.dotnetMsbuild runCmd

            let additionalArgs = additionalMSBuildProps |> List.map (Dotnet.ProjInfo.Inspect.MSBuild.MSbuildCli.Property)

            let infoResult =
                file
                |> Dotnet.ProjInfo.Inspect.getProjectInfos loggedMessages.Enqueue msbuildExec [getFscArgs; getP2PRefs; gp] additionalArgs

            infoResult, (loggedMessages.ToArray() |> Array.toList)

        let todo =
            match results with
            | Choice1Of2 [getFscArgsResult; getP2PRefsResult; gpResult] ->
                match getFscArgsResult, getP2PRefsResult, gpResult with
                | Choice2Of2(MSBuildPrj.MSBuildSkippedTarget), Choice2Of2(MSBuildPrj.MSBuildSkippedTarget), Choice1Of2 (MSBuildPrj.GetResult.Properties props) ->
                    // Projects with multiple target frameworks, fails if the target framework is not choosen
                    let prop key = props |> Map.ofList |> Map.tryFind key

                    match prop "IsCrossTargetingBuild", prop "TargetFrameworks" with
                    | Some (MSBuildPrj.MSBuild.ConditionEquals "true"), Some (MSBuildPrj.MSBuild.StringList tfms) ->
                        CrossTargeting tfms
                    | _ ->
                        failwithf "error getting msbuild info: some targets skipped, found props: %A" props
                | Choice1Of2 (MSBuildPrj.GetResult.FscArgs fa), Choice1Of2 (MSBuildPrj.GetResult.ResolvedP2PRefs p2p), Choice1Of2 (MSBuildPrj.GetResult.Properties p) ->
                    NoCrossTargeting { FscArgs = fa; P2PRefs = p2p; Properties = p |> Map.ofList }
                | r ->
                    failwithf "error getting msbuild info: %A" r
            | Choice1Of2 r ->
                failwithf "error getting msbuild info: internal error, more info returned than expected %A" r
            | Choice2Of2 r ->
                match r with
                | Dotnet.ProjInfo.Inspect.GetProjectInfoErrors.MSBuildSkippedTarget ->
                    failwithf "Unexpected MSBuild result, all targets skipped"
                | Dotnet.ProjInfo.Inspect.GetProjectInfoErrors.UnexpectedMSBuildResult(r) ->
                    failwithf "Unexpected MSBuild result %s" r
                | Dotnet.ProjInfo.Inspect.GetProjectInfoErrors.MSBuildFailed(exitCode, (workDir, exePath, args)) ->
                    let logMsg = [ yield "Log: "; yield! log ] |> String.concat (Environment.NewLine)
                    let msbuildErrorMsg =
                        [ sprintf "MSBuild failed with exitCode %i" exitCode
                          sprintf "Working Directory: '%s'" workDir
                          sprintf "Exe Path: '%s'" exePath
                          sprintf "Args: '%s'" args ]
                        |> String.concat " "
                    
                    failwithf "%s%s%s" msbuildErrorMsg (Environment.NewLine) logMsg

        match todo with
        | CrossTargeting (tfm :: _) ->
            // Atm setting a preferenece is not supported in FSAC
            // As workaround, lets choose the first of the target frameworks and use that
            file |> projInfo ["TargetFramework", tfm]
        | CrossTargeting [] ->
            failwithf "Unexpected, found cross targeting but empty target frameworks list"
        | NoCrossTargeting { FscArgs = rsp; P2PRefs = p2ps; Properties = props } ->

            //TODO cache projects info of p2p ref
            let p2pProjects =
                p2ps
                // do not follow others lang project, is not supported by FCS anyway
                |> List.filter (fun p2p -> p2p.ProjectReferenceFullPath.ToLower().EndsWith(".fsproj"))
                |> List.map (fun p2p -> p2p.ProjectReferenceFullPath |> projInfo ["TargetFramework", p2p.TargetFramework] )

            let tar =
                match props |> Map.tryFind "TargetPath" with
                | Some t -> t
                | None -> failwith "error, 'TargetPath' property not found"

            let rspNormalized =
                //workaround, arguments in rsp can use relative paths
                rsp |> List.map (FscArguments.useFullPaths projDir)
            
            let extraInfo = getExtraInfo props
            let po =
                {
                    ProjectFileName = file
                    SourceFiles = [||]
                    OtherOptions = rspNormalized |> Array.ofList
                    ReferencedProjects = p2pProjects |> Array.ofList
                    IsIncompleteTypeCheckEnvironment = false
                    UseScriptResolutionRules = false
                    LoadTime = DateTime.Now
                    UnresolvedReferences = None
                    OriginalLoadReferences = []
                    Stamp = None
                    ExtraProjectInfo =
                        Some (box {
                            ExtraProjectInfoData.ProjectSdkType = ProjectSdkType.DotnetSdk(extraInfo)
                            ExtraProjectInfoData.ProjectOutputType = FscArguments.outType rspNormalized
                        })
                }

            tar, po

    let _, po = projInfo [] file
    po


  let load file =
      try
        let po = getProjectOptionsFromProjectFile file
        let compileFiles = FscArguments.compileFiles (po.OtherOptions |> List.ofArray)
        Ok (po, Seq.toList compileFiles, Map<string,string>([||]))
      with
        | ProjectInspectException d -> Err d
        | e -> Err (GenericError(e.Message))

