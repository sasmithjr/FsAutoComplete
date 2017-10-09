open System
open System.IO
open System.Diagnostics
open System.Text.RegularExpressions

#load "../../.paket/load/net45/IntegrationTests/Hopac.fsx"
#load "../../.paket/load/net45/IntegrationTests/Http.fs.fsx"
#load "../../.paket/load/net45/IntegrationTests/Newtonsoft.Json.fsx"

open Newtonsoft.Json

let (</>) a b = Path.Combine(a,b)

type FsAutoCompleteWrapperStdio() =

  let p = new System.Diagnostics.Process()
  let cachedOutput = new Text.StringBuilder()

  do
    p.StartInfo.FileName <- FsAutoCompleteWrapperStdio.ExePath ()
    p.StartInfo.RedirectStandardOutput <- true
    p.StartInfo.RedirectStandardError  <- true
    p.StartInfo.RedirectStandardInput  <- true
    p.StartInfo.UseShellExecute <- false
    p.StartInfo.EnvironmentVariables.Add("FCS_ToolTipSpinWaitTime", "10000")
    if Environment.GetEnvironmentVariable("FSAC_TESTSUITE_WAITDEBUGGER") = "1" then
      p.StartInfo.Arguments <- "--wait-for-debugger"
    printfn "Starting %s %s" p.StartInfo.FileName p.StartInfo.Arguments
    p.Start () |> ignore

  static member ExePath () =
      IO.Path.Combine(__SOURCE_DIRECTORY__,
                      "../../src/FsAutoComplete/bin/Debug/fsautocomplete.exe")

  member x.project (s: string) : unit =
    fprintf p.StandardInput "project \"%s\"\n" s

  member x.parse (s: string) : unit =
    let text = if IO.File.Exists s then IO.File.ReadAllText(s) else ""
    fprintf p.StandardInput "parse \"%s\" sync\n%s\n<<EOF>>\n" s text

  member x.parseContent (filename: string) (content: string) : unit =
    fprintf p.StandardInput "parse \"%s\" sync\n%s\n<<EOF>>\n" filename content

  member x.completion (fn: string) (lineStr:string)(line: int) (col: int) : unit =
    fprintf p.StandardInput "completion \"%s\" \"%s\" %d %d\n" fn lineStr line col

  member x.methods (fn: string) (lineStr: string)(line: int) (col: int) : unit =
    fprintf p.StandardInput "methods \"%s\" \"%s\" %d %d\n" fn lineStr line col

  member x.completionFilter (fn: string) (lineStr: string)(line: int) (col: int) (filter: string) : unit =
    fprintf p.StandardInput "completion \"%s\" \"%s\" %d %d filter=%s\n" fn lineStr line col filter

  member x.tooltip (fn: string) (lineStr: string) (line: int) (col: int) : unit =
    fprintf p.StandardInput "tooltip \"%s\" \"%s\" %d %d\n" fn lineStr line col

  member x.typesig (fn: string) (lineStr: string) (line: int) (col: int) : unit =
    fprintf p.StandardInput "typesig \"%s\" \"%s\" %d %d\n" fn lineStr line col

  member x.finddeclaration (fn: string) (lineStr: string) (line: int) (col: int) : unit =
    fprintf p.StandardInput "finddecl \"%s\" \"%s\" %d %d\n" fn lineStr line col

  member x.symboluse (fn: string) (lineStr: string) (line: int) (col: int) : unit =
    fprintf p.StandardInput "symboluse \"%s\" \"%s\" %d %d\n" fn lineStr line col

  member x.declarations (fn: string) : unit =
    fprintf p.StandardInput "declarations \"%s\"\n" fn

  member x.lint (fn: string) : unit =
    fprintf p.StandardInput "lint \"%s\"\n" fn

  member x.send (s: string) : unit =
    fprintf p.StandardInput "%s" s

  member x.workspacepeek (dir: string) (deep: int): unit =
    fprintf p.StandardInput "workspacepeek \"%s\" %i\n" dir deep

  /// Wait for a single line to be output (one JSON message)
  /// Note that this line will appear at the *start* of output.json,
  /// so use carefully, and preferably only at the beginning.
  member x.waitForLine () : unit =
    cachedOutput.AppendLine(p.StandardOutput.ReadLine()) |> ignore

  member x.finalOutput () : string =
    let s = p.StandardOutput.ReadToEnd()
    let t = p.StandardError.ReadToEnd()
    p.WaitForExit()
    cachedOutput.ToString() + s + t

