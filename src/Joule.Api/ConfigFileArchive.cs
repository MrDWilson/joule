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
                // The demo mirrors its runtime settings, and may keep a sample apps.yaml so "Apply for me" can be tried safely.
                if(options.AllowedFiles.Length is <1 or >2 || options.AllowedFiles[0]!="runtime-settings.json" || options.AllowedFiles.Skip(1).Any(f=>f!=DemoAppsFile))throw new DomainException("Demo mirror requires runtime-settings.json (and optionally apps.yaml) only.",400);
                Directory.CreateDirectory(root!);CheckNoLinks(root!);
                if(Directory.EnumerateFileSystemEntries(root!).Any(p=>!DemoEntry(Path.GetFileName(p))))throw new DomainException("Demo mirror root must be a dedicated owned demo directory.",400);
                // The marker keeps its pre-rename name so existing demo directories stay recognised as Joule's own.
                var marker=Path.Combine(root!,".predbat-ai-demo");CheckNoLinks(marker);
                if(!File.Exists(marker))WriteProtected(marker,Encoding.UTF8.GetBytes("Joule owned demo configuration directory\n"));
                if(options.AllowedFiles.Contains(DemoAppsFile) && !File.Exists(Path.Combine(root!,DemoAppsFile)))WriteProtected(Path.Combine(root!,DemoAppsFile),Encoding.UTF8.GetBytes(DemoData.AppsYaml));
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
    public const string DemoAppsFile="apps.yaml";
    /// <summary>What the demo's own directory may hold: its files, the ownership marker and an interrupted write's temporary file.</summary>
    static bool DemoEntry(string name)=>name is "runtime-settings.json" or DemoAppsFile or ".predbat-ai-demo" || Regex.IsMatch(name,@"^(runtime-settings\.json|apps\.yaml)\.(joule-)?[a-f0-9]{32}\.tmp$");
    /// <summary>Demo reset: the sample apps.yaml goes back to how it started (the next capture records it).</summary>
    public void ResetDemoAppsYaml()
    {
        lock(gate)
        {
            if(!options.DemoRuntimeMirror || !options.AllowedFiles.Contains(DemoAppsFile))return;
            RequireEnabled();if(quarantined)throw new DomainException("Reconcile the demo file restore first.");
            var destination=Resolve(DemoAppsFile);var tmp=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{WriteProtected(tmp,Encoding.UTF8.GetBytes(DemoData.AppsYaml));CheckNoLinks(destination);File.Move(tmp,destination,true);}finally{if(File.Exists(tmp))File.Delete(tmp);}
        }
    }

    // ------------------------------------------------------------------ edits Joule makes itself (apps.yaml "Apply for me")

    /// <summary>The current text of one allowed file and its hash. Server-side only: callers mask it before it leaves the service.</summary>
    public (string Text, string Hash) ReadText(string file)
    {
        lock(gate){RequireEnabled();var current=ReadOne(file);return (new UTF8Encoding(false,true).GetString(current.Bytes),current.File.Hash);}
    }
    /// <summary>One file's exact bytes as stored in a version (for putting a snapshot back). Never sent to the browser.</summary>
    public byte[] ReadVersionFile(string id,string file)
    {
        lock(gate){RequireEnabled();var version=ReadVersion(id);var index=version.Files.FindIndex(x=>x.Path==file);if(index<0)throw new DomainException("File is not in this version.",404);return ReadArchived(version,index);}
    }
    public bool Allows(string file)=>Enabled && options.AllowedFiles.Contains(file,StringComparer.Ordinal);
    /// <summary>Whether Joule's process may change the file: it can open it for writing (the folder needn't be writable, see <see cref="ReplaceFile"/>).</summary>
    public bool CanWrite(string file)
    {
        lock(gate)
        {
            if(!Allows(file))return false;
            try{var path=Resolve(file);if(!File.Exists(path))return false;using var _=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite);return true;}
            catch(Exception e) when (e is UnauthorizedAccessException or IOException or DomainException){return false;}
        }
    }
    /// <summary>Test hook: always rewrite in place instead of writing a temporary file and renaming it.</summary>
    public bool ForceInPlaceWrite { get; set; }

    /// <summary>
    /// Replaces one allowed file with new contents: first a snapshot of the exact current bytes (the "before" version), then a durable
    /// journal, then an atomic write (a temporary file in the same folder renamed over the original, with the original's permissions and
    /// owner), then a re-read that confirms the bytes and a new "after" version. When the owner can't be kept (Joule runs as a different
    /// user) or the folder isn't writable, the file is rewritten in place instead, which keeps owner, permissions and links exactly.
    /// <paramref name="expectedHash"/> is the file the person reviewed: anything else is refused before a byte is written.
    /// </summary>
    public FileReplaceResult ReplaceFile(string file,byte[] bytes,string expectedHash,int revision,string reason,string beforeReason)
    {
        lock(gate)
        {
            RequireEnabled(); if(quarantined)throw new DomainException("A file operation needs reviewing in Files before Joule changes files again.");
            if(reason==null || reason.Length>4000 || beforeReason.Length>4000)throw new DomainException("Archive reason is too long.",400);
            if(bytes.Length>options.MaxFileBytes)throw new DomainException("The edited file would exceed the archive size limit.",400);
            ValidateText(bytes);
            var current=ReadOne(file);
            if(current.File.Hash!=expectedHash)throw new DomainException($"{file} has changed since you reviewed this edit. Review it again.");
            var before=Capture(revision,beforeReason,onlyIfChanged:true);
            if(before.Files.FirstOrDefault(x=>x.Path==file)?.Hash!=expectedHash)throw new DomainException($"{file} changed while Joule was saving a copy. Review it again.");
            var journal=new FileRestoreJournal{TargetVersion="",BeforeVersion=before.Id,Notes=reason};
            WriteJournal(journal); // Durable before the mounted file is touched.
            var destination=Resolve(file); var written=false;
            try
            {
                written=WriteFile(destination,file,bytes,expectedHash);
                var after=ReadCurrent();
                if(after.First(x=>x.File.Path==file).File.Hash!=Hash(bytes))throw new DomainException($"{file} didn't read back as written.");
                var version=WriteVersion(revision,reason,after,null);
                journal.CompletedFiles.Add(file); journal.CreatedVersion=version.Id; journal.Status="verified"; WriteJournal(journal);
                return new(before,version,expectedHash,Hash(bytes));
            }
            catch
            {
                // Nothing reached the file: close the journal without quarantine. Otherwise the outcome needs a person to look.
                var unchanged=false; try{unchanged=ReadOne(file).File.Hash==expectedHash;}catch{}
                if(unchanged && !written){journal.Status="rejected";}
                else{quarantined=true;journal.Status="uncertain";}
                try{WriteJournal(journal);}catch{quarantined=true;}
                throw;
            }
        }
    }

    /// <summary>Returns true once the destination has been replaced.</summary>
    bool WriteFile(string destination,string file,byte[] bytes,string expectedHash)
    {
        var mode=OperatingSystem.IsWindows()?default:File.GetUnixFileMode(destination);
        var owner=UnixOwner.Get(destination);
        if(!ForceInPlaceWrite)
        {
            var temporary=destination+".joule-"+Guid.NewGuid().ToString("N")+".tmp";
            var created=false;
            try
            {
                try{WriteProtected(temporary,bytes);created=true;}
                catch(Exception e) when (e is UnauthorizedAccessException or IOException){/* Folder not writable: rewrite in place below. */}
                if(created && (OperatingSystem.IsWindows() || KeepOwnerAndMode(temporary,mode,owner)))
                {
                    if(ReadOne(file).File.Hash!=expectedHash)throw new DomainException($"{file} changed just before Joule saved it. Nothing was changed.");
                    CheckNoLinks(destination); File.Move(temporary,destination,true); return true;
                }
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        // In place: the same file (owner, permissions, links) with new contents. The journal and snapshot cover an interruption.
        if(ReadOne(file).File.Hash!=expectedHash)throw new DomainException($"{file} changed just before Joule saved it. Nothing was changed.");
        CheckNoLinks(destination);
        using(var stream=new FileStream(destination,new FileStreamOptions{Mode=FileMode.Open,Access=FileAccess.Write,Share=FileShare.None,Options=FileOptions.WriteThrough}))
        {
            stream.SetLength(0);stream.Write(bytes);stream.Flush(true);
        }
        return true;
    }
    static bool KeepOwnerAndMode(string temporary,UnixFileMode mode,(uint Uid,uint Gid)? owner)
    {
        // Owner unknown on this platform, or it can't be kept (Joule runs as another user): rewrite in place instead.
        if(owner is not { } o || UnixOwner.Get(temporary) is not { } mine)return false;
        if(mine!=o && !UnixOwner.Set(temporary,o.Uid,o.Gid))return false;
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(temporary,mode);
        return true;
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
