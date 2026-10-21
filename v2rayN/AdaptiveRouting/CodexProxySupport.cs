using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AdaptiveRouting;

public sealed record CodexConfigChange(string BackupPath, string AppliedSha256, bool OriginalExisted = true, int? OriginalUnixMode = null);

public static class CodexProxySupport
{
    public static string DefaultHome => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home
        ? home : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    /// <summary>Conservative, line-preserving edit of the single feature. Ambiguous TOML
    /// layouts are refused instead of rewriting unrelated settings or credentials.</summary>
    public static string EnableSystemProxy(string text)
    {
        if (text.Contains("\"\"\"", StringComparison.Ordinal) || text.Contains("'''", StringComparison.Ordinal))
            throw new InvalidOperationException("配置含多行 TOML 字符串，请在已有 [features] 段手动设置 respect_system_proxy = true。");
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var sections = lines.Select((line, index) => (line, index))
            .Where(x => Regex.IsMatch(x.line, @"^\s*\[features\]\s*(#.*)?$"))
            .Select(x => x.index).ToArray();
        if (sections.Length > 1 || lines.Any(l => Regex.IsMatch(l, @"^\s*features\s*[.=]")) ||
            lines.Any(l => Regex.IsMatch(l, "^\\s*\\[\\s*[\"']features[\"']")))
            throw new InvalidOperationException("features 使用非标准或重复 TOML 写法，请手动编辑以保留配置。");
        if (sections.Length == 0)
            return text.TrimEnd('\r', '\n') + nl + nl + "[features]" + nl + "respect_system_proxy = true" + nl;
        var start = sections[0];
        var end = lines.FindIndex(start + 1, l => Regex.IsMatch(l, @"^\s*\["));
        if (end < 0) end = lines.Count;
        var matches = Enumerable.Range(start + 1, end - start - 1)
            .Where(i => Regex.IsMatch(lines[i], @"^\s*respect_system_proxy\s*=")).ToArray();
        if (matches.Length > 1 || Enumerable.Range(start + 1, end - start - 1)
            .Any(i => Regex.IsMatch(lines[i], "^\\s*[\"']respect_system_proxy[\"']\\s*=")))
            throw new InvalidOperationException("respect_system_proxy 写法不明确，请手动编辑。");
        if (matches.Length == 1)
        {
            var old = lines[matches[0]];
            if (!Regex.IsMatch(old, @"^\s*respect_system_proxy\s*=\s*(true|false)\s*(#.*)?$"))
                throw new InvalidOperationException("respect_system_proxy 不是布尔值，未修改。");
            lines[matches[0]] = Regex.Replace(old, @"(=\s*)(true|false)", "${1}true");
        }
        else lines.Insert(start + 1, "respect_system_proxy = true");
        return string.Join(nl, lines);
    }

    public static CodexConfigChange? Apply(string home)
    {
        Directory.CreateDirectory(home);
        var file = Path.Combine(home, "config.toml");
        var existed = File.Exists(file);
        var bytes = existed ? File.ReadAllBytes(file) : [];
        int? unixMode = OperatingSystem.IsWindows() ? null : (int)(existed
            ? File.GetUnixFileMode(file) : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var hasBom = bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 });
        var original = Encoding.UTF8.GetString(bytes.AsSpan(hasBom ? 3 : 0));
        var edited = EnableSystemProxy(original);
        var output = new UTF8Encoding(hasBom).GetPreamble().Concat(Encoding.UTF8.GetBytes(edited)).ToArray();
        if (bytes.SequenceEqual(output)) return null;
        var backup = Path.Combine(home, "v2rayn-proxy-backups", $"config-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        WritePrivateFile(backup, bytes, unixMode);
        if (File.Exists(file) && !File.ReadAllBytes(file).SequenceEqual(bytes))
            throw new IOException("Codex 配置在备份后发生变化，未覆盖。");
        var temp = file + ".v2rayn-" + Guid.NewGuid().ToString("N") + ".tmp";
        WritePrivateFile(temp, output, unixMode); File.Move(temp, file, true);
        return new(backup, Convert.ToHexString(SHA256.HashData(output)), existed, unixMode);
    }

