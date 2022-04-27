module Marksman.Server

open System
open System.IO
open Ionide.LanguageServerProtocol
open Ionide.LanguageServerProtocol.Types
open Ionide.LanguageServerProtocol.Server

open LanguageServerProtocol.Logging
open Marksman.Misc
open Marksman.Parser
open Text

type Document =
    { path: PathUri
      version: option<int>
      text: Text
      elements: array<Element> }

module Document =
    let logger =
        LogProvider.getLoggerByName "Document"

    let applyLspChange (change: DidChangeTextDocumentParams) (document: Document) : Document =
        let newVersion = change.TextDocument.Version

        logger.trace (
            Log.setMessage "Processing text change"
            >> Log.addContext "uri" document.path
            >> Log.addContext "currentVersion" document.version
            >> Log.addContext "newVersion" newVersion
        )

        // Sanity checking
        match newVersion, document.version with
        | Some newVersion, Some curVersion ->
            let expectedVersion =
                curVersion + change.ContentChanges.Length

            if expectedVersion <> newVersion then
                logger.warn (
                    Log.setMessage "Unexpected document version"
                    >> Log.addContext "uri" document.path
                    >> Log.addContext "currentVersion" curVersion
                    >> Log.addContext "newVersion" newVersion
                )
        | _ -> ()

        let newText =
            applyTextChange change.ContentChanges document.text

        let newElements = scrapeText newText

        { document with
            version = newVersion
            text = newText
            elements = newElements }

    let fromLspDocument (item: TextDocumentItem) : Document =
        let path = PathUri.fromString item.Uri
        let text = mkText item.Text
        let elements = scrapeText text

        { path = path
          version = Some item.Version
          text = text
          elements = elements }


    let load (path: PathUri) : option<Document> =
        try
            let content =
                (new StreamReader(path.AbsolutePath)).ReadToEnd()

            let text = mkText content
            let elements = scrapeText text

            Some
                { path = path
                  text = text
                  elements = elements
                  version = None }
        with
        | :? FileNotFoundException -> None


type Folder =
    { name: string
      root: PathUri
      documents: Map<PathUri, Document> }

module Folder =
    let private logger =
        LogProvider.getLoggerByName "Folder"

    let tryFindDocument (uri: PathUri) (folder: Folder) : option<Document> = Map.tryFind uri folder.documents

    let rec private loadDocuments (root: PathUri) : seq<PathUri * Document> =
        let logger =
            LogProvider.getLoggerByName "readRoot"

        let di = DirectoryInfo(root.AbsolutePath)

        try
            let files = di.GetFiles("*.md")
            let dirs = di.GetDirectories()

            seq {
                for file in files do
                    let pathUri =
                        PathUri.fromString file.FullName

                    let document = Document.load pathUri

                    match document with
                    | Some document -> yield pathUri, document
                    | _ -> ()

                for dir in dirs do
                    yield! loadDocuments (PathUri.fromString dir.FullName)
            }
        with
        | :? UnauthorizedAccessException as exn ->
            logger.warn (
                Log.setMessage "Couldn't read the root folder"
                >> Log.addContext "root" root
                >> Log.addException exn
            )

            Seq.empty
        | :? DirectoryNotFoundException as exn ->
            logger.warn (
                Log.setMessage "The root folder doesn't exist"
                >> Log.addContext "root" root
                >> Log.addException exn
            )

            Seq.empty

    let tryLoad (name: string) (root: PathUri) : option<Folder> =
        if Directory.Exists(root.AbsolutePath) then
            let documents =
                loadDocuments root |> Map.ofSeq

            { name = name
              root = root
              documents = documents }
            |> Some
        else
            logger.warn (
                Log.setMessage "Folder path doesn't exist"
                >> Log.addContext "uri" root
            )

            None

    let loadDocument (uri: PathUri) (folder: Folder) : Folder =
        match Document.load uri with
        | Some doc -> { folder with documents = Map.add uri doc folder.documents }
        | None -> folder

    let removeDocument (uri: PathUri) (folder: Folder) : Folder =
        { folder with documents = Map.remove uri folder.documents }

    let addDocument (doc: Document) (folder: Folder) : Folder =
        { folder with documents = Map.add doc.path doc folder.documents }

type ClientDescription =
    { info: ClientInfo option
      caps: ClientCapabilities }
    member this.IsVSCode: bool =
        this.info
        |> Option.exists (fun x -> x.Name = "Visual Studio Code")

