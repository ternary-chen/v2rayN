using AdaptiveRouting;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

int count = 0;
void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
async Task Test(string name, Func<Task> action) { await action(); Console.WriteLine("PASS " + name); count++; }
var root = Path.Combine(OperatingSystem.IsMacOS() ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : Path.GetTempPath(), "custom-update-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
var next = CustomBuild.Current.Build + 1;
var version = "v7.25.6-learning." + next;
CustomUpdateManifest Manifest(byte[] data, string runtime = "win-x64") => new(1, next, version, new string('a',40), "Update notes", [new(runtime,"https://github.com/ternary-chen/v2rayN/releases/download/"+version+"/client.zip",Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)),data.Length)]);
async Task Reject(Func<Task> action) { try { await action(); } catch (InvalidDataException) { return; } throw new Exception("Unsafe data accepted"); }
await Test("Manifest channel rejects upstream assets, insecure URLs, downgrade schema and duplicate architecture", async () =>
{
    var manifest=Manifest([1,2,3]); manifest.Validate("win-x64",version);
    foreach(var url in new[]{"http://github.com/ternary-chen/v2rayN/releases/download/"+version+"/client.zip", "https://github.com/2dust/v2rayN/releases/download/"+version+"/client.zip", "https://github.com.evil.test/ternary-chen/v2rayN/releases/download/"+version+"/client.zip"})
        await Reject(()=>{(manifest with { Packages=[manifest.Packages[0] with { Url=url }] }).Validate("win-x64",version);return Task.CompletedTask;});
    await Reject(()=>{(manifest with { Schema=7 }).Validate("win-x64",version);return Task.CompletedTask;});
    await Reject(()=>{(manifest with { Packages=[manifest.Packages[0],manifest.Packages[0]] }).Validate("win-x64",version);return Task.CompletedTask;});
});
await Test("Discover only published custom manifests and choose build sequence rather than GitHub array order", async () =>
{
    var low=Manifest([1]) with { Build=next,Version=version };
    var high=Manifest([1]) with { Build=next+1,Version="v7.25.6-learning."+(next+1) };
    high=high with {Packages=[high.Packages[0] with {Url=high.Packages[0].Url.Replace(version,high.Version)}]};
    object Release(CustomUpdateManifest m,bool draft=false)=>new {draft,tag_name=m.Version,assets=new[]{new {name="custom-update.json",browser_download_url=$"https://github.com/ternary-chen/v2rayN/releases/download/{m.Version}/custom-update.json"}}};
    using var http=new HttpClient(new Fake(req=>new StringContent(req.RequestUri!.Host=="api.github.com" ? JsonSerializer.Serialize(new[]{Release(low),Release(high)}) : JsonSerializer.Serialize(req.RequestUri.AbsolutePath.Contains(high.Version)?high:low))));
    var found=await new CustomUpdateClient(http).FindAsync("win-x64",CustomBuild.Current.Build,CancellationToken.None);
    Check(found?.Build==high.Build,"Wrong release order");
    Check(await new CustomUpdateClient(http).FindAsync("win-x64",high.Build,CancellationToken.None) is null,"Downgrade offered");
});
await Test("Download verifies bytes and SHA, reuses valid cache and rejects tampering without a ready package", async () =>
{
    byte[] bytes=[1,2,3,4,5];var manifest=Manifest(bytes);var requests=0;
    using var http=new HttpClient(new Fake(_=>{requests++;return new ByteArrayContent(bytes);}));
    var client=new CustomUpdateClient(http);var file=Path.Combine(root,"download.zip");
    await client.DownloadAsync(manifest,"win-x64",file,null,CancellationToken.None);
    await client.DownloadAsync(manifest,"win-x64",file,null,CancellationToken.None);
    Check(requests==1 && await CustomUpdateClient.VerifyAsync(file,manifest.Packages[0]),"Cache or hash verification failed");
    var wrong=manifest with {Packages=[manifest.Packages[0] with {Sha256=new string('0',64)}]};
    var invalid=Path.Combine(root,"invalid.zip");
    await Reject(()=>client.DownloadAsync(wrong,"win-x64",invalid,null,CancellationToken.None));
    Check(!File.Exists(invalid) && !File.Exists(invalid+".partial"),"Bad package retained as ready");
});
string Zip(string name, Action<ZipArchive> action)
{
    var file=Path.Combine(root,name+".zip");using var z=ZipFile.Open(file,ZipArchiveMode.Create);action(z);return file;
}
void Entry(ZipArchive z,string name,string content="data",bool link=false)
{
    var e=z.CreateEntry(name);if(link)e.ExternalAttributes=0xA1FF<<16;using var w=new StreamWriter(e.Open());w.Write(content);
}
await Test("Archive validation rejects traversal, private configuration and links escaping a Mac bundle", async () =>
{
    foreach(var name in new[]{"../outside","C:/outside","guiConfigs/guiNConfig.json","x/../../outside","x\\outside"})
    {
        var file=Zip(Guid.NewGuid().ToString("N"),z=>Entry(z,name));
        await Reject(()=>{CustomUpdateArchive.Validate(file,"win-x64");return Task.CompletedTask;});
    }
    var escaped=Zip("escaped-link",z=>Entry(z,"v2rayN Learning.app/Contents/MacOS/bad","../../../../outside",true));
    await Reject(()=>{CustomUpdateArchive.Validate(escaped,"osx-arm64");return Task.CompletedTask;});
    var writeThrough=Zip("write-through",z=>{Entry(z,"v2rayN Learning.app/Contents/MacOS/link","../Resources",true);Entry(z,"v2rayN Learning.app/Contents/MacOS/link/file");});
    await Reject(()=>{CustomUpdateArchive.Validate(writeThrough,"osx-arm64");return Task.CompletedTask;});
});
await Test("Valid Windows payload and Mac relative resource links pass validation", () =>
{
    var windows=Zip("valid-win",z=>{Entry(z,"v2rayN.exe");Entry(z,"CustomUpdater.exe");Entry(z,"build-info.json");Entry(z,"custom-build.json");});CustomUpdateArchive.Validate(windows,"win-x64");
    var mac=Zip("valid-mac",z=>{Entry(z,"v2rayN Learning.app/Contents/MacOS/v2rayN");Entry(z,"v2rayN Learning.app/Contents/MacOS/CustomUpdater");Entry(z,"build-info.json");Entry(z,"v2rayN Learning.app/Contents/Resources/custom-build.json");Entry(z,"v2rayN Learning.app/Contents/MacOS/custom-build.json","../Resources/custom-build.json",true);});CustomUpdateArchive.Validate(mac,"osx-arm64");
    Check(!CustomUpdateArchive.WindowsProgramFile("bin/xray/xray.exe") && !CustomUpdateArchive.WindowsProgramFile("guiConfigs/guiNDB.db"),"Core/user data selected for overwrite");
    return Task.CompletedTask;
});

