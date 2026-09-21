// Prints one tab-separated line for a managed assembly: MVID, AssemblyConfiguration,
// AssemblyInformationalVersion. Reads metadata only; the assembly is never loaded.
//
// Used by assemble:cameraagent-final-head-535 for testAssemblies[]. It is a script
// rather than a project so the identity of the reader is the hash of this file and
// nothing else, and so a Release build of the solution is not a prerequisite of
// reading a Release assembly's identity.
open System.IO
open System.Reflection.Metadata
open System.Reflection.PortableExecutable

let path = fsi.CommandLineArgs.[1]
let stream = File.OpenRead(path)
let pe = new PEReader(stream)
let md = pe.GetMetadataReader()
let mvid = md.GetGuid(md.GetModuleDefinition().Mvid)
let asm = md.GetAssemblyDefinition()
let mutable configuration = ""
let mutable informational = ""
for handle in asm.GetCustomAttributes() do
    let attribute = md.GetCustomAttribute(handle)
    let ctorHandle = attribute.Constructor
    let typeName =
        match ctorHandle.Kind with
        | HandleKind.MemberReference ->
            let mr = md.GetMemberReference(MemberReferenceHandle.op_Explicit ctorHandle)
            match mr.Parent.Kind with
            | HandleKind.TypeReference ->
                let tr = md.GetTypeReference(TypeReferenceHandle.op_Explicit mr.Parent)
                md.GetString(tr.Name)
            | _ -> ""
        | _ -> ""
    if typeName = "AssemblyConfigurationAttribute" || typeName = "AssemblyInformationalVersionAttribute" then
        let mutable blob = md.GetBlobReader(attribute.Value)
        if blob.ReadUInt16() = 1us then
            let value = blob.ReadSerializedString()
            if typeName = "AssemblyConfigurationAttribute" then configuration <- value
            else informational <- value
pe.Dispose()
stream.Dispose()
printfn "%s\t%s\t%s" (mvid.ToString("D")) configuration informational