let formatJson json =
    try
      let parsedJson = JsonConvert.DeserializeObject(json)
      JsonConvert.SerializeObject(parsedJson, Formatting.Indented)
    with _ ->
      json


open Hopac
open HttpFs.Client

#load "../../src/FsAutoComplete/FsAutoComplete.HttpApiContract.fs"

open FsAutoComplete.HttpApiContract

type FsAutoCompleteWrapperHttp() =

  let p = new System.Diagnostics.Process()

  let port = 8089

  do
    p.StartInfo.FileName <- FsAutoCompleteWrapperStdio.ExePath ()
    p.StartInfo.RedirectStandardOutput <- true
    p.StartInfo.RedirectStandardError  <- true
    p.StartInfo.RedirectStandardInput  <- true
    p.StartInfo.UseShellExecute <- false
    p.StartInfo.EnvironmentVariables.Add("FCS_ToolTipSpinWaitTime", "10000")
    if Environment.GetEnvironmentVariable("FSAC_TESTSUITE_WAITDEBUGGER") = "1" then
      p.StartInfo.Arguments <- "--wait-for-debugger"
    p.StartInfo.Arguments <- sprintf "%s --mode http --port %i" p.StartInfo.Arguments port
    printfn "Starting %s %s" p.StartInfo.FileName p.StartInfo.Arguments

    let initialized = new System.Threading.ManualResetEvent(false)

    let fsacOutLines = System.Collections.Concurrent.ConcurrentQueue<string>()
    p.ErrorDataReceived.Add(fun ea -> fsacOutLines.Enqueue(if isNull ea.Data then "" else ea.Data))
    p.OutputDataReceived.Add(fun ea ->
      let s = if isNull ea.Data then "" else ea.Data
      fsacOutLines.Enqueue(s)
      let isStartedMessage = s.Contains "listener started in"
      if isStartedMessage then initialized.Set() |> ignore else ())

    p.Start () |> ignore
    p.BeginOutputReadLine()
    p.BeginErrorReadLine()

    // Wait until FsAC sends the 'listener started' magic string until
    // we inform the caller that it's ready to accept requests.
    if initialized.WaitOne(TimeSpan.FromSeconds(10.0)) then
      ()
    else
      fsacOutLines.ToArray() |> Array.iter (printfn "%s")
      fsacOutLines.ToArray() |> Array.iter (eprintfn "%s")
      if p.HasExited then
        eprintfn "FSAC started and suddendly exited"
      else
        p.Kill()
      failwithf "FSAC wait for initialization timed out"

  let urlWithId (id: int) format = Printf.ksprintf (fun s -> sprintf "http://localhost:%i/%s?requestId=%i" port s id) format

  let crazyness jsonEncodedAsJson =
    jsonEncodedAsJson
    |> JsonConvert.DeserializeObject
    :?>  Newtonsoft.Json.Linq.JArray
    |> Seq.cast<Newtonsoft.Json.Linq.JValue>
    |> Seq.map (fun v -> v.Value :?> string)
    |> Seq.toList

  let doRequest action requestId r =
    Request.createUrl Post (urlWithId requestId "%s" action)
    |> Request.bodyString (r |> JsonConvert.SerializeObject)
    |> Request.responseAsString
    |> Hopac.run
    |> crazyness

  let allResp = ResizeArray<string> ()

  let recordRequest action requestId r =
    doRequest action requestId r
    |> List.iter allResp.Add

  let absPath path = Path.Combine(Environment.CurrentDirectory, path)

  let makeRequestId () = 12

  static member ExePath () = FsAutoCompleteWrapperStdio.ExePath ()

  member x.project (s: string) : unit =
    { ProjectRequest.FileName = absPath s }
    |> recordRequest "project" (makeRequestId())

  member x.parse (s: string) : unit =
    let path = absPath s
    let lines =
      let text = if IO.File.Exists path then IO.File.ReadAllText(path) else ""
      text.Split('\n')
    { ParseRequest.FileName = path; IsAsync = false; Lines = lines; Version = 0 }
    |> recordRequest "parse" (makeRequestId())

  member x.parseContent (filename: string) (content: string) : unit =
    let lines = content.Split('\n')
    { ParseRequest.FileName = absPath filename; IsAsync = false; Lines = lines; Version = 0 }
    |> recordRequest "parse" (makeRequestId())

  member x.completion (fn: string) (lineStr:string)(line: int) (col: int) : unit =
    { CompletionRequest.FileName = absPath fn; SourceLine = lineStr; Line = line; Column = col; Filter = ""; IncludeKeywords = false }
    |> recordRequest "completion" (makeRequestId())

  member x.methods (fn: string) (lineStr: string)(line: int) (col: int) : unit =
    { PositionRequest.Line = line; FileName = absPath fn; Column = col; Filter = "" }
    |> recordRequest "methods" (makeRequestId())

  member x.completionFilter (fn: string) (lineStr: string)(line: int) (col: int) (filter: string) : unit =
    { CompletionRequest.FileName = absPath fn; SourceLine = lineStr; Line = line; Column = col; Filter = filter; IncludeKeywords = false }
    |> recordRequest"completion" (makeRequestId())

  member x.tooltip (fn: string) (lineStr: string) (line: int) (col: int) : unit =
    { PositionRequest.Line = line; FileName = absPath fn; Column = col; Filter = "" }
    |> recordRequest "tooltip" (makeRequestId())

  member x.typesig (fn: string) (lineStr: string) (line: int) (col: int) : unit =
    { PositionRequest.Line = line; FileName = absPath fn; Column = col; Filter = "" }
    |> recordRequest "signature" (makeRequestId())

  member x.finddeclaration (fn: string) (lineStr: string) (line: int) (col: int) : unit =
    { PositionRequest.Line = line; FileName = absPath fn; Column = col; Filter = "" }
    |> recordRequest "finddeclaration" (makeRequestId())

  member x.symboluse (fn: string) (lineStr: string) (line: int) (col: int) : unit =
    { PositionRequest.Line = line; FileName = absPath fn; Column = col; Filter = "" }
    |> recordRequest "symboluse" (makeRequestId())

  member x.declarations (fn: string)  : unit =
    let fn = absPath fn
    let lines = File.ReadAllLines fn
    { DeclarationsRequest.FileName = fn; Lines = lines; Version = 0 }
    |> recordRequest "declarations" (makeRequestId())

  member x.lint (fn: string) : unit =
    { FileRequest.FileName = absPath fn }
    |> recordRequest "lint" (makeRequestId())

  member x.send (s: string) : unit =
    if s.Contains("quit") then
      if not p.HasExited then
        p.Kill ()

  member x.workspacepeek (dir: string) (deep: int): unit =
    { WorkspacePeekRequest.Directory = absPath dir; Deep = deep; ExcludedDirs = [| |] }
    |> recordRequest "workspacePeek" (makeRequestId())

  /// Wait for a single line to be output (one JSON message)
  /// Note that this line will appear at the *start* of output.json,
  /// so use carefully, and preferably only at the beginning.
  member x.waitForLine () : unit =
    ()

  member x.finalOutput () : string =
    allResp
    |> String.concat "\n"


