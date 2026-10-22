using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace AdaptiveRouting;

public sealed record CustomInstallPlan(int Schema, CustomUpdateManifest Manifest, string Runtime, string Package,
    string WorkDirectory, string InstallDirectory, string ConfigDirectory, string Executable, string PreviousExeSha256,
    int ParentPid, long ParentStartTicks, string Nonce, int[] ProbePorts);
public sealed record CustomHealth(int Build, string Nonce, int Pid, string Executable);

/// <summary>Runs in the copied helper after explicit UI confirmation. Never downloads after shutting down the old proxy.</summary>
public static class CustomUpdateInstaller
{
    public static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    public static void NoLinks(string path)
    {
        var directory = Path.GetFullPath(path);
        while (directory != null)
        {
            if ((File.Exists(directory) || Directory.Exists(directory)) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("更新路径不能经过符号链接或重解析点。");
            directory = Path.GetDirectoryName(directory);
        }
    }
    public static void ValidatePlan(CustomInstallPlan p)
    {
        if (p.Runtime is not ("win-x64" or "osx-arm64") || p.Schema != 1 || p.Manifest.Build <= CustomBuild.Current.Build || !Guid.TryParseExact(p.Nonce, "N", out _)
            || p.ParentPid <= 0 || p.ParentStartTicks <= 0 || p.ProbePorts.Any(x => x is < 1 or > 65535)) throw new InvalidDataException("安装计划无效或不是新版本。");
        if (p.InstallDirectory.Contains("/AppTranslocation/", StringComparison.Ordinal)) throw new IOException("请先将应用安装到可写的 Applications 目录。 ");
        p.Manifest.Validate(p.Runtime, p.Manifest.Version);
        foreach (var path in new[] { p.WorkDirectory, p.InstallDirectory, p.ConfigDirectory, p.Executable, p.Package })
        {
            if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path) throw new InvalidDataException("安装计划必须使用规范绝对路径。");
            NoLinks(path);
        }
        if (p.WorkDirectory == p.InstallDirectory || p.WorkDirectory == p.ConfigDirectory || p.WorkDirectory == Path.GetPathRoot(p.WorkDirectory)
            || p.InstallDirectory == Path.GetPathRoot(p.InstallDirectory) || p.WorkDirectory.StartsWith(p.InstallDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || p.InstallDirectory.StartsWith(p.WorkDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || p.ConfigDirectory.StartsWith(p.WorkDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("备份与安装目录边界无效。");
        var expected = p.Runtime == "win-x64" ? Path.Combine(p.InstallDirectory, "v2rayN.exe") : Path.Combine(p.InstallDirectory, "Contents", "MacOS", "v2rayN");
        if (p.Executable != expected || (p.Runtime == "osx-arm64" && !p.InstallDirectory.EndsWith(".app", StringComparison.Ordinal))
            || !File.Exists(p.Executable) || !Hash(p.Executable).Equals(p.PreviousExeSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("当前安装位置或程序摘要已改变。");
        if (!File.Exists(Path.Combine(p.ConfigDirectory, "guiNConfig.json"))) throw new InvalidDataException("没有找到当前用户配置，停止更新。");
    }

    public static async Task<int> RunAsync(CustomInstallPlan plan)
    {
        var result = Path.Combine(plan.WorkDirectory, "install-result.json");
        var journal = new List<string>();
        var backup = Path.Combine(plan.WorkDirectory, "backup-program");
        var configBackup = Path.Combine(plan.WorkDirectory, "backup-config");
        var health = Path.Combine(plan.WorkDirectory, "health.json");
        var macBackup = Path.Combine(Path.GetDirectoryName(plan.InstallDirectory)!, ".v2rayN-before-" + plan.Nonce + ".app");
        var macCandidate = Path.Combine(Path.GetDirectoryName(plan.InstallDirectory)!, ".v2rayN-update-" + plan.Nonce + ".app");
        Process? launched = null;
        var newStart = DateTime.MaxValue;
        var parentExited = false;
        var modified = false;
        void Report(string status, string? reason = null) => File.WriteAllText(result, JsonSerializer.Serialize(new { status, reason, version = plan.Manifest.Version, time = DateTimeOffset.UtcNow, backup, configBackup, macBackup, files = journal }, CustomUpdateManifest.Json));
        try
        {
            ValidatePlan(plan);
            if (!await CustomUpdateClient.VerifyAsync(plan.Package, plan.Manifest.Validate(plan.Runtime, plan.Manifest.Version))) throw new InvalidDataException("安装前校验失败。");
            CustomUpdateArchive.Validate(plan.Package, plan.Runtime);
            var stage = Path.Combine(plan.WorkDirectory, "payload");
            if (Directory.Exists(stage) || Directory.Exists(backup) || Directory.Exists(configBackup) || Directory.Exists(macCandidate) || Directory.Exists(macBackup))
                throw new IOException("更新工作目录已被使用，请重新准备安装。");
            Directory.CreateDirectory(stage);
            if (plan.Runtime == "win-x64") ZipFile.ExtractToDirectory(plan.Package, stage);
            else
            {
                await Tool("/usr/bin/ditto", ["-x", "-k", plan.Package, stage]);
                var bundle = Path.Combine(stage, "v2rayN Learning.app");
                await Tool("/usr/bin/codesign", ["--verify", "--deep", "--strict", bundle]);
                await Tool("/usr/bin/ditto", [bundle, macCandidate]); // Same-volume candidate, before touching the running app.
                await Tool("/usr/bin/codesign", ["--verify", "--deep", "--strict", macCandidate]);
            }
            var buildPath = plan.Runtime == "win-x64" ? Path.Combine(stage, "custom-build.json") : Path.Combine(stage, "v2rayN Learning.app", "Contents", "Resources", "custom-build.json");
            var build = JsonSerializer.Deserialize<CustomBuild>(File.ReadAllText(buildPath), CustomUpdateManifest.Json)!;
            using var info = JsonDocument.Parse(File.ReadAllText(Path.Combine(stage, "build-info.json")));
            if (build.Build != plan.Manifest.Build || build.Version != plan.Manifest.Version || info.RootElement.GetProperty("commit").GetString() != plan.Manifest.Commit)
                throw new InvalidDataException("安装包版本/提交与更新清单不一致。");
            // Test destination permissions before asking the parent to exit.
            var probe = Path.Combine(plan.InstallDirectory, ".update-write-" + plan.Nonce);
            await File.WriteAllTextAsync(probe, "write-check"); File.Delete(probe);
            using var parent = Process.GetProcessById(plan.ParentPid);
            if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartTicks || Path.GetFullPath(parent.MainModule!.FileName) != plan.Executable) throw new IOException("原程序身份已改变。");
            await File.WriteAllTextAsync(Path.Combine(plan.WorkDirectory, "prepared"), plan.Nonce);
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            parentExited = true;
            if (!Hash(plan.Executable).Equals(plan.PreviousExeSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("准备期间当前程序发生变化，取消替换。");
            CopyTree(plan.ConfigDirectory, configBackup);
            Directory.CreateDirectory(backup);
            if (plan.Runtime == "win-x64")
            {
                var files = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories)
                    .Select(x => Path.GetRelativePath(stage, x).Replace('\\', '/'))
                    .Where(x => CustomUpdateArchive.WindowsProgramFile(x) || x.StartsWith("docs/", StringComparison.Ordinal) || x.StartsWith(".agents/", StringComparison.Ordinal)).ToArray();
                if (!files.Contains("v2rayN.exe") || !files.Contains("CustomUpdater.exe")) throw new InvalidDataException("缺少客户端或独立更新程序。");
                foreach (var relative in files)
                {
                    var target = Path.Combine(plan.InstallDirectory, relative); NoLinks(target);
                    if (File.Exists(target)) CopyFile(target, Path.Combine(backup, relative));
                    journal.Add(relative);
                    Report("replacing"); // Record before changing each file so rollback also removes new files.
                    modified = true;
                    CopyFile(Path.Combine(stage, relative), target);
                }
            }
            else
            {
                Directory.Move(plan.InstallDirectory, macBackup); modified = true;
                Directory.Move(macCandidate, plan.InstallDirectory);
            }
            newStart = DateTime.UtcNow;
            launched = Start(plan, health);
            var healthy = await WaitHealthy(plan, launched, health, TimeSpan.FromSeconds(60));
            if (!healthy) throw new IOException("新版本未完成启动或原有代理端口未恢复。");
            Report("installed");
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                if (launched != null && !launched.HasExited) { launched.Kill(true); await launched.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                if (launched != null) StopStartedCores(plan, newStart);
                if (modified)
                {
                    if (plan.Runtime == "win-x64")
                    {
                        foreach (var relative in journal.AsEnumerable().Reverse())
                        {
                            var target = Path.Combine(plan.InstallDirectory, relative); NoLinks(target);
                            var saved = Path.Combine(backup, relative);
                            if (File.Exists(saved)) CopyFile(saved, target); else if (File.Exists(target)) File.Delete(target);
                        }
                    }
                    else if (Directory.Exists(macBackup))
                    {
                        if (Directory.Exists(plan.InstallDirectory)) Directory.Move(plan.InstallDirectory, macCandidate + ".failed");
                        Directory.Move(macBackup, plan.InstallDirectory);
                    }
                    RestoreTree(configBackup, plan.ConfigDirectory);
                }
                if (parentExited)
                {
                    var recoveryFile = Path.Combine(plan.WorkDirectory, "rollback-health.json");
                    using var recovered = Start(plan, recoveryFile);
                    if (!await WaitHealthy(plan, recovered, recoveryFile, TimeSpan.FromSeconds(60), CustomBuild.Current.Build))
                        throw new IOException("旧版文件已恢复，但客户端/代理未通过恢复检查，请查看备份目录。");
                }
                Report(modified ? "rolled-back" : "not-installed", ex.Message);
            }
            catch (Exception rollback) { Report("recovery-needed", ex.Message + "；回退失败：" + rollback.Message); }
            return 1;
        }
        finally { launched?.Dispose(); }
    }

    private static Process Start(CustomInstallPlan plan, string? health)
    {
        var start = new ProcessStartInfo(plan.Executable) { WorkingDirectory = Path.GetDirectoryName(plan.Executable)!, UseShellExecute = false, CreateNoWindow = true };
        start.Environment.Remove("V2RAYN_UPDATE_HEALTH_FILE"); start.Environment.Remove("V2RAYN_UPDATE_NONCE");
        if (health != null) { start.Environment["V2RAYN_UPDATE_HEALTH_FILE"] = health; start.Environment["V2RAYN_UPDATE_NONCE"] = plan.Nonce; }
        return Process.Start(start) ?? throw new IOException("无法启动客户端。");
    }
    private static async Task<bool> WaitHealthy(CustomInstallPlan plan, Process process, string file, TimeSpan limit, int? expectedBuild = null)
    {
        var end = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < end)
        {
            if (process.HasExited) return false;
            if (File.Exists(file))
            {
                try
                {
                    var health = JsonSerializer.Deserialize<CustomHealth>(await File.ReadAllTextAsync(file));
                    if (health?.Build == (expectedBuild ?? plan.Manifest.Build) && health.Nonce == plan.Nonce && health.Pid == process.Id && health.Executable == plan.Executable)
                    {
                        var ready = true;
                        foreach (var port in plan.ProbePorts) ready &= await Probe(port);
                        if (ready) { await Task.Delay(2000); return !process.HasExited; }
                    }
                }
                catch (IOException) { }
                catch (JsonException) { }
            }
            await Task.Delay(500);
        }
        return false;
    }
    private static void StopStartedCores(CustomInstallPlan plan, DateTime since)
    {
        // Only exact core paths belonging to this install/user-data directory and started with the new app.
        var bases = new[] { plan.InstallDirectory, Path.GetDirectoryName(plan.ConfigDirectory.TrimEnd(Path.DirectorySeparatorChar))! };
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var root in bases)
            foreach (var (folder, name) in new[] { ("xray", "xray"), ("sing_box", "sing-box"), ("mihomo", "mihomo"), ("v2fly", "v2ray") })
                paths.Add(Path.Combine(root, "bin", folder, name + (OperatingSystem.IsWindows() ? ".exe" : "")));
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.StartTime.ToUniversalTime() >= since && process.MainModule?.FileName is { } path && paths.Contains(path))
                    { process.Kill(true); process.WaitForExit(5000); }
                }
                catch { /* A departed process or another user's process is not ours to stop. */ }
            }
        }
    }

