module Main.Lang.GameLoader
open LSP.Json.Ser
open LSP
open LSP.Types
open System
open System.Runtime.InteropServices
open CWTools.Utilities.Position
open CWTools.Games
open System.IO
open CWTools.Localisation
open LSP.Types
open CWTools.Games.Files
open CWTools.Parser
open Main.Serialize
open CWTools.Common
open System.Text
open System.Reflection
open FParsec

// let loadSTL() =
//     let stlLocCommands =
//         configs |> List.tryFind (fun (fn, _) -> Path.GetFileName fn = "localisation.cwt")
//                 |> Option.bind (fun (fn, ft) -> if activeGame = STL then Some (fn, ft) else None)
//                 |> Option.map (fun (fn, ft) -> STLParser.loadLocCommands fn ft)
//                 |> Option.defaultValue []
//     let stlEventTargetLinks =
//         configs |> List.tryFind (fun (fn, _) -> Path.GetFileName fn = "links.cwt")
//                 |> Option.map (fun (fn, ft) -> UtilityParser.loadEventTargetLinks STLConstants.Scope.Any STLConstants.parseScope STLConstants.allScopes fn ft)
//                 |> Option.defaultValue (Scopes.STL.scopedEffects |> List.map SimpleLink)


//     let stlsettings = {
//         CWTools.Games.Stellaris.StellarisSettings.rootDirectory = path
//         scope = FilesScope.All
//         modFilter = None
//         scriptFolders = folders
//         excludeGlobPatterns = Some dontLoadPatterns
//         validation = {
//             validateVanilla = validateVanilla
//             experimental = experimental
//             langs = languages
//         }
//         rules = Some {
//             ruleFiles = configs
//             validateRules = true
//             debugRulesOnly = false
//             debugMode = false
//         }
//         embedded = {
//             triggers = triggers
//             effects = effects
//             modifiers = modifiers
//             embeddedFiles = cachedFiles
//             cachedResourceData = cached
//             localisationCommands = stlLocCommands
//             eventTargetLinks = stlEventTargetLinks
//         }
//         initialLookup = STLLookup()
//     }

let rec replaceFirst predicate value = function
    | [] -> []
    | h :: t when predicate h -> value :: t
    | h :: t -> h :: replaceFirst predicate value t

let fixEmbeddedFileName (s : string) =
    let count = (Seq.filter ((=) '.') >> Seq.length) s
    let mutable out = "//" + s
    [1 .. count - 1] |> List.iter (fun _ -> out <- (replaceFirst ((=) '.') '\\' (out |> List.ofSeq)) |> Array.ofList |> System.String )
    out

let rec getAllFolders dirs =
    if Seq.isEmpty dirs then Seq.empty else
        seq { yield! dirs |> Seq.collect Directory.EnumerateDirectories
              yield! dirs |> Seq.collect Directory.EnumerateDirectories |> getAllFolders }
let getAllFoldersUnion dirs =
    seq {
        yield! dirs
        yield! getAllFolders dirs
    }

