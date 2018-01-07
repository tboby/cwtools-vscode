﻿module Main.Program

open LSP
open LSP.Types
open System
open System.IO
open Microsoft.FSharp.Compiler.SourceCodeServices
open CWTools.Parser
open CWTools.Games
open FParsec
open System.Threading.Tasks
open System.Text

let private TODO() = raise (Exception "TODO")

type Server(send : BinaryWriter) = 
    let send = send
    let docs = DocumentStore()
    let projects = ProjectManager()
    let checker = FSharpChecker.Create()
    let emptyProjectOptions = checker.GetProjectOptionsFromCommandLineArgs("NotFound.fsproj", [||])
    let notFound (doc: Uri) (): 'Any = 
        raise (Exception (sprintf "%s does not exist" (doc.ToString())))
    let mutable docErrors : DocumentHighlight list = []
    let lint (doc: Uri): Async<unit> = 
        eprintfn "%s" "lint!"
        async {
            let name = doc.AbsolutePath.ToString()
            let version = docs.GetVersion doc |> Option.defaultWith (notFound doc)
            let source = docs.GetText doc |> Option.defaultWith (notFound doc)
            let parsed = CKParser.parseEventString source name
            eprintfn "%s" "lint!"
            match parsed with
            |Success(_,_,_) -> ()
            |Failure(msg,p,s) ->
                let response = {
                                range = {
                                        start = { 
                                                line = (int p.Position.Line - 1)
                                                character = (int 0 )
                                            }
                                        ``end`` = {
                                                line = (int p.Position.Line - 1)
                                                character = (int p.Position.Column - 1)
                                            }
                                }
                                severity = Some DiagnosticSeverity.Error
                                code = None
                                source = None
                                message = msg
                            }
                LanguageServer.sendNotification send (PublishDiagnostics {uri = doc; diagnostics = [response]})

            //let compilerOptions = projects.FindProjectOptions doc |> Option.defaultValue emptyCompilerOptions
            // let! parseResults, checkAnswer = checker.ParseAndCheckFileInProject(name, version, source, projectOptions)
            // for error in parseResults.Errors do 
            //     eprintfn "%s %d:%d %s" error.FileName error.StartLineAlternate error.StartColumn error.Message
            // match checkAnswer with 
            // | FSharpCheckFileAnswer.Aborted -> eprintfn "Aborted checking %s" name 
            // | FSharpCheckFileAnswer.Succeeded checkResults -> 
            //     for error in checkResults.Errors do 
            //         eprintfn "%s %d:%d %s" error.FileName error.StartLineAlternate error.StartColumn error.Message
        }

    let parserErrorToDiagnostics e =
        let file, error, (position : Position) = e
        let result = {
                        range = {
                                start = { 
                                        line = (int position.Line - 1)
                                        character = (int 0 )
                                    }
                                ``end`` = {
                                            line = (int position.Line - 1)
                                            character = (int position.Column - 1)
                                    }
                        }
                        severity = Some DiagnosticSeverity.Error
                        code = None
                        source = None
                        message = error
                    }
        (file, result)
    let (|TrySuccess|TryFailure|) tryResult =  
        match tryResult with
        | true, value -> TrySuccess value
        | _ -> TryFailure
    let processWorkspace (uri : option<Uri>) =
        eprintfn "%A %A" "processAlll" uri
        match uri with
        |Some u -> 
            let path = u.LocalPath.Substring(1)
            eprintfn "%s %A" "processingSTL" path
            try
                let docs = DocsParser.parseDocsFile @"G:\Projects\CK2 Events\CWTools\files\game_effects_triggers_1.9.1.txt"
                let triggers, effects = (docs |> (function |Success(p, _, _) -> p))
                let game = STLGame(path, FilesScope.All, "", triggers, effects)
                eprintfn "%s %A %A" "procssed:" path game.ParserErrors.Length
                eprintfn "%A %A" game.AllFiles (Directory.Exists path)
                let valErrors = game.ValidationErrors |> List.map (fun (n, e) -> let (Position p) = n.Position in (p.StreamName, e, p) )
                game.ParserErrors @ valErrors
                    |> List.map parserErrorToDiagnostics
                    |> List.groupBy fst
                    |> List.map (fun (f, rs) -> PublishDiagnostics {uri = (match Uri.TryCreate(f, UriKind.Absolute) with |TrySuccess value -> value |TryFailure -> eprintfn "%s" f; Uri "/") ; diagnostics = List.map snd rs})
                    |> List.iter (fun f -> LanguageServer.sendNotification send f)
            with
                | :? System.Exception as e -> eprintfn "%A" e
            
        |None -> ()

    interface ILanguageServer with 
        member this.Initialize(p: InitializeParams): InitializeResult = 
            let task = new Task((fun () -> processWorkspace(p.rootUri)))
            task.Start()
            { capabilities = 
                { defaultServerCapabilities with 
                    textDocumentSync = 
                        { defaultTextDocumentSyncOptions with 
                            openClose = true 
                            save = Some { includeText = true }
                            change = TextDocumentSyncKind.Full } } }
        member this.Initialized(): unit = 
            ()
        member this.Shutdown(): unit = 
            ()
        member this.DidChangeConfiguration(p: DidChangeConfigurationParams): unit =
            eprintfn "New configuration %s" (p.ToString())
        member this.DidOpenTextDocument(p: DidOpenTextDocumentParams): unit = 
            eprintfn "%s" "lint!"
            docs.Open p
            lint p.textDocument.uri |> Async.RunSynchronously
        member this.DidChangeTextDocument(p: DidChangeTextDocumentParams): unit = 
            docs.Change p
            lint p.textDocument.uri |> Async.RunSynchronously
        member this.WillSaveTextDocument(p: WillSaveTextDocumentParams): unit = TODO()
        member this.WillSaveWaitUntilTextDocument(p: WillSaveTextDocumentParams): list<TextEdit> = TODO()
        member this.DidSaveTextDocument(p: DidSaveTextDocumentParams): unit = 
            eprintfn "%s" (p.ToString())
        member this.DidCloseTextDocument(p: DidCloseTextDocumentParams): unit = 
            docs.Close p
        member this.DidChangeWatchedFiles(p: DidChangeWatchedFilesParams): unit = 
            for change in p.changes do 
                eprintfn "Watched file %s %s" (change.uri.ToString()) (change._type.ToString())
                if change.uri.AbsolutePath.EndsWith ".fsproj" then
                    projects.UpdateProjectFile change.uri 
                lint change.uri |> Async.RunSynchronously
        member this.Completion(p: TextDocumentPositionParams): CompletionList = TODO()
        member this.Hover(p: TextDocumentPositionParams): Hover = TODO()
        member this.ResolveCompletionItem(p: CompletionItem): CompletionItem = TODO()
        member this.SignatureHelp(p: TextDocumentPositionParams): SignatureHelp = TODO()
        member this.GotoDefinition(p: TextDocumentPositionParams): list<Location> = TODO()
        member this.FindReferences(p: ReferenceParams): list<Location> = TODO()
        member this.DocumentHighlight(p: TextDocumentPositionParams): list<DocumentHighlight> = TODO()
        member this.DocumentSymbols(p: DocumentSymbolParams): list<SymbolInformation> = TODO()
        member this.WorkspaceSymbols(p: WorkspaceSymbolParams): list<SymbolInformation> = TODO()
        member this.CodeActions(p: CodeActionParams): list<Command> = TODO()
        member this.CodeLens(p: CodeLensParams): List<CodeLens> = TODO()
        member this.ResolveCodeLens(p: CodeLens): CodeLens = TODO()
        member this.DocumentLink(p: DocumentLinkParams): list<DocumentLink> = TODO()
        member this.ResolveDocumentLink(p: DocumentLink): DocumentLink = TODO()
        member this.DocumentFormatting(p: DocumentFormattingParams): list<TextEdit> = TODO()
        member this.DocumentRangeFormatting(p: DocumentRangeFormattingParams): list<TextEdit> = TODO()
        member this.DocumentOnTypeFormatting(p: DocumentOnTypeFormattingParams): list<TextEdit> = TODO()
        member this.Rename(p: RenameParams): WorkspaceEdit = TODO()
        member this.ExecuteCommand(p: ExecuteCommandParams): unit = TODO()

[<EntryPoint>]
let main (argv: array<string>): int =
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    let read = new BinaryReader(Console.OpenStandardInput())
    let write = new BinaryWriter(Console.OpenStandardOutput())
    let server = Server(write)
    eprintfn "Listening on stdin"
    LanguageServer.connect server read write
    0 // return an integer exit code