    public static async Task<bool> Probe(int port)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            using var socket = new TcpClient(); await socket.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
            var stream = socket.GetStream(); await stream.WriteAsync(new byte[] { 5, 1, 0 }, deadline.Token);
            var reply = new byte[2]; await stream.ReadExactlyAsync(reply, deadline.Token);
            return reply[0] == 5 && reply[1] == 0;
        }
        catch { return false; }
    }
    public static void CopyFile(string source, string target)
    {
        NoLinks(source); NoLinks(target); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target, true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(source));
    }
    public static void CopyTree(string source, string target)
    {
        NoLinks(source); NoLinks(target); Directory.CreateDirectory(target);
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
        {
            NoLinks(path); var next = Path.Combine(target, Path.GetFileName(path));
            if (Directory.Exists(path)) CopyTree(path, next); else CopyFile(path, next);
        }
    }
    private static void RestoreTree(string source, string target)
    {
        NoLinks(target);
        foreach (var path in Directory.EnumerateFileSystemEntries(target))
        {
            NoLinks(path); var saved = Path.Combine(source, Path.GetFileName(path));
            if (Directory.Exists(path))
            {
                if (Directory.Exists(saved)) RestoreTree(saved, path);
                // Keep newly created directories; they may contain data not owned by this installer.
            }
            else if (!File.Exists(saved)) File.Delete(path);
        }
        CopyTree(source, target);
    }
    private static async Task Tool(string executable, string[] args)
    {
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start(); var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        await output; var detail = await error;
        if (process.ExitCode != 0) throw new IOException(Path.GetFileName(executable) + " 校验/解包失败：" + detail);
    }
}