let getConfigFiles cachePath useEmbeddedRules useManualRules manualRulesFolder =
    let embeddedConfigFiles =
        match cachePath, useEmbeddedRules, useManualRules with
        | Some path, false, false ->
            let configFiles = (getAllFoldersUnion ([path] |> Seq.ofList)) |> Seq.collect (Directory.EnumerateFiles)
            let configFiles = configFiles |> List.ofSeq |> List.filter (fun f -> Path.GetExtension f = ".cwt")
            configFiles |> List.map (fun f -> f, File.ReadAllText(f))
        | _ ->
            let embeddedConfigFileNames = Assembly.GetEntryAssembly().GetManifestResourceNames() |> Array.filter (fun f -> f.Contains("config.config") && f.EndsWith(".cwt"))
            embeddedConfigFileNames |> List.ofArray |> List.map (fun f -> fixEmbeddedFileName f, (new StreamReader(Assembly.GetEntryAssembly().GetManifestResourceStream(f))).ReadToEnd())
    let configpath = "Main.files.config.cwt"
    let configFiles =
        match useManualRules, manualRulesFolder with
        |true, Some rf ->
            let configFiles =
                if Directory.Exists rf
                then getAllFoldersUnion ([rf] |> Seq.ofList)
                else if Directory.Exists "./.cwtools" then getAllFoldersUnion (["./.cwtools"] |> Seq.ofList) else Seq.empty
            let configFiles = configFiles |> Seq.collect (Directory.EnumerateFiles)
            configFiles |> List.ofSeq |> List.filter (fun f -> Path.GetExtension f = ".cwt" || Path.GetExtension f = ".log")
        |_ ->
            let configFiles = (if Directory.Exists "./.cwtools" then getAllFoldersUnion (["./.cwtools"] |> Seq.ofList) else Seq.empty) |> Seq.collect (Directory.EnumerateFiles)
            configFiles |> List.ofSeq |> List.filter (fun f -> Path.GetExtension f = ".cwt" || Path.GetExtension f = ".log")
    let configs =
        match configFiles.Length > 0 with
        |true ->
            configFiles |> List.map (fun f -> f, File.ReadAllText(f))
            //["./config.cwt", File.ReadAllText("./config.cwt")]
        |false ->
            embeddedConfigFiles
    configs

let getFolderList (filename : string, filetext : string) =
    if Path.GetFileName filename = "folders.cwt"
    then Some (filetext.Split(([|"\r\n"; "\r"; "\n"|]), StringSplitOptions.None) |> List.ofArray)
    else None

type ServerSettings =
    {
        cachePath : string option
        useEmbeddedRules : bool
        useManualRules : bool
        manualRulesFolder : string option
        isVanillaFolder : bool
        path : string
        dontLoadPatterns : string list
        validateVanilla : bool
        languages : CWTools.Common.Lang list
        experimental : bool
    }

type GameLanguage = |STL |HOI4 |EU4 |CK2 |IR

let getCachedFiles (game : GameLanguage) cachePath isVanillaFolder =
    let timer = new System.Diagnostics.Stopwatch()
    timer.Start()
    let cached, cachedFiles =
        match (game, cachePath, isVanillaFolder) with
        | _, _, true ->
            eprintfn "Vanilla folder, so not loading cache"
            ([], [])
        | STL, Some cp, _ -> deserialize (cp + "/../stl.cwb")
        | EU4, Some cp, _ -> deserialize (cp + "/../eu4.cwb")
        | HOI4, Some cp, _ -> deserialize (cp + "/../hoi4.cwb")
        | CK2, Some cp, _ -> deserialize (cp + "/../ck2.cwb")
        | IR, Some cp, _ -> deserialize (cp + "/../ir.cwb")
        | _ -> ([], [])
    eprintfn "Parse cache time: %i" timer.ElapsedMilliseconds; timer.Restart()
    cached, cachedFiles