#if FSAC_TEST_HTTP
type FsAutoCompleteWrapper = FsAutoCompleteWrapperHttp
#else
type FsAutoCompleteWrapper = FsAutoCompleteWrapperStdio
#endif

let writeNormalizedOutput (fn: string) (s: string) =

  let driveLetterRegex = if Path.DirectorySeparatorChar  = '/' then "" else "[a-zA-Z]:"
  let normalizeDirSeparators (s: string) =
    if Path.DirectorySeparatorChar  = '/' then
      s
    else
       if Path.GetExtension fn = ".json"
       then s.Replace(@"\\", "/")
       else s.Replace('\\','/')

  let lines = s.TrimEnd().Split('\n')

  for i in [ 0 .. lines.Length - 1 ] do

    // re-serialize json so is indented
    if Path.GetExtension fn = ".json" then
      lines.[i] <- formatJson lines.[i]

    // replace paths with <absolute path removed>
    lines.[i] <- Regex.Replace(normalizeDirSeparators lines.[i],
                               sprintf "%s/.*?test/FsAutoComplete\.IntegrationTests/(.*?(\"|$))" driveLetterRegex,
                               "<absolute path removed>/FsAutoComplete.IntegrationTests/$1")

    // replace paths ending with whitespace with <absolute path removed>
    lines.[i] <- Regex.Replace(lines.[i],
                               sprintf "%s/.*?test/FsAutoComplete\.IntegrationTests/(.*?)\\s" driveLetterRegex,
                               "<absolute path removed>/FsAutoComplete.IntegrationTests/$1 ")

    // replace paths ending with ( with <absolute path removed>
    lines.[i] <- Regex.Replace(lines.[i],
                               sprintf "%s/.*?test/FsAutoComplete\.IntegrationTests/(.*?)\\(" driveLetterRegex,
                               "<absolute path removed>/FsAutoComplete.IntegrationTests/$1(")

    // replace quoted paths "<path>" with <absolute path removed>
    lines.[i] <- Regex.Replace(lines.[i],
                               sprintf "\"%s/[^\"]*?/([^\"/]*?\.dll\")" driveLetterRegex,
                               "\"<absolute path removed>/$1")

    // replace quoted paths '<path>' with <absolute path removed>
    lines.[i] <- Regex.Replace(lines.[i],
                               sprintf "'%s/[^']*?/([^'/]*?\.[a-zA-Z]*)'" driveLetterRegex,
                               "'<absolute path removed>/$1'")

    // replace temp directory with <tempdir path removed>
    lines.[i] <- Regex.Replace(lines.[i],
                               Path.GetTempPath().Replace('\\','/'),
                               "<tempdir path removed>/")

    // replace temp filename with <tempfile name removed>
    lines.[i] <- Regex.Replace(lines.[i],
                               "tmp.*?\.tmp",
                               "<tempfile name removed>")

    // normalize newline char
    lines.[i] <- lines.[i].Replace("\r", "").Replace(@"\r", "")

  //workaround for https://github.com/fsharp/fsharp/issues/774
  let lines = lines |> Array.filter ((<>) "non-IL or abstract method with non-zero RVA")

  // Write manually to ensure \n line endings on all platforms
  use f = new StreamWriter(fn)
  for line in lines do
    f.Write(line)
    f.Write('\n')

