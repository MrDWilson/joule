using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Joule;

public sealed class ConfigFileArchiveOptions
{
    public string? Root { get; set; }
    public string ArchiveDirectory { get; set; } = "data/config-archive";
    public string[] AllowedFiles { get; set; } = [];
    public bool DemoRuntimeMirror { get; set; }
    public int MaxFileBytes { get; set; } = 1024 * 1024;
}
public record ArchivedConfigFile(string Path, string Hash, long Bytes);
public sealed class ConfigFileVersion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public int RuntimeRevision { get; set; }
    public string Reason { get; set; } = "";
    public string? RestoredFrom { get; set; }
    public List<ArchivedConfigFile> Files { get; set; } = [];
}
public record ConfigArchiveStatus(bool Enabled, bool Quarantined, string? LatestVersion, string[] AllowedFiles, string? Reason);
public record ConfigFileView(string Version, string File, string Hash, string Text, string Redaction);
/// <summary>Two masked copies of one file. Keys lists the settings that differ (added, removed or changed) without their values.</summary>
public record ConfigFileDiff(string From, string To, string File, bool Changed, string Before, string After, string Redaction, List<ConfigKeyChange>? Keys = null);
public sealed class FileRestoreJournal
{
    public string Id { get; set; }=Guid.NewGuid().ToString("N");
    public string Status { get; set; }="pending";
    public string TargetVersion { get; set; }="";
    public string BeforeVersion { get; set; }="";
    public string? CreatedVersion { get; set; }
    public List<string> CompletedFiles { get; set; }=[];
    public DateTimeOffset At { get; set; }=DateTimeOffset.UtcNow;
    public string Notes { get; set; }="";
}
/// <summary>Exact-byte snapshots are private. Only masked views leave this service: keys and layout, never values or comments
/// (see <see cref="ConfigFileMask"/>).</summary>
public sealed class ConfigFileArchive
{
    readonly object gate=new();
    readonly ConfigFileArchiveOptions options;
    readonly string archive;
    readonly string? root;
    bool quarantined;
    public Action<int>? BeforeReplace { get; set; } // Fault injection for filesystem recovery tests.
    public bool Enabled => root!=null && options.AllowedFiles.Length>0;
    public ConfigFileArchive(ConfigFileArchiveOptions options)
    {
        this.options=options; archive=Path.GetFullPath(options.ArchiveDirectory);
        root=string.IsNullOrWhiteSpace(options.Root)?null:Path.GetFullPath(options.Root);
        if(options.MaxFileBytes is <1 or >4*1024*1024 || options.AllowedFiles.Length>20) throw new DomainException("Archive limits are invalid.",400);
        foreach(var file in options.AllowedFiles) ValidateRelative(file);
        if(options.AllowedFiles.Distinct(StringComparer.Ordinal).Count()!=options.AllowedFiles.Length) throw new DomainException("Duplicate allowed files.",400);
        if(Enabled)
        {
            CheckNoLinks(root!); CheckNoLinks(archive);
            if(archive==root || archive.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.Ordinal)) throw new DomainException("Archive storage must be outside the mounted configuration root.",400);
            if(options.DemoRuntimeMirror)
            {
                if(options.AllowedFiles.Length!=1 || options.AllowedFiles[0]!="runtime-settings.json")throw new DomainException("Demo mirror requires only runtime-settings.json.",400);
                Directory.CreateDirectory(root!);CheckNoLinks(root!);
                if(Directory.EnumerateFileSystemEntries(root!).Any(p=>Path.GetFileName(p) is not ("runtime-settings.json" or DataFiles.DemoMarker or DataFiles.LegacyDemoMarker)))throw new DomainException("Demo mirror root must be a dedicated owned demo directory.",400);
                var marker=Path.Combine(root!,DataFiles.DemoMarker);CheckNoLinks(marker);
                var legacyMarker=Path.Combine(root!,DataFiles.LegacyDemoMarker);CheckNoLinks(legacyMarker);
                // A demo folder from before the rename carries the old marker: rename it once, or keep it if that fails.
                if(!File.Exists(marker) && File.Exists(legacyMarker)){ try{File.Move(legacyMarker,marker);} catch(Exception e) when(e is IOException or UnauthorizedAccessException){} }
                if(!File.Exists(marker) && !File.Exists(legacyMarker))WriteProtected(marker,Encoding.UTF8.GetBytes("Joule owned demo configuration directory\n"));
            }
            Directory.CreateDirectory(archive); ProtectDirectory(archive);
            quarantined=ReadJournals().Any(x=>x.Status is "pending" or "partial" or "uncertain" or "awaiting_state");
        }
    }
    void RequireEnabled(){ if(!Enabled) throw new DomainException("File archive is not configured. Set a local mounted root and explicit allowed files.",409); }
    public ConfigArchiveStatus Status(){lock(gate) return new(Enabled,quarantined,Enabled?List().LastOrDefault()?.Id:null,options.AllowedFiles.ToArray(),quarantined?"An incomplete file restore requires manual review and reconciliation.":null);}
    public List<ConfigFileVersion> List()
    {
        lock(gate)
        {
            if(!Enabled)return [];
            CheckNoLinks(archive);
            return Directory.EnumerateDirectories(archive).Where(p=>Regex.IsMatch(Path.GetFileName(p),"^[a-f0-9]{32}$"))
                .Select(p=>ReadVersion(Path.GetFileName(p))).OrderBy(x=>x.At).ThenBy(x=>x.Id).ToList();
        }
    }
    public ConfigFileVersion Capture(int revision,string reason,bool onlyIfChanged=false)
    {
        lock(gate)
        {
            RequireEnabled(); if(reason==null || reason.Length>4000)throw new DomainException("Archive reason is too long.",400);
            var content=ReadCurrent(); var last=List().LastOrDefault();
            if(onlyIfChanged && last!=null && SameHashes(last,content)) return last;
            return WriteVersion(revision,reason,content,null);
        }
    }
    public ConfigFileView View(string id,string file)
    {
        lock(gate)
        {
            RequireEnabled(); var version=ReadVersion(id); var index=version.Files.FindIndex(x=>x.Path==file);
            if(index<0)throw new DomainException("File is not in this version.",404);
            var bytes=ReadArchived(version,index);
            return new(id,file,version.Files[index].Hash,ConfigFileMask.Render(Masked(file,bytes)),ViewNote);
        }
    }
    public const string ViewNote="Keys and layout are shown; every value and comment is hidden (•••), so secrets never reach the browser. The exact file stays in protected storage.";
    public ConfigFileDiff Diff(string from,string to,string file)
    {
        lock(gate)
        {
            RequireEnabled();
            var beforeVersion=ReadVersion(from);var afterVersion=ReadVersion(to);
            int Index(ConfigFileVersion v){var i=v.Files.FindIndex(x=>x.Path==file);return i>=0?i:throw new DomainException("File is not in this version.",404);}
            var beforeIndex=Index(beforeVersion);var afterIndex=Index(afterVersion);
            var before=Masked(file,ReadArchived(beforeVersion,beforeIndex));var after=Masked(file,ReadArchived(afterVersion,afterIndex));
            var (markedBefore,markedAfter)=ConfigFileMask.MarkedDiff(before,after);
            var changed=beforeVersion.Files[beforeIndex].Hash!=afterVersion.Files[afterIndex].Hash;
            return new(from,to,file,changed,markedBefore,markedAfter,"Values stay hidden. Minus and plus mark the lines that differ, including changes to a value only.",ConfigFileMask.Changes(before,after));
        }
    }
    public ConfigFileVersion Restore(string targetId,string expectedVersion,Dictionary<string,string> expectedHashes,int revision,string notes,bool deferCompletion=false)
    {
        lock(gate)
        {
            RequireEnabled(); if(quarantined)throw new DomainException("File restore is quarantined. Review mounted files and reconcile first.");
            if(notes==null || notes.Length>4000)throw new DomainException("Restore notes must be at most 4000 characters.",400);
            var latest=List().LastOrDefault();
            if(latest==null || latest.Id!=expectedVersion)throw new DomainException("File history changed. Refresh before restoring.");
            var target=ReadVersion(targetId); var current=ReadCurrent();
            if(expectedHashes==null || expectedHashes.Count!=current.Count || current.Any(x=>!expectedHashes.TryGetValue(x.File.Path,out var hash) || hash!=x.File.Hash))throw new DomainException("Mounted file hashes changed. Capture and review the new version before restoring.");
            if(!SameHashes(latest,current))throw new DomainException("Uncaptured external file changes must be reviewed before restoring.");
            if(!target.Files.Select(x=>x.Path).Order().SequenceEqual(options.AllowedFiles.Order()))throw new DomainException("The version does not match the current explicit file allowlist.");
            var replacements=target.Files.Select((f,i)=>(f.Path,Bytes:ReadArchived(target,i))).ToList();
            var journal=new FileRestoreJournal{TargetVersion=target.Id,BeforeVersion=latest.Id,Notes=notes};
            WriteJournal(journal); // Durable before any mounted file is touched.
            try
            {
                for(var i=0;i<replacements.Count;i++)
                {
                    var item=replacements[i]; var destination=Resolve(item.Path); var now=ReadOne(item.Path);
                    if(now.File.Hash!=expectedHashes[item.Path])throw new DomainException("A file changed during restore; partial results require review.");
                    BeforeReplace?.Invoke(i);
                    var temporary=destination+".joule-"+Guid.NewGuid().ToString("N")+".tmp";
                    try
                    {
                        WriteProtected(temporary,item.Bytes);
                        if(ReadOne(item.Path).File.Hash!=expectedHashes[item.Path])throw new DomainException("A file changed immediately before replacement; review partial restore results.");
                        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(temporary,File.GetUnixFileMode(destination));
                        CheckNoLinks(destination); File.Move(temporary,destination,true);
                    }
                    finally { if(File.Exists(temporary))File.Delete(temporary); }
                    journal.CompletedFiles.Add(item.Path); WriteJournal(journal);
                }
                var restored=ReadCurrent();
                if(!SameHashes(target,restored))throw new DomainException("A restored file changed before completion; review and reconcile the mounted files.");
                var version=WriteVersion(revision,"Manual restore: "+notes,restored,target.Id);
                journal.CreatedVersion=version.Id; journal.Status=deferCompletion?"awaiting_state":"verified"; WriteJournal(journal); if(deferCompletion)quarantined=true; return version;
            }
            catch
            {
                quarantined=true; journal.Status=journal.CompletedFiles.Count==0?"uncertain":"partial";
                try{WriteJournal(journal);}catch{} // The durable pending journal still quarantines restart.
                throw;
            }
        }
    }
    public ConfigFileVersion Reconcile(int revision,string notes)
    {
        lock(gate)
        {
            RequireEnabled(); if(string.IsNullOrWhiteSpace(notes)||notes.Length>4000)throw new DomainException("Record your review of mounted files before reconciliation.",400);
            var captured=Capture(revision,"Manual file reconciliation: "+notes);
            foreach(var j in ReadJournals().Where(x=>x.Status is "pending" or "partial" or "uncertain" or "awaiting_state")){j.Status="reconciled";j.Notes=notes;WriteJournal(j);}
            quarantined=false;return captured;
        }
    }
    public void VerifyCurrent(string? expectedVersion)
    {
        lock(gate)
        {
            RequireEnabled();if(quarantined)throw new DomainException("File archive requires reconciliation first.");
            var latest=List().LastOrDefault();if(latest==null || latest.Id!=expectedVersion || !SameHashes(latest,ReadCurrent()))throw new DomainException("Mounted files or history changed. Capture and review them before acknowledging runtime reload.");
        }
    }
    public void CompleteRestore(string version)
    {
        lock(gate)
        {
            var journal=ReadJournals().SingleOrDefault(x=>x.CreatedVersion==version&&x.Status=="awaiting_state")??throw new DomainException("Pending file restore not found.");
            journal.Status="verified";WriteJournal(journal);quarantined=ReadJournals().Any(x=>x.Status is "pending" or "partial" or "uncertain" or "awaiting_state");
        }
    }
    public bool DemoRuntimeMirror => options.DemoRuntimeMirror;
    public void MirrorDemoRuntime(AppState state)
    {
        lock(gate)
        {
            if(!options.DemoRuntimeMirror)return;
            if(state.DataSource!="Demo")throw new DomainException("Demo mirror cannot write live configuration.");
            RequireEnabled();if(quarantined)throw new DomainException("Reconcile the demo file restore first.");
            var destination=Resolve("runtime-settings.json");
            var bytes=JsonSerializer.SerializeToUtf8Bytes(state.Settings.OrderBy(x=>x.Key).ToDictionary(x=>x.Key,x=>x.Value),new JsonSerializerOptions(JsonDefaults.Options){WriteIndented=true});
            var tmp=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{WriteProtected(tmp,bytes);CheckNoLinks(destination);File.Move(tmp,destination,true);}finally{if(File.Exists(tmp))File.Delete(tmp);}
        }
    }
    public void Quarantine(string reason)
    {
        lock(gate){RequireEnabled();quarantined=true;WriteJournal(new FileRestoreJournal{Status="uncertain",Notes=reason,BeforeVersion=List().LastOrDefault()?.Id??""});}
    }
    static void ValidateRelative(string file)
    {
        if(string.IsNullOrWhiteSpace(file)||Path.IsPathRooted(file)||file.Contains('\\')||file.Split('/').Any(x=>x is "" or "." or "..")||file.Any(char.IsControl)||!Regex.IsMatch(file,@"^[a-zA-Z0-9_./-]+\.(yaml|yml|json|toml|cfg|conf)$")||Regex.IsMatch(file,@"(secret|credential|token|password|\.env)",RegexOptions.IgnoreCase))
            throw new DomainException("Allow only explicit relative configuration text files; secrets files, traversal and unsupported types are rejected.",400);
    }
    string Resolve(string relative)
    {
        ValidateRelative(relative); if(!options.AllowedFiles.Contains(relative,StringComparer.Ordinal))throw new DomainException("File is not explicitly allowed.",400);
        var full=Path.GetFullPath(Path.Combine(root!,relative));
        if(!full.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.Ordinal))throw new DomainException("Path leaves configured root.",400);
        CheckNoLinks(full); return full;
    }
    static void CheckNoLinks(string path)
    {
        var full=Path.GetFullPath(path); var current=Path.GetPathRoot(full)!;
        foreach(var part in full[current.Length..].Split(Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries))
        {
            current=Path.Combine(current,part); var info=new FileInfo(current);
            if(info.LinkTarget!=null || (info.Exists && (info.Attributes&FileAttributes.ReparsePoint)!=0) || new DirectoryInfo(current).LinkTarget!=null)
                throw new DomainException("Symbolic links are not allowed in archive or configuration paths.",400);
        }
    }
    record Content(ArchivedConfigFile File,byte[] Bytes);
    List<Content> ReadCurrent()=>options.AllowedFiles.Select(ReadOne).ToList();
    Content ReadOne(string file)
    {
        var path=Resolve(file);var info=new FileInfo(path);
        if(!info.Exists)throw new DomainException("An allowed configuration file is missing.",409);
        if(info.Length>options.MaxFileBytes)throw new DomainException("Configuration file exceeds archive size limit.",400);
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(stream.Length>options.MaxFileBytes)throw new DomainException("Configuration file exceeds archive size limit.",400);
        var stamp=File.GetLastWriteTimeUtc(path);var bytes=new byte[checked((int)stream.Length)];stream.ReadExactly(bytes);
        CheckNoLinks(path);if(stream.Length!=bytes.Length || File.GetLastWriteTimeUtc(path)!=stamp)throw new DomainException("A configuration file changed while being captured. Retry after it is stable.");
        ValidateText(bytes);
        return new(new(file,Hash(bytes),bytes.Length),bytes);
    }
    static void ValidateText(byte[] bytes)
    {
        try{var text=new UTF8Encoding(false,true).GetString(bytes);if(text.Any(c=>char.IsControl(c)&&c is not ('\r' or '\n' or '\t')))throw new DecoderFallbackException();}
        catch(DecoderFallbackException){throw new DomainException("Archive supports UTF-8 configuration text only.",400);}
    }
    static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    static bool SameHashes(ConfigFileVersion version,List<Content> content)=>version.Files.Count==content.Count && content.All(x=>version.Files.Any(f=>f.Path==x.File.Path&&f.Hash==x.File.Hash));
    ConfigFileVersion WriteVersion(int revision,string reason,List<Content> content,string? restoredFrom)
    {
        CheckNoLinks(archive);var version=new ConfigFileVersion{RuntimeRevision=revision,Reason=reason,RestoredFrom=restoredFrom,Files=content.Select(x=>x.File).ToList()};
        var staging=Path.Combine(archive,"staging-"+version.Id); Directory.CreateDirectory(staging);ProtectDirectory(staging);
        try
        {
            for(var i=0;i<content.Count;i++)WriteProtected(Path.Combine(staging,$"{i}.bin"),content[i].Bytes);
            WriteProtected(Path.Combine(staging,"manifest.json"),JsonSerializer.SerializeToUtf8Bytes(version,JsonDefaults.Options));
            Directory.Move(staging,Path.Combine(archive,version.Id));return version;
        }
        catch{if(Directory.Exists(staging))Directory.Delete(staging,true);throw;}
    }
    ConfigFileVersion ReadVersion(string id)
    {
        if(!Regex.IsMatch(id??"","^[a-f0-9]{32}$"))throw new DomainException("Invalid file version.",400);
        var path=Path.Combine(archive,id!,"manifest.json");CheckNoLinks(path);
        if(!File.Exists(path))throw new DomainException("File version not found.",404);
        var version=JsonSerializer.Deserialize<ConfigFileVersion>(File.ReadAllBytes(path),JsonDefaults.Options)??throw new IOException("Invalid archive manifest");
        if(version.Id!=id||version.Files.Count>20)throw new IOException("Invalid archive manifest"); foreach(var f in version.Files)ValidateRelative(f.Path); return version;
    }
    byte[] ReadArchived(ConfigFileVersion version,int index)
    {
        var path=Path.Combine(archive,version.Id,$"{index}.bin"); CheckNoLinks(path);
        if(new FileInfo(path).Length>options.MaxFileBytes)throw new IOException("Archived file exceeds size limit");
        var bytes=File.ReadAllBytes(path);if(Hash(bytes)!=version.Files[index].Hash)throw new IOException("Archived file integrity check failed");ValidateText(bytes);return bytes;
    }
    static List<MaskedLine> Masked(string file,byte[] bytes)=>ConfigFileMask.Mask(file,new UTF8Encoding(false,true).GetString(bytes));
    List<FileRestoreJournal> ReadJournals()=>Directory.EnumerateFiles(archive,"journal-*.json").Select(p=>{CheckNoLinks(p);return JsonSerializer.Deserialize<FileRestoreJournal>(File.ReadAllBytes(p),JsonDefaults.Options)!;}).ToList();
    void WriteJournal(FileRestoreJournal journal)
    {
        var path=Path.Combine(archive,"journal-"+journal.Id+".json");CheckNoLinks(path);var tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{WriteProtected(tmp,JsonSerializer.SerializeToUtf8Bytes(journal,JsonDefaults.Options));File.Move(tmp,path,true);}finally{if(File.Exists(tmp))File.Delete(tmp);}
    }
    static void ProtectDirectory(string path){if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);}
    static void WriteProtected(string path,byte[] bytes)
    {
        var opts=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None,Options=FileOptions.WriteThrough};
        if(!OperatingSystem.IsWindows())opts.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
        using var stream=new FileStream(path,opts);stream.Write(bytes);stream.Flush(true);
    }
}