let loadEU4 (serverSettings : ServerSettings) =
    let cached, cachedFiles = getCachedFiles EU4 serverSettings.cachePath serverSettings.isVanillaFolder
    let configs = getConfigFiles serverSettings.cachePath serverSettings.useEmbeddedRules serverSettings.useManualRules serverSettings.manualRulesFolder
    let folders = configs |> List.tryPick getFolderList
    let eu4Mods =
        configs |> List.tryFind (fun (fn, _) -> Path.GetFileName fn = "modifiers.cwt")
                |> Option.map (fun (fn, ft) -> EU4Parser.loadModifiers fn ft)
                |> Option.defaultValue []

    let eu4LocCommands =
        configs |> List.tryFind (fun (fn, _) -> Path.GetFileName fn = "localisation.cwt")
                |> Option.map (fun (fn, ft) -> EU4Parser.loadLocCommands fn ft)
                |> Option.defaultValue []

    // let eu4Mods = EU4Parser.loadModifiers "eu4mods" ((new StreamReader(Assembly.GetEntryAssembly().GetManifestResourceStream(eu4modpath))).ReadToEnd())
    let eu4settings = {
        rootDirectory = serverSettings.path
        scriptFolders = folders
        excludeGlobPatterns = Some serverSettings.dontLoadPatterns
        embedded = {
            embeddedFiles = cachedFiles
            modifiers = eu4Mods
            cachedResourceData = cached
            triggers = []
            effects = []
            localisationCommands = eu4LocCommands
            eventTargetLinks = []
        }
        validation = {
            validateVanilla = serverSettings.validateVanilla;
            langs = serverSettings.languages
            experimental = serverSettings.experimental
        }
        rules = Some {
            ruleFiles = configs
            validateRules = true
            debugRulesOnly = false
            debugMode = false
        }
        scope = FilesScope.All
        modFilter = None
        initialLookup = EU4Lookup()
    }
    let game = CWTools.Games.EU4.EU4Game(eu4settings)
    game


let loadHOI4 serverSettings =
    let cached, cachedFiles = getCachedFiles HOI4 serverSettings.cachePath serverSettings.isVanillaFolder
    let configs = getConfigFiles serverSettings.cachePath serverSettings.useEmbeddedRules serverSettings.useManualRules serverSettings.manualRulesFolder
    let folders = configs |> List.tryPick getFolderList

    let hoi4modpath = "Main.files.hoi4.modifiers"
    let hoi4Mods =
        configs |> List.tryFind (fun (fn, _) -> Path.GetFileName fn = "modifiers.cwt")
                |> Option.map (fun (fn, ft) -> HOI4Parser.loadModifiers fn ft)
                |> Option.defaultValue []
    let hoi4LocCommands =
        configs |> List.tryFind (fun (fn, _) -> Path.GetFileName fn = "localisation.cwt")
                |> Option.map (fun (fn, ft) -> HOI4Parser.loadLocCommands fn ft)
                |> Option.defaultValue []
    // let hoi4Mods = HOI4Parser.loadModifiers "hoi4mods" ((new StreamReader(Assembly.GetEntryAssembly().GetManifestResourceStream(hoi4modpath))).ReadToEnd())

    let hoi4settings = {
        rootDirectory = serverSettings.path
        scriptFolders = folders
        excludeGlobPatterns = Some serverSettings.dontLoadPatterns
        embedded = {
            embeddedFiles = cachedFiles
            modifiers = hoi4Mods
            cachedResourceData = cached
            triggers = []
            effects = []
            localisationCommands = hoi4LocCommands
            eventTargetLinks = []
        }
        validation = {
            validateVanilla = serverSettings.validateVanilla;
            langs = serverSettings.languages
            experimental = serverSettings.experimental
        }
        rules = Some {
            ruleFiles = configs
            validateRules = true
            debugRulesOnly = false
            debugMode = false
        }
        scope = FilesScope.All
        modFilter = None
        initialLookup = HOI4Lookup()
    }
    let game = CWTools.Games.HOI4.HOI4Game(hoi4settings)
    game