let runProcess (workingDir: string) (exePath: string) (args: string) =
    printfn "Running '%s %s' in working dir '%s'" exePath args workingDir
    let psi = System.Diagnostics.ProcessStartInfo()
    psi.FileName <- exePath
    psi.WorkingDirectory <- workingDir
    psi.RedirectStandardOutput <- false
    psi.RedirectStandardError <- false
    psi.Arguments <- args
    psi.CreateNoWindow <- true
    psi.UseShellExecute <- false

    use p = new System.Diagnostics.Process()
    p.StartInfo <- psi
    p.Start() |> ignore
    p.WaitForExit()

    let exitCode = p.ExitCode
    exitCode

let runProcessCaptureOut (workingDir: string) (exePath: string) (args: string) =
    printfn "Running '%s %s' in working dir '%s'" exePath args workingDir
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

    let sbOut = System.Collections.Generic.List<string>()
    p.OutputDataReceived.Add(fun ea ->
        printfn "%s" (ea.Data)
        sbOut.Add(ea.Data) |> ignore)

    let sbErr = System.Collections.Generic.List<string>()
    p.ErrorDataReceived.Add(fun ea ->
        printfn "%s" (ea.Data)
        sbErr.Add(ea.Data) |> ignore)

    p.Start() |> ignore
    p.BeginOutputReadLine()
    p.BeginErrorReadLine()
    p.WaitForExit()

    let exitCode = p.ExitCode
    (exitCode, sbOut |> List.ofSeq, sbErr |> List.ofSeq)

let processResultLog msg (err, outData, errData) =
    let sb = System.Text.StringBuilder()
    sb.Append(sprintf "%s with exit code %i" msg err) |> ignore
    sb.Append("Output:") |> ignore
    outData |> List.iter (fun (s: string) -> sb.Append(s) |> ignore)
    sb.Append("Error:") |> ignore
    errData |> List.iter (fun (s: string) -> sb.Append(s) |> ignore)
    sb.ToString()

