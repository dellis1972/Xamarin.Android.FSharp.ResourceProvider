﻿namespace Xamarin.Android.FSharp

open System
open System.IO
open System.Reflection
open System.CodeDom.Compiler
open System.Collections.Generic
open Microsoft.CSharp
open FSharp.Quotations
open FSharp.Core.CompilerServices
open Microsoft.FSharp.Core.CompilerServices

[<TypeProvider>]
type ResourceProvider(config : TypeProviderConfig) =
    let mutable providedAssembly = None
    let invalidateEvent = Event<EventHandler,EventArgs>()

    let compiler = new CSharpCodeProvider()
    let (/) a b = Path.Combine(a,b)
    let pathToResources = config.ResolutionFolder/"Resources"
    let resourceFileName = pathToResources/"Resource.designer.cs"
    // watcher doesn't trigger when I specify the filename exactly
    let watcher = new FileSystemWatcher(pathToResources, "*.cs", EnableRaisingEvents=true)

    let generate sourceCode =
        let asm = sprintf "ProvidedTypes%s.dll" (Guid.NewGuid() |> string)
        let cp = CompilerParameters(
                    GenerateInMemory = false,
                    OutputAssembly = config.TemporaryFolder/asm,
                    TempFiles = new TempFileCollection(config.TemporaryFolder, false),
                    CompilerOptions = "/nostdlib /noconfig")

        let addRef ref = 
            cp.ReferencedAssemblies.Add ref |> ignore

        let addProjectReferences() =
            // This might add references that we don't need. Not sure it matters.
            let parentFolder = (Directory.GetParent config.ResolutionFolder)
            let packages = sprintf "%c%s%c" Path.DirectorySeparatorChar "packages" Path.DirectorySeparatorChar
            config.ReferencedAssemblies
            |> Array.filter(fun r -> File.Exists r && (r.StartsWith parentFolder.FullName || r.IndexOf(packages, StringComparison.OrdinalIgnoreCase) >= 0))
            |> Array.iter addRef

        let addReference assemblyFileName =
            printfn "Adding reference %s" assemblyFileName
            let reference =
                config.ReferencedAssemblies |> Array.tryFind(fun r -> r.EndsWith(sprintf "%c%s" Path.DirectorySeparatorChar assemblyFileName, StringComparison.InvariantCultureIgnoreCase)
                                                                      && r.IndexOf("Facade") = -1)

            match reference with
            | Some ref -> addRef ref
                          (Some ref, assemblyFileName)
            | None -> printfn "Did not find %s in referenced assemblies." assemblyFileName
                      None, assemblyFileName


        printfn "F# Android resource provider"
        let system = addReference "System.dll"
        let mscorlib = addReference "mscorlib.dll"
        let android = addReference "Mono.Android.dll"
        let nunitLite = addReference "Xamarin.Android.NUnitLite.dll"

        addProjectReferences()

        let addIfMissingReference addResult =
            match android, addResult with
            | (Some androidRef, _), (None, assemblyFileName) ->
                // When the TP is ran from XS, config.ReferencedAssemblies doesn't contain mscorlib or System.dll
                // but from xbuild, it does. Need to investigate why.
                let systemPath = Path.GetDirectoryName androidRef
                addRef (systemPath/".."/"v1.0"/assemblyFileName)
            | _, _ -> ()

        addIfMissingReference system
        addIfMissingReference mscorlib
        addIfMissingReference nunitLite

        let result = compiler.CompileAssemblyFromSource(cp, [| sourceCode |])
        if result.Errors.HasErrors then
            let errors = [ for e in result.Errors do yield e ] 
                         |> List.filter (fun e -> not e.IsWarning )

            if errors.Length > 0 then
                printfn "%A" errors
                failwithf "%A" errors

        let asm = Assembly.ReflectionOnlyLoadFrom cp.OutputAssembly

        let types = asm.GetTypes()
        let namespaces =
            let dict = Dictionary<_,List<_>>()
            for t in types do
                printfn "%A" t
                let namespc = if isNull t.Namespace then "global" else t.Namespace
                match dict.TryGetValue(namespc) with
                | true, ns -> ns.Add(t)
                | _, _ ->
                    let ns = List<_>()
                    ns.Add(t)
                    dict.Add(namespc, ns)
            dict
            |> Seq.map (fun kv ->
                { new IProvidedNamespace with
                    member x.NamespaceName = kv.Key
                    member x.GetNestedNamespaces() = [||] //FIXME
                    member x.GetTypes() = kv.Value.ToArray()
                    member x.ResolveTypeName(typeName: string) = null
                }
            )
            |> Seq.toArray
        providedAssembly <- Some(File.ReadAllBytes(result.PathToAssembly), namespaces)    

    let invalidate _ =
        printfn "Invalidating resources"
        invalidateEvent.Trigger(null, null)

    do
        printfn "Resource folder %s" config.ResolutionFolder
        printfn "Resource file name %s" resourceFileName
        watcher.Changed.Add invalidate
        watcher.Created.Add invalidate

        AppDomain.CurrentDomain.add_ReflectionOnlyAssemblyResolve(fun _ args ->
            let name = AssemblyName(args.Name)
            printfn "Resolving %s" args.Name
            let existingAssembly = 
                AppDomain.CurrentDomain.GetAssemblies()
                |> Seq.tryFind(fun a -> AssemblyName.ReferenceMatchesDefinition(name, a.GetName()))
            let asm = 
                match existingAssembly with
                | Some a -> printfn "Resolved to %s" a.Location
                            Assembly.ReflectionOnlyLoadFrom a.Location

                | None -> null
            asm)

        let source =
            if File.Exists resourceFileName then
                File.ReadAllText resourceFileName
            else
                let asm = Assembly.GetExecutingAssembly()
                let resourceNames = asm.GetManifestResourceNames()
                use stream = asm.GetManifestResourceStream("Resource.designer.cs")
                use reader = new StreamReader(stream)
                let source = reader.ReadToEnd()
                let namespc = (new DirectoryInfo(config.ResolutionFolder)).Name
                source.Replace("${Namespace}", namespc)
        generate source

    interface ITypeProvider with
        [<CLIEvent>]
        member x.Invalidate = invalidateEvent.Publish
        member x.GetStaticParameters(typeWithoutArguments) = [||]
        member x.GetGeneratedAssemblyContents(assembly) =
            match providedAssembly with
            | Some(bytes, _) -> bytes
            | _ -> failwith "Generate was never called"
        member x.GetNamespaces() =
            match providedAssembly with
            | Some(_, namespaces) -> namespaces
            | _ -> failwith "Generate was never called"
        member x.ApplyStaticArguments(typeWithoutArguments, typeNameWithArguments, staticArguments) = null
        member x.GetInvokerExpression(mb, parameters) = Expr.Call(mb :?> MethodInfo, Array.toList parameters)
        member x.Dispose() = 
            compiler.Dispose()
            watcher.Dispose()

[<assembly: TypeProviderAssembly>]
do()