let loadCK2 serverSettings =
    let cached, cachedFiles = getCachedFiles CK2 serverSettings.cachePath serverSettings.isVanillaFolder
    let configs = getConfigFiles serverSettings.cachePath serverSettings.useEmbeddedRules serverSettings.useManualRules serverSettings.manualRulesFolder
    let folders = configs |> List.tryPick getFolderList

    let ck2Mods =
        configs |> List.tryFind (fun (fn, _) -> Path.GetFileName fn = "modifiers.cwt")
                |> Option.map (fun (fn, ft) -> CK2Parser.loadModifiers fn ft)
                |> Option.defaultValue []

    let ck2LocCommands =
        configs |> List.tryFind (fun (fn, _) -> Path.GetFileName fn = "localisation.cwt")
                |> Option.map (fun (fn, ft) -> CK2Parser.loadLocCommands fn ft)
                |> Option.defaultValue []

    let ck2EventTargetLinks =
        configs |> List.tryFind (fun (fn, _) -> Path.GetFileName fn = "links.cwt")
                |> Option.map (fun (fn, ft) -> UtilityParser.loadEventTargetLinks CK2Constants.Scope.Any CK2Constants.parseScope CK2Constants.allScopes fn ft)
                |> Option.defaultValue (CWTools.Process.Scopes.CK2.scopedEffects |> List.map SimpleLink)

    // let ck2Mods = CK2Parser.loadModifiers "ck2mods" ((new StreamReader(Assembly.GetEntryAssembly().GetManifestResourceStream(ck2modpath))).ReadToEnd())
    let ck2settings = {
        rootDirectory = serverSettings.path
        scriptFolders = folders
        excludeGlobPatterns = Some serverSettings.dontLoadPatterns
        embedded = {
            embeddedFiles = cachedFiles
            modifiers = ck2Mods
            cachedResourceData = cached
            triggers = []
            effects = []
            localisationCommands = ck2LocCommands
            eventTargetLinks = ck2EventTargetLinks
        }
        validation = {
            validateVanilla = serverSettings.validateVanilla;
            langs = serverSettings.languages
            experimental = serverSettings.experimental
        }
        rules = Some {
            ruleFiles = configs
            validateRules = true
            debugRulesOnly = false
            debugMode = false
        }
        scope = FilesScope.All
        modFilter = None
        initialLookup = CK2Lookup()
    }
    let game = CWTools.Games.CK2.CK2Game(ck2settings)
    game

let loadIR serverSettings =
    let cached, cachedFiles = getCachedFiles IR serverSettings.cachePath serverSettings.isVanillaFolder
    let configs = getConfigFiles serverSettings.cachePath serverSettings.useEmbeddedRules serverSettings.useManualRules serverSettings.manualRulesFolder
    let folders = configs |> List.tryPick getFolderList

    let irMods =
        configs |> List.tryFind (fun (fn, _) -> Path.GetFileName fn = "modifiers.cwt")
                |> Option.map (fun (fn, ft) -> IRParser.loadModifiers fn ft)
                |> Option.defaultValue []

    let irLocCommands =
        configs |> List.tryFind (fun (fn, _) -> Path.GetFileName fn = "localisation.cwt")
                |> Option.map (fun (fn, ft) -> IRParser.loadLocCommands fn ft)
                |> Option.defaultValue []

    let irEventTargetLinks =
        configs |> List.tryFind (fun (fn, _) -> Path.GetFileName fn = "links.cwt")
                |> Option.map (fun (fn, ft) -> UtilityParser.loadEventTargetLinks IRConstants.Scope.Any IRConstants.parseScope IRConstants.allScopes fn ft)
                |> Option.defaultValue (CWTools.Process.Scopes.IR.scopedEffects |> List.map SimpleLink)

    let irEffects =
        configs |> List.tryFind (fun (fn, _) -> Path.GetFileName fn = "effects.log")
                |> Option.map (fun (fn, ft) -> JominiParser.parseEffectStream (new MemoryStream(System.Text.Encoding.GetEncoding(1252).GetBytes(ft))))
                |> Option.bind (function | Success(r,_,_) -> Some r | _ -> None)
                |> Option.map (JominiParser.processEffects IRConstants.parseScopes)
                |> Option.defaultValue []

    let irTriggers =
        configs |> List.tryFind (fun (fn, _) -> Path.GetFileName fn = "triggers.log")
                |> Option.map (fun (fn, ft) -> JominiParser.parseTriggerStream (new MemoryStream(System.Text.Encoding.GetEncoding(1252).GetBytes(ft))))
                |> Option.bind (function | Success(r,_,_) -> Some r | _ -> None)
                |> Option.map (JominiParser.processTriggers IRConstants.parseScopes)
                |> Option.defaultValue []

    // let ck2Mods = CK2Parser.loadModifiers "ck2mods" ((new StreamReader(Assembly.GetEntryAssembly().GetManifestResourceStream(ck2modpath))).ReadToEnd())
    let irsettings = {
        rootDirectory = serverSettings.path
        scriptFolders = folders
        excludeGlobPatterns = Some serverSettings.dontLoadPatterns
        embedded = {
            embeddedFiles = cachedFiles
            modifiers = irMods
            cachedResourceData = cached
            triggers = irTriggers
            effects = irEffects
            localisationCommands = irLocCommands
            eventTargetLinks = irEventTargetLinks
        }
        validation = {
            validateVanilla = serverSettings.validateVanilla;
            langs = serverSettings.languages
            experimental = serverSettings.experimental
        }
        rules = Some {
            ruleFiles = configs
            validateRules = true
            debugRulesOnly = false
            debugMode = false
        }
        scope = FilesScope.All
        modFilter = None
        initialLookup = IRLookup()
    }
    let game = CWTools.Games.IR.IRGame(irsettings)
    game