module ClientDescription =
    let fromParams (par: InitializeParams) : ClientDescription =
        let caps =
            par.Capabilities
            |> Option.defaultValue
                { Workspace = None
                  TextDocument = None
                  Experimental = None }

        { info = par.ClientInfo; caps = caps }

type State =
    { client: ClientDescription
      folders: Map<PathUri, Folder>
      revision: int }

module State =
    let logger =
        LogProvider.getLoggerByName "State"

    let tryFindFolder (uri: PathUri) (state: State) : option<Folder> =
        let root =
            state.folders
            |> Map.tryFindKey (fun root _ -> uri.AbsolutePath.StartsWith(root.AbsolutePath))

        root
        |> Option.map (fun root -> state.folders[root])

    let findFolder (uri: PathUri) (state: State) : Folder =
        tryFindFolder uri state
        |> Option.defaultWith (fun _ -> failwith $"Expected folder now found: {uri}")

    let tryFindDocument (uri: PathUri) (state: State) : option<Document> =
        tryFindFolder uri state
        |> Option.map (Folder.tryFindDocument uri)
        |> Option.flatten

    let updateFoldersFromLsp (added: WorkspaceFolder []) (removed: WorkspaceFolder []) (state: State) : State =
        logger.trace (
            Log.setMessage "Updating workspace folders"
            >> Log.addContext "numAdded" added.Length
            >> Log.addContext "numRemoved" removed.Length
        )

        let removedUris =
            removed
            |> Array.map (fun f -> PathUri(Uri(f.Uri)))

        let mutable newFolders = state.folders

        for uri in removedUris do
            newFolders <- Map.remove uri newFolders

        let addedFolders =
            seq {
                for f in added do
                    let rootUri = PathUri.fromString f.Uri

                    let folder = Folder.tryLoad f.Name rootUri

                    match folder with
                    | Some folder -> yield rootUri, folder
                    | _ -> ()
            }

        for uri, folder in addedFolders do
            newFolders <- Map.add uri folder newFolders

        { state with folders = newFolders }

    let updateDocument (newDocument: Document) (state: State) : State =
        let folder =
            findFolder newDocument.path state

        let newContent =
            folder.documents
            |> Map.add newDocument.path newDocument

        let newFolder =
            { folder with documents = newContent }

        let newFolders =
            state.folders |> Map.add newFolder.root newFolder

        { state with folders = newFolders }

    let removeDocument (path: PathUri) (state: State) : State =
        let folder = findFolder path state

        let newFolder =
            Folder.removeDocument path folder

        { state with folders = Map.add folder.root newFolder state.folders }

let extractWorkspaceFolders (par: InitializeParams) : Map<string, PathUri> =
    match par.WorkspaceFolders with
    | Some folders ->
        folders
        |> Array.map (fun { Name = name; Uri = uri } -> name, Uri(uri) |> PathUri)
        |> Map.ofArray
    | _ ->
        let rootPath =
            par.RootUri
            |> Option.orElse par.RootPath
            |> Option.defaultWith (fun () -> failwith $"No folders configured in workspace: {par}")

        let rootUri = Uri(rootPath) |> PathUri

        let rootName =
            Path.GetFileName(rootUri.AbsolutePath)

        Map.ofList [ rootName, rootUri ]

let readWorkspace (roots: Map<string, PathUri>) : list<Folder> =
    seq {
        for KeyValue (name, root) in roots do
            match Folder.tryLoad name root with
            | Some folder -> yield folder
            | _ -> ()
    }
    |> List.ofSeq

let mkServerCaps (_pars: InitializeParams) : ServerCapabilities =
    let workspaceFoldersCaps =
        { Supported = Some true
          ChangeNotifications = Some true }

    let markdownFilePattern =
        { Glob = "**/*.md"
          Matches = Some FileOperationPatternKind.File
          Options = Some { FileOperationPatternOptions.Default with IgnoreCase = Some true } }

    let markdownFileRegistration =
        { Filters =
            [| { Scheme = None
                 Pattern = markdownFilePattern } |] }

    let workspaceFileCaps =
        { WorkspaceFileOperationsServerCapabilities.Default with
            DidCreate = Some markdownFileRegistration
            DidDelete = Some markdownFileRegistration
            // VSCode behaves weirdly when communicating file renames, so let's turn this off.
            // Anyway, when the file is renamed VSCode sends
            // - didClose on the old name, and
            // - didOpen on the new one
            // which is enough to keep the state in sync.
            DidRename = None }

    let workspaceCaps =
        { WorkspaceServerCapabilities.Default with
            WorkspaceFolders = Some workspaceFoldersCaps
            FileOperations = Some workspaceFileCaps }

    let textSyncCaps =
        { TextDocumentSyncOptions.Default with
            OpenClose = Some true
            Change = Some TextDocumentSyncKind.Incremental }

    { ServerCapabilities.Default with
        Workspace = Some workspaceCaps
        TextDocumentSync = Some textSyncCaps }