    public static void Undo(string home, CodexConfigChange change)
    {
        var file = Path.Combine(home, "config.toml");
        if (!File.Exists(file) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) != change.AppliedSha256)
            throw new IOException("Codex 配置已有后续修改，停止自动恢复，避免覆盖你的改动。备份仍保留。");
        if (!change.OriginalExisted) { File.Delete(file); return; }
        var backup = File.ReadAllBytes(change.BackupPath);
        var temp = file + ".v2rayn-restore-" + Guid.NewGuid().ToString("N") + ".tmp";
        WritePrivateFile(temp, backup, change.OriginalUnixMode); File.Move(temp, file, true);
    }

    private static void WritePrivateFile(string path, byte[] bytes, int? unixMode)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = (UnixFileMode)(unixMode ?? (int)(UnixFileMode.UserRead | UnixFileMode.UserWrite));
        using var stream = new FileStream(path, options);
        stream.Write(bytes);
        // UnixCreateMode is filtered by umask. Restore the original mode after the private write.
        if (!OperatingSystem.IsWindows() && unixMode is int mode) File.SetUnixFileMode(path, (UnixFileMode)mode);
    }

    public static string? FindDesktopRuntime()
    {
        // Prefer a running desktop runtime to an unrelated npm CLI on PATH.
        foreach (var p in Process.GetProcessesByName("codex"))
            using (p)
                try
                {
                    var path = p.MainModule?.FileName;
                    var normalized = path?.Replace('\\', '/');
                    if (normalized is not null &&
                        (normalized.Contains("/OpenAI/Codex/bin/", StringComparison.OrdinalIgnoreCase) ||
                         normalized.Contains("/Codex.app/Contents/", StringComparison.OrdinalIgnoreCase) ||
                         normalized.Contains("/ChatGPT.app/Contents/", StringComparison.OrdinalIgnoreCase)))
                        return ResolveRuntimeSelection(path!);
                }
                catch (Exception) { }
        var bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        var roots = new List<string> { bin };
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            roots.Add(Path.Combine(home, "Library", "Application Support", "OpenAI", "Codex", "bin"));
            foreach (var apps in new[] { "/Applications", Path.Combine(home, "Applications") })
                foreach (var app in new[] { "Codex.app", "ChatGPT.app" })
                    try
                    {
                        if (ResolveRuntimeSelection(Path.Combine(apps, app)) is { } runtime) return runtime;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
        }
        foreach (var folder in roots.Distinct())
            try
            {
                if (!Directory.Exists(folder)) continue;
                var name = OperatingSystem.IsWindows() ? "codex.exe" : "codex";
                var candidate = Directory.EnumerateFiles(folder, name, new EnumerationOptions
                { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (candidate is not null) return candidate;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        return null;
    }

    public static string? ResolveRuntimeSelection(string selection)
    {
        if (File.Exists(selection))
            return Path.GetFileName(selection) is "codex" or "codex.exe" ? Path.GetFullPath(selection) : null;
        if (!selection.TrimEnd(Path.DirectorySeparatorChar).EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return null;
        var resources = Path.Combine(selection, "Contents", "Resources");
        if (!Directory.Exists(resources)) return null;
        var direct = Path.Combine(resources, "codex");
        if (File.Exists(direct)) return direct;
        // Do not choose one of multiple architectures/runtimes by arbitrary enumeration order.
        var candidates = Directory.EnumerateFiles(resources, "codex", new EnumerationOptions
        { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    public static async Task<string> DiagnoseAsync(string executable, string home, CancellationToken ct)
    {
        executable = ResolveRuntimeSelection(executable) ?? throw new FileNotFoundException("请选择 Codex 桌面版实际使用的 codex 可执行文件，或包含唯一运行时的 .app 应用。", executable);
        if (!Directory.Exists(home)) throw new DirectoryNotFoundException("Codex 配置目录不存在，请先确认目录或启用系统代理支持。");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = home
        };
        start.ArgumentList.Add("doctor"); start.ArgumentList.Add("--json");
        foreach (var key in start.Environment.Keys.Where(k => k.EndsWith("_PROXY", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["CODEX_HOME"] = home;
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Codex 诊断。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch
        {
            try { process.Kill(true); } catch { }
            throw;
        }
        var stdout = await output; await error;
        if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
            throw new IOException("Codex doctor 未返回报告，请确认该运行时支持 doctor --json。");
        var root = JsonNode.Parse(stdout) ?? throw new IOException("Codex 未返回有效 JSON 报告。");
        var text = new StringBuilder($"运行时：{root["codexVersion"]}\n");
        var dotenv = Path.Combine(home, ".env");
        if (File.Exists(dotenv))
        {
            var keys = File.ReadLines(dotenv).Select(line => Regex.Match(line, @"^\s*(?:export\s+)?([A-Za-z_]*[Pp][Rr][Oo][Xx][Yy][A-Za-z_]*)\s*="))
                .Where(match => match.Success).Select(match => match.Groups[1].Value).Distinct().ToArray();
            if (keys.Length > 0) text.AppendLine("注意：Codex .env 仍含代理设置（" + string.Join(", ", keys) + "），这次不能单独证明系统代理生效。");
        }
        foreach (var key in new[] { "network.env", "network.provider_reachability", "network.websocket_reachability" })
        {
            var check = root["checks"]?[key];
            if (check is null) { text.AppendLine($"{key}: 本版本未提供该项检查"); continue; }
            text.AppendLine($"{check["summary"]} ({check["durationMs"]} ms)");
            // Only allow known redacted diagnostics; never display auth/config contents or raw stderr.
            foreach (var detail in new[] { "respect system proxy", "proxy env vars", "proxy env vars present", "handshake result" })
                if (check["details"]?[detail] is { } value) text.AppendLine($"  {detail}: {value}");
        }
        text.AppendLine("此检测不发起模型任务。HTTP 101 仅验证 WebSocket 握手，不代表长会话或所有工具连接都已验证。");
        text.AppendLine("请关闭 TUN 后再运行，才能验证纯系统代理；本程序不会自动关闭 TUN 或重启 Codex。");
        return text.ToString();
    }
}