let loadSTL serverSettings =
    let cached, cachedFiles = getCachedFiles STL serverSettings.cachePath serverSettings.isVanillaFolder
    let configs = getConfigFiles serverSettings.cachePath serverSettings.useEmbeddedRules serverSettings.useManualRules serverSettings.manualRulesFolder
    let folders = configs |> List.tryPick getFolderList

    let docspath = "Main.files.trigger_docs_2.2.4.txt"
    let docs = DocsParser.parseDocsStream (Assembly.GetEntryAssembly().GetManifestResourceStream(docspath))

    let triggers, effects = (docs |> (function |Success(p, _, _) -> DocsParser.processDocs STLConstants.parseScopes p |Failure(e, _, _) -> eprintfn "%A" e; [], []))
    let logspath = "Main.files.setup.log"

    let timer = new System.Diagnostics.Stopwatch()
    timer.Start()

    let modfile = SetupLogParser.parseLogsStream (Assembly.GetEntryAssembly().GetManifestResourceStream(logspath))
    let modifiers = (modfile |> (function |Success(p, _, _) -> SetupLogParser.processLogs p))
    eprintfn "Parse setup.log time: %i" timer.ElapsedMilliseconds; timer.Restart()

    //let configs = [
    let stlLocCommands =
        configs |> List.tryFind (fun (fn, _) -> Path.GetFileName fn = "localisation.cwt")
                |> Option.map (fun (fn, ft) -> STLParser.loadLocCommands fn ft)
                |> Option.defaultValue []
    let stlEventTargetLinks =
        configs |> List.tryFind (fun (fn, _) -> Path.GetFileName fn = "links.cwt")
                |> Option.map (fun (fn, ft) -> UtilityParser.loadEventTargetLinks STLConstants.Scope.Any STLConstants.parseScope STLConstants.allScopes fn ft)
                |> Option.defaultValue (CWTools.Process.Scopes.STL.scopedEffects |> List.map SimpleLink)


    let stlsettings = {
        CWTools.Games.Stellaris.StellarisSettings.rootDirectory = serverSettings.path
        scope = FilesScope.All
        modFilter = None
        scriptFolders = folders
        excludeGlobPatterns = Some serverSettings.dontLoadPatterns
        validation = {
            validateVanilla = serverSettings.validateVanilla
            experimental = serverSettings.experimental
            langs = serverSettings.languages
        }
        rules = Some {
            ruleFiles = configs
            validateRules = true
            debugRulesOnly = false
            debugMode = false
        }
        embedded = {
            triggers = triggers
            effects = effects
            modifiers = modifiers
            embeddedFiles = cachedFiles
            cachedResourceData = cached
            localisationCommands = stlLocCommands
            eventTargetLinks = stlEventTargetLinks
        }
        initialLookup = STLLookup()
    }
    let game = CWTools.Games.Stellaris.STLGame(stlsettings)
    game