type MarksmanClient(_notSender: ClientNotificationSender, _reqSender: ClientRequestSender) =
    inherit LspClient()

type MarksmanServer(_client: MarksmanClient) =
    inherit LspServer()
    let mutable state: option<State> = None

    let logger =
        LogProvider.getLoggerByName "MarksmanServer"

    let updateState (newState: State) : unit =
        logger.trace (Log.setMessage $"Updating state: revision {newState.revision}")

        let newState =
            { newState with revision = newState.revision + 1 }

        state <- Some newState

        logger.trace (Log.setMessage $"Updated state: revision {newState.revision}")

    let requireState () : State =
        Option.defaultWith (fun _ -> failwith "State was not initialized") state

    override this.Initialize(par: InitializeParams) : AsyncLspResult<InitializeResult> =
        let workspaceFolders =
            extractWorkspaceFolders par

        logger.debug (
            Log.setMessage "Obtained workspace folders"
            >> Log.addContext "workspace" workspaceFolders
        )

        let folders = readWorkspace workspaceFolders

        let numNotes =
            folders |> List.sumBy (fun x -> x.documents.Count)

        logger.debug (
            Log.setMessage "Completed reading workspace folders"
            >> Log.addContext "numFolders" folders.Length
            >> Log.addContext "numNotes" numNotes
        )

        let state =
            { client = ClientDescription.fromParams par
              folders =
                folders
                |> List.map (fun x -> x.root, x)
                |> Map.ofList
              revision = 0 }

        updateState state

        let serverCaps = mkServerCaps par

        let initResult =
            { InitializeResult.Default with Capabilities = serverCaps }

        AsyncLspResult.success initResult


    override this.TextDocumentDidChange(par: DidChangeTextDocumentParams) =
        let state = requireState ()

        let docUri =
            par.TextDocument.Uri |> Uri |> PathUri

        let doc = State.tryFindDocument docUri state

        match doc with
        | Some doc ->
            let newDoc = Document.applyLspChange par doc

            let newState =
                State.updateDocument newDoc state

            updateState newState
        | _ ->
            logger.warn (
                Log.setMessage "Document not found"
                >> Log.addContext "method" "textDocumentDidChange"
                >> Log.addContext "uri" docUri
            )

        async.Return()

    override this.TextDocumentDidClose(par: DidCloseTextDocumentParams) =
        let path =
            par.TextDocument.Uri |> Uri |> PathUri

        let docFromDisk = Document.load path

        let newState =
            match docFromDisk with
            | Some doc -> State.updateDocument doc (requireState ())
            | _ -> State.removeDocument path (requireState ())

        updateState newState
        async.Return()

    override this.TextDocumentDidOpen(par: DidOpenTextDocumentParams) =
        let document =
            Document.fromLspDocument par.TextDocument

        let newState =
            State.updateDocument document (requireState ())

        updateState newState
        async.Return()

    override this.WorkspaceDidChangeWorkspaceFolders(par: DidChangeWorkspaceFoldersParams) =
        let state = requireState ()

        let newState =
            State.updateFoldersFromLsp par.Event.Added par.Event.Removed state

        updateState newState
        async.Return()


    override this.WorkspaceDidCreateFiles(par: CreateFilesParams) =
        let docUris =
            par.Files
            |> Array.map (fun fc -> PathUri.fromString fc.Uri)

        let mutable newState = requireState ()

        for docUri in docUris do
            logger.trace (
                Log.setMessage "Processing file create not"
                >> Log.addContext "uri" docUri
            )

            match Document.load docUri with
            | Some doc -> newState <- State.updateDocument doc newState
            | _ ->
                logger.warn (
                    Log.setMessage "Couldn't load created document"
                    >> Log.addContext "uri" docUri
                )

                ()

        updateState newState
        async.Return()

    override this.WorkspaceDidDeleteFiles(par: DeleteFilesParams) =
        let mutable newState = requireState ()

        let deletedUris =
            par.Files
            |> Array.map (fun x -> PathUri.fromString x.Uri)

        for uri in deletedUris do
            logger.trace (
                Log.setMessage "Processing file delete not"
                >> Log.addContext "uri" uri
            )

            newState <- State.removeDocument uri newState

        updateState newState
        async.Return()

    override this.Dispose() = ()