async Task Tool(string name, params string[] args)
{
    using var p=new Process {StartInfo=new(name){UseShellExecute=false,RedirectStandardError=true,RedirectStandardOutput=true}};
    foreach(var arg in args)p.StartInfo.ArgumentList.Add(arg);
    p.Start();var output=p.StandardOutput.ReadToEndAsync();var error=p.StandardError.ReadToEndAsync();await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
    Check(p.ExitCode==0,name+": "+await error);await output;
}
if ((OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) && Environment.GetEnvironmentVariable("UPDATE_FIXTURE_DIR") is {Length:>0} fixture)
    foreach(var fail in new[]{false,true})
        await Test(fail?"Real failed replacement rolls back program version and configuration":"Real replacement waits for confirmation handoff and checks new process health",async()=>
        {
            var mac=OperatingSystem.IsMacOS();var runtime=mac?"osx-arm64":"win-x64";
            var test=Path.Combine(root,fail?"rollback":"success");var install=Path.Combine(test,mac?"client.app":"client");
            var work=Path.Combine(test,"work");var control=Path.Combine(test,"control");Directory.CreateDirectory(work);Directory.CreateDirectory(control);
            var bin=mac?Path.Combine(install,"Contents","MacOS"):install;Directory.CreateDirectory(bin);
            if (!mac) CustomUpdateInstaller.CopyTree(fixture,install);
            else
            {
                var resources=Path.Combine(install,"Contents","Resources");Directory.CreateDirectory(resources);
                foreach(var file in Directory.GetFiles(fixture))
                {
                    var name=Path.GetFileName(file);
                    if(name=="v2rayN") CustomUpdateInstaller.CopyFile(file,Path.Combine(bin,name));
                    else {CustomUpdateInstaller.CopyFile(file,Path.Combine(resources,name));File.CreateSymbolicLink(Path.Combine(bin,name),"../Resources/"+name);}
                }
                await File.WriteAllTextAsync(Path.Combine(install,"Contents","Info.plist"),"<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>CFBundleIdentifier</key><string>test.v2rayn.updater</string><key>CFBundleExecutable</key><string>v2rayN</string><key>CFBundlePackageType</key><string>APPL</string></dict></plist>");
            }
            var config=mac?Path.Combine(test,"userdata","guiConfigs"):Path.Combine(install,"guiConfigs");Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(config,"guiNConfig.json"),"original-private-config");
            var oldBuild=new CustomBuild(1,CustomBuild.Current.Build,CustomBuild.Current.Version);
            var buildPath=mac?Path.Combine(install,"Contents","Resources","custom-build.json"):Path.Combine(bin,"custom-build.json");
            await File.WriteAllTextAsync(buildPath,JsonSerializer.Serialize(oldBuild));
            if(mac) File.CreateSymbolicLink(Path.Combine(bin,"custom-build.json"),"../Resources/custom-build.json");
            string package;
            if(!mac) package=Zip("lifecycle-"+fail,z=>
            {
                foreach(var f in Directory.GetFiles(fixture)) z.CreateEntryFromFile(f,Path.GetFileName(f));
                Entry(z,"CustomUpdater.exe","fixture-only-not-launched");
                Entry(z,"custom-build.json",JsonSerializer.Serialize(new CustomBuild(1,next,version)));
                Entry(z,"build-info.json",JsonSerializer.Serialize(new {commit=new string('a',40)}));
            });
            else
            {
                await Tool("/usr/bin/codesign","--force","--sign","-",install);
                var stage=Path.Combine(test,"package-stage");Directory.CreateDirectory(stage);
                var app=Path.Combine(stage,"v2rayN Learning.app");await Tool("/usr/bin/ditto",install,app);
                await File.WriteAllTextAsync(Path.Combine(app,"Contents","Resources","custom-build.json"),JsonSerializer.Serialize(new CustomBuild(1,next,version)));
                await File.WriteAllTextAsync(Path.Combine(stage,"build-info.json"),JsonSerializer.Serialize(new {commit=new string('a',40)}));
                CustomUpdateInstaller.CopyFile(Path.Combine(app,"Contents","MacOS","v2rayN"), Path.Combine(app,"Contents","MacOS","CustomUpdater"));
                await Tool("/usr/bin/codesign","--force","--sign","-",Path.Combine(app,"Contents","MacOS","CustomUpdater"));
                await Tool("/usr/bin/codesign","--force","--sign","-",app);
                package=Path.Combine(test,"fixture.zip");await Tool("/usr/bin/ditto","-c","-k",stage,package);
            }
            var manifest=Manifest(await File.ReadAllBytesAsync(package),runtime);
            var exe=Path.Combine(bin,mac?"v2rayN":"v2rayN.exe");
            Environment.SetEnvironmentVariable("UPDATE_FIXTURE_CONTROL",control);
            Environment.SetEnvironmentVariable("UPDATE_FIXTURE_CONFIG",config);
            using var old=Process.Start(new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=bin})!;
            try
            {
                var end=DateTime.UtcNow.AddSeconds(10);while(!File.Exists(Path.Combine(control,"fixture-pid"))&&DateTime.UtcNow<end)await Task.Delay(50);
                Check(!old.HasExited,"Fixture failed to launch");
                Environment.SetEnvironmentVariable("UPDATE_FIXTURE_BASE_BUILD",CustomBuild.Current.Build.ToString());
                Environment.SetEnvironmentVariable("UPDATE_FIXTURE_FAIL",fail?"1":null);
                var plan=new CustomInstallPlan(1,manifest,runtime,package,work,install,config,exe,CustomUpdateInstaller.Hash(exe),old.Id,old.StartTime.ToUniversalTime().Ticks,Guid.NewGuid().ToString("N"),[]);
                var running=CustomUpdateInstaller.RunAsync(plan);
                end=DateTime.UtcNow.AddSeconds(10);while(!File.Exists(Path.Combine(work,"prepared"))&&!running.IsCompleted&&DateTime.UtcNow<end)await Task.Delay(50);
                Check(File.Exists(Path.Combine(work,"prepared")),"Preflight did not finish: "+(File.Exists(Path.Combine(work,"install-result.json"))?await File.ReadAllTextAsync(Path.Combine(work,"install-result.json")):"timeout"));
                Check(!old.HasExited && JsonSerializer.Deserialize<CustomBuild>(await File.ReadAllTextAsync(buildPath))!.Build==oldBuild.Build,"Installer replaced before parent handoff");
                await File.WriteAllTextAsync(Path.Combine(control,"stop-pid"),old.Id.ToString());
                var code=await running.WaitAsync(TimeSpan.FromSeconds(20));
                using var report=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(work,"install-result.json")));
                Check(report.RootElement.GetProperty("status").GetString()==(fail?"rolled-back":"installed"),"Wrong result: "+report.RootElement);
                Check(code==(fail?1:0),"Wrong exit status");
                Check(await File.ReadAllTextAsync(Path.Combine(config,"guiNConfig.json"))=="original-private-config","User config was not retained/restored");
                var current=JsonSerializer.Deserialize<CustomBuild>(await File.ReadAllTextAsync(buildPath))!;
                Check(current.Build==(fail?oldBuild.Build:next),"Wrong installed version after transaction");
            }
            finally
            {
                Environment.SetEnvironmentVariable("UPDATE_FIXTURE_FAIL",null);
                Environment.SetEnvironmentVariable("UPDATE_FIXTURE_BASE_BUILD",null);
                foreach(var process in Process.GetProcessesByName("v2rayN"))
                { using(process) try {if(process.MainModule?.FileName==exe){process.Kill(true);await process.WaitForExitAsync();}}catch{} }
                Environment.SetEnvironmentVariable("UPDATE_FIXTURE_CONTROL",null);Environment.SetEnvironmentVariable("UPDATE_FIXTURE_CONFIG",null);
            }
        });
Console.WriteLine($"{count} custom update checks passed.");

sealed class Fake(Func<HttpRequestMessage,HttpContent> response):HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=response(request)});
}