let (|NonExitCodeResult|_|) processResult =
  match processResult with
  | (0,_,_) -> None
  | data -> Some data

let deleteDir d =
  if Directory.Exists(d) then
    printfn "Deleting dir '%s'" d
    Directory.Delete(d, true)

let setEnvVar envVar f =
  let oldValue = System.Environment.GetEnvironmentVariable(envVar)
  let newValue = f oldValue
  System.Environment.SetEnvironmentVariable(envVar, newValue)

  { new IDisposable with
    member x.Dispose() =
      System.Environment.SetEnvironmentVariable(envVar, oldValue) }

let withPath dir =
  setEnvVar "PATH" (fun pathvar -> dir + Path.PathSeparator.ToString() + pathvar)

module DotnetCli =

  // see https://github.com/dotnet/core/blob/master/release-notes/download-archive.md for released version
  // the channel and version are passed to Channel and Version argument of install script, see that for
  // more help
  let private dotnetSdkInstallScript channel version toDir =
    let isWindows = Environment.OSVersion.Platform = PlatformID.Win32NT
    let file = if isWindows then "dotnet-install.ps1" else "dotnet-install.sh"
    let repoDir = Path.Combine(__SOURCE_DIRECTORY__, "..", "..")
    let sdkDir = Path.Combine(repoDir, ".dotnetsdk", toDir) |> Path.GetFullPath

    if Directory.Exists(sdkDir) then
      printfn ".net core sdk found in '%s'" sdkDir
      sdkDir
    else
      printfn ".net core sdk not found in '%s'" sdkDir

      Directory.CreateDirectory(sdkDir) |> ignore

      use client = new System.Net.WebClient()
      let installScriptPath = Path.Combine(sdkDir, file)
      let installScriptUrl = sprintf "https://dot.net/v1/%s" file
      printfn "downloading .net core sdk install script %s" installScriptUrl
      try
        client.DownloadFile(installScriptUrl, installScriptPath)
      with _ when not(isWindows) ->
        //DownloadFile fails in WLS (https://github.com/Microsoft/BashOnWindows/issues/1639), fallback to curl
        printfn "download failed, retry with curl"
        match runProcess __SOURCE_DIRECTORY__  "curl" (sprintf "%s -o %s" installScriptUrl installScriptPath) with
        | 0 -> ()
        | _ -> failwithf "Failed to download script '%s' from curl" installScriptUrl

      printfn "installing .net core sdk (channel %s, version %s) to '%s'" channel version sdkDir

      if isWindows then
        let powershell script args = runProcess __SOURCE_DIRECTORY__ "powershell" (sprintf """-NoProfile -ExecutionPolicy unrestricted -File "%s" %s """ script args) |> ignore
        powershell installScriptPath  (sprintf "-InstallDir %s -Channel %s -Version %s" sdkDir channel version)
      else
        let bash script args = runProcess __SOURCE_DIRECTORY__ "bash" (sprintf """ "%s" %s """ script args) |> ignore
        bash installScriptPath  (sprintf "--install-dir %s -channel %s -version %s" sdkDir channel version)

      sdkDir

  let sdk1Dir () = dotnetSdkInstallScript "1.0" "1.0.4" "v1.0.4"
  let sdk2Dir () = dotnetSdkInstallScript "2.0" "2.0.0" "v2.0.0"

  let useSdk sdkDir =
    let p = withPath sdkDir
    let e = setEnvVar "DOTNET_SKIP_FIRST_TIME_EXPERIENCE" (fun _ -> "1")
    { new IDisposable with
      member x.Dispose() = p.Dispose(); e.Dispose() }

  let withNetFxBclAvaiable version =
    let isWindows = Environment.OSVersion.Platform = PlatformID.Win32NT
    if isWindows then
      //on windows is not needed
      { new IDisposable with member x.Dispose() = () }
    else
      let monoLibPath = "/usr/lib/mono/"
      setEnvVar "FrameworkPathOverride" (fun _ -> sprintf "%s%s-api/" monoLibPath version)
