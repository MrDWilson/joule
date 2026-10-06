using Joule;
using Xunit;
namespace Joule.Tests;
public class ConfigFileArchiveTests : IDisposable
{
    readonly string path=Path.Combine(OperatingSystem.IsMacOS()?"/private/tmp":Path.GetTempPath(),"predbat-files-"+Guid.NewGuid().ToString("N"));
    ConfigFileArchive Archive(params string[] files) { Directory.CreateDirectory(Path.Combine(path,"config")); return new(new(){Root=Path.Combine(path,"config"),ArchiveDirectory=Path.Combine(path,"archive"),AllowedFiles=files}); }
    [Fact] public void SnapshotIsImmutableExactBytesAndRedactsSecrets()
    {
        var a=Archive("apps.yaml"); var original="predbat:\n  password: super-secret\n  load_scaling: 1.08\n";
        File.WriteAllText(Path.Combine(path,"config/apps.yaml"),original); var v=a.Capture(1,"manual");
        File.WriteAllText(Path.Combine(path,"config/apps.yaml"),"changed");
        Assert.DoesNotContain("super-secret",a.View(v.Id,"apps.yaml").Text);
        Assert.Equal(v.Id,a.List().Single().Id);
        var bytes=File.ReadAllBytes(Path.Combine(path,"archive",v.Id,"0.bin")); Assert.Equal(System.Text.Encoding.UTF8.GetBytes(original),bytes);
        if(!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite,File.GetUnixFileMode(Path.Combine(path,"archive",v.Id,"0.bin")));
    }
    [Fact] public void RestoreRejectsStaleHashThenMakesNewVersion()
    {
        var a=Archive("apps.yaml"); var file=Path.Combine(path,"config/apps.yaml"); File.WriteAllText(file,"value: 1\n"); var first=a.Capture(1,"initial");
        File.WriteAllText(file,"value: 2\n"); var second=a.Capture(2,"external");
        Assert.Throws<DomainException>(()=>a.Restore(first.Id,second.Id,first.Files.ToDictionary(x=>x.Path,x=>x.Hash),2,"test"));
        var restored=a.Restore(first.Id,second.Id,second.Files.ToDictionary(x=>x.Path,x=>x.Hash),2,"test");
        Assert.Equal("value: 1\n",File.ReadAllText(file)); Assert.NotEqual(first.Id,restored.Id); Assert.Equal(first.Id,restored.RestoredFrom);
    }
    [Theory] [InlineData("../outside.yaml")] [InlineData("/etc/secret.yaml")] [InlineData("secrets.yaml")] [InlineData("script.sh")]
    public void RejectsUnsafeAllowlist(string relative) => Assert.Throws<DomainException>(()=>Archive(relative));
    [Fact] public void RejectsSymlinkAndOversizedBinaryFiles()
    {
        var a=Archive("apps.yaml"); Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path,"other.yaml"),"secret");
        File.CreateSymbolicLink(Path.Combine(path,"config/apps.yaml"),Path.Combine(path,"other.yaml"));
        Assert.Throws<DomainException>(()=>a.Capture(1,"test")); File.Delete(Path.Combine(path,"config/apps.yaml"));
        File.WriteAllBytes(Path.Combine(path,"config/apps.yaml"),[0,1,2]); Assert.Throws<DomainException>(()=>a.Capture(1,"test"));
    }
    [Fact] public void PartialRestoreIsJournalledAndRequiresReconciliation()
    {
        var options=new ConfigFileArchiveOptions{Root=Path.Combine(path,"config"),ArchiveDirectory=Path.Combine(path,"archive"),AllowedFiles=["a.yaml","b.yaml"]}; Directory.CreateDirectory(options.Root);
        foreach(var f in options.AllowedFiles) File.WriteAllText(Path.Combine(options.Root,f),"value: 1");
        var a=new ConfigFileArchive(options); var first=a.Capture(1,"initial");
        foreach(var f in options.AllowedFiles) File.WriteAllText(Path.Combine(options.Root,f),"value: 2"); var second=a.Capture(2,"changed");
        a.BeforeReplace=(index)=>{if(index==1)throw new IOException("injected");};
        Assert.Throws<IOException>(()=>a.Restore(first.Id,second.Id,second.Files.ToDictionary(x=>x.Path,x=>x.Hash),2,"test"));
        Assert.True(a.Status().Quarantined); Assert.True(new ConfigFileArchive(options).Status().Quarantined);
        Assert.Throws<DomainException>(()=>a.Restore(first.Id,second.Id,second.Files.ToDictionary(x=>x.Path,x=>x.Hash),2,"test"));
        a.Reconcile(2,"Reviewed partial restore"); Assert.False(a.Status().Quarantined);
    }
    [Fact] public void OversizedFileAndCorruptedSnapshotAreRejectedBeforeRestore()
    {
        var a=Archive("apps.yaml");var file=Path.Combine(path,"config/apps.yaml");File.WriteAllText(file,"value: 1");var first=a.Capture(1,"initial");
        File.WriteAllText(file,new string('x',1024*1024+1));Assert.Throws<DomainException>(()=>a.Capture(1,"oversized"));
        File.WriteAllText(file,"value: 2");var second=a.Capture(2,"changed");File.WriteAllText(Path.Combine(path,"archive",first.Id,"0.bin"),"tampered");
        Assert.Throws<IOException>(()=>a.Restore(first.Id,second.Id,second.Files.ToDictionary(x=>x.Path,x=>x.Hash),2,"restore"));
        Assert.Equal("value: 2",File.ReadAllText(file));Assert.False(a.Status().Quarantined);
    }
    [Fact] public void RestoreQuarantinesWhenAlreadyReplacedFileChangesBeforeCompletion()
    {
        var a=Archive("a.yaml","b.yaml");
        foreach(var f in new[]{"a.yaml","b.yaml"})File.WriteAllText(Path.Combine(path,"config",f),"value: 1");
        var first=a.Capture(1,"initial");
        foreach(var f in new[]{"a.yaml","b.yaml"})File.WriteAllText(Path.Combine(path,"config",f),"value: 2");
        var second=a.Capture(2,"changed");
        a.BeforeReplace=index=>{if(index==1)File.WriteAllText(Path.Combine(path,"config/a.yaml"),"external: 3");};
        Assert.Throws<DomainException>(()=>a.Restore(first.Id,second.Id,second.Files.ToDictionary(x=>x.Path,x=>x.Hash),2,"restore"));
        Assert.Equal("external: 3",File.ReadAllText(Path.Combine(path,"config/a.yaml")));
        Assert.True(a.Status().Quarantined);
        Assert.True(Archive("a.yaml","b.yaml").Status().Quarantined);
        Assert.DoesNotContain(a.List(),v=>v.RestoredFrom==first.Id);
    }
    [Fact] public void InterruptedJournalTemporaryFileDoesNotBlockRestartReconciliation()
    {
        var a=Archive("apps.yaml");var file=Path.Combine(path,"config/apps.yaml");
        File.WriteAllText(file,"value: 1");var first=a.Capture(1,"initial");
        File.WriteAllText(file,"value: 2");var second=a.Capture(2,"changed");
        a.BeforeReplace=_=>throw new IOException("interrupted restore");
        Assert.Throws<IOException>(()=>a.Restore(first.Id,second.Id,second.Files.ToDictionary(x=>x.Path,x=>x.Hash),2,"restore"));
        var journal=Assert.Single(Directory.GetFiles(Path.Combine(path,"archive"),"journal-*.json"));
        // A process termination can leave the previous fixed-name update incomplete.
        File.WriteAllText(journal+".tmp","interrupted journal write");
        var restarted=Archive("apps.yaml");Assert.True(restarted.Status().Quarantined);
        var reconciled=restarted.Reconcile(2,"Reviewed unchanged mounted file");
        Assert.False(restarted.Status().Quarantined);
        Assert.False(Archive("apps.yaml").Status().Quarantined);
        Assert.Equal("value: 2",File.ReadAllText(file));
        Assert.Equal(reconciled.Id,restarted.Status().LatestVersion);
    }
    [Fact] public void RestoreRechecksHashesImmediatelyBeforeEachReplacement()
    {
        var a=Archive("apps.yaml");var file=Path.Combine(path,"config/apps.yaml");File.WriteAllText(file,"value: 1");var first=a.Capture(1,"first");
        File.WriteAllText(file,"value: 2");var second=a.Capture(2,"second");
        a.BeforeReplace=_=>File.WriteAllText(file,"external: 3");
        Assert.Throws<DomainException>(()=>a.Restore(first.Id,second.Id,second.Files.ToDictionary(x=>x.Path,x=>x.Hash),2,"restore"));
        Assert.Equal("external: 3",File.ReadAllText(file));Assert.True(a.Status().Quarantined);
    }
    [Fact] public void RedactedDiffIdentifiesValueOnlyChangedLinesWithoutReturningContents()
    {
        var a=Archive("apps.yaml");var file=Path.Combine(path,"config/apps.yaml");File.WriteAllText(file,"load_scaling: 1.08\npassword: secret-one");var first=a.Capture(1,"first");
        File.WriteAllText(file,"load_scaling: 0.99\npassword: secret-two");var second=a.Capture(2,"second");var diff=a.Diff(first.Id,second.Id,"apps.yaml");
        Assert.True(diff.Changed);Assert.Contains("- ",diff.Before);Assert.Contains("+ ",diff.After);
        Assert.DoesNotContain("secret",diff.Before,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("secret",diff.After,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0.99",diff.After);Assert.DoesNotContain("1.08",diff.Before);
    }
    [Theory]
    [InlineData("password: |\n  abcDEFghijkLMNopQRstuv: ignored\n", "apps.yaml")]
    [InlineData("password: >-\n  abcDEFghijkLMNopQRstuv: ignored\n", "apps.yaml")]
    [InlineData("password: \"\n  abcDEFghijkLMNopQRstuv: ignored\n  \"\n", "apps.yaml")]
    [InlineData("password = \"\"\"\nabcDEFghijkLMNopQRstuv: ignored\n\"\"\"\n", "apps.toml")]
    [InlineData("password = '''\nabcDEFghijkLMNopQRstuv: ignored\n'''\n", "apps.toml")]
    public void ViewsAndDiffsHideKeyShapedMultilineScalarContent(string source,string relative)
    {
        var a=Archive(relative);var file=Path.Combine(path,"config",relative);File.WriteAllText(file,source);var first=a.Capture(1,"first");
        File.WriteAllText(file,source.Replace("ignored","changed"));var second=a.Capture(1,"second");
        Assert.DoesNotContain("abcDEFghijkLMNopQRstuv",a.View(first.Id,relative).Text);
        var diff=a.Diff(first.Id,second.Id,relative);
        Assert.True(diff.Changed);Assert.Contains("- ",diff.Before);Assert.Contains("+ ",diff.After);
        Assert.DoesNotContain("abcDEFghijkLMNopQRstuv",diff.Before);Assert.DoesNotContain("abcDEFghijkLMNopQRstuv",diff.After);
    }
    public void Dispose(){if(Directory.Exists(path)) Directory.Delete(path,true);}
}
