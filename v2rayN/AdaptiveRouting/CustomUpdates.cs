using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AdaptiveRouting;

public sealed class CustomUpdateOptions { public bool Enabled { get; set; } = true; }
public sealed record CustomBuild(int Schema, int Build, string Version)
{
    public static CustomBuild Current { get; } = JsonSerializer.Deserialize<CustomBuild>(
        typeof(CustomBuild).Assembly.GetManifestResourceStream("AdaptiveRouting.custom-build.json")!)!;
    public static string? Runtime => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "win-x64"
        : OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : null;
}
public sealed record CustomUpdateAsset(string Runtime, string Url, string Sha256, long Size);
public sealed record CustomUpdateManifest(int Schema, int Build, string Version, string Commit, string Notes, CustomUpdateAsset[] Packages)
{
    public const string Repository = "ternary-chen/v2rayN";
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static bool AllowedAsset(string url, string version)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        var prefix = "/" + Repository + "/releases/download/" + version + "/";
        return uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal) && uri.AbsolutePath[prefix.Length..] is { Length: > 0 } name
            && !name.Contains('/') && !name.Contains('%') && !name.Contains('\\');
    }
    public CustomUpdateAsset Validate(string runtime, string expectedVersion)
    {
        if (Schema != 1 || Build <= 0 || Version != expectedVersion || !Regex.IsMatch(Version, @"^v\d+\.\d+\.\d+-learning\.\d+$")
            || !Regex.IsMatch(Commit ?? "", "^[0-9a-f]{40}$") || (Notes?.Length ?? 0) > 16000 || Packages is null)
            throw new InvalidDataException("定制版更新清单格式或来源不匹配。");
        if (Packages.Select(x => x.Runtime).Distinct().Count() != Packages.Length) throw new InvalidDataException("更新清单包含重复架构。");
        var asset = Packages.SingleOrDefault(x => x.Runtime == runtime) ?? throw new InvalidDataException("此发布没有当前系统架构的安装包。");
        if (!AllowedAsset(asset.Url, Version) || !asset.Url.EndsWith(".zip", StringComparison.Ordinal) || !Regex.IsMatch(asset.Sha256 ?? "", "^[0-9a-fA-F]{64}$")
            || asset.Size is < 1 or > 1024L * 1024 * 1024) throw new InvalidDataException("安装包地址、大小或校验值无效。");
        return asset;
    }
}

public sealed class CustomUpdateClient(HttpClient http)
{
    public async Task<CustomUpdateManifest?> FindAsync(string runtime, int currentBuild, CancellationToken ct)
    {
        using var response = await http.GetAsync("https://api.github.com/repos/" + CustomUpdateManifest.Repository + "/releases?per_page=30", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        using var releases = JsonDocument.Parse(await ReadLimitedAsync(response.Content, 2 * 1024 * 1024, ct));
        var candidates = new List<CustomUpdateManifest>();
        foreach (var release in releases.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            var tag = release.GetProperty("tag_name").GetString() ?? "";
            if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+-learning\.\d+$")) continue;
            // Only the fork's explicitly published custom manifests are an update channel.
            var manifests = release.GetProperty("assets").EnumerateArray().Where(x => x.GetProperty("name").GetString() == "custom-update.json").ToArray();
            if (manifests.Length != 1) continue;
            var url = manifests[0].GetProperty("browser_download_url").GetString() ?? "";
            if (!CustomUpdateManifest.AllowedAsset(url, tag)) throw new InvalidDataException("更新清单不在指定仓库。");
            using var item = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct); item.EnsureSuccessStatusCode();
            var manifest = JsonSerializer.Deserialize<CustomUpdateManifest>(await ReadLimitedAsync(item.Content, 64 * 1024, ct), CustomUpdateManifest.Json)
                ?? throw new InvalidDataException("更新清单为空。");
            manifest.Validate(runtime, tag);
            if (manifest.Build > currentBuild) candidates.Add(manifest);
        }
        return candidates.OrderByDescending(x => x.Build).FirstOrDefault();
    }

    public async Task DownloadAsync(CustomUpdateManifest manifest, string runtime, string destination, Action<int>? progress, CancellationToken ct)
    {
        var asset = manifest.Validate(runtime, manifest.Version);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination) && await VerifyAsync(destination, asset, ct)) { progress?.Invoke(100); return; }
        var part = destination + ".partial";
        try
        {
            using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != asset.Size) throw new InvalidDataException("安装包响应大小与清单不一致。");
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; long total = 0; var last = -1;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > asset.Size) throw new InvalidDataException("安装包超出清单大小。");
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    var percent = (int)(total * 100 / asset.Size);
                    if (percent != last) { progress?.Invoke(percent); last = percent; }
                }
                if (total != asset.Size) throw new IOException("安装包下载未完成，将在下次检查时重试。");
            }
            if (!await VerifyAsync(part, asset, ct)) throw new InvalidDataException("安装包 SHA-256 校验失败，未准备安装。");
            File.Move(part, destination, true);
        }
        finally { if (File.Exists(part)) File.Delete(part); }
    }
    public static async Task<bool> VerifyAsync(string file, CustomUpdateAsset asset, CancellationToken ct = default)
    {
        if (!File.Exists(file) || new FileInfo(file).Length != asset.Size) return false;
        await using var stream = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }
    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        await using var source = await content.ReadAsStreamAsync(ct);
        using var target = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        { if (target.Length + read > limit) throw new InvalidDataException("更新元数据过大。"); await target.WriteAsync(buffer.AsMemory(0, read), ct); }
        return target.ToArray();
    }
}

public static class CustomUpdateArchive
{
    public static void Validate(string archive, string runtime)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 20000 || zip.Entries.Sum(x => x.Length) > 3L * 1024 * 1024 * 1024) throw new InvalidDataException("安装包解压大小异常。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = new List<string>();
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.TrimEnd('/');
            if (name.Length == 0 || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.Any(char.IsControl)
                || name.Split('/').Any(x => x is ".." or "." or "") || !names.Add(name)) throw new InvalidDataException("安装包包含不安全或重复路径。");
            if (name.Split('/').Any(x => new[] { "guiConfigs", "guiBackups", "guiLogs", "binConfigs", "guiTemps", "auth.json", ".env" }.Contains(x, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException("安装包包含用户数据目录。");
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
            {
                if (runtime != "osx-arm64" || !name.StartsWith("v2rayN Learning.app/", StringComparison.Ordinal) || entry.Length > 1024) throw new InvalidDataException("安装包链接无效。");
                using var reader = new StreamReader(entry.Open()); var link = reader.ReadToEnd();
                if (Path.IsPathRooted(link) || link.Contains('\\') || link.Contains(':') || link.Any(char.IsControl)) throw new InvalidDataException("安装包链接不安全。");
                var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "update-link-validation"));
                var target = Path.GetFullPath(Path.Combine(root, Path.GetDirectoryName(name)!, link));
                var bundle = Path.Combine(root, "v2rayN Learning.app") + Path.DirectorySeparatorChar;
                if (!target.StartsWith(bundle, StringComparison.Ordinal)) throw new InvalidDataException("安装包链接越过应用目录。");
                links.Add(name);
            }
        }
        if (links.Any(link => names.Any(name => name.StartsWith(link + "/", StringComparison.OrdinalIgnoreCase)))) throw new InvalidDataException("安装包向链接目录写入文件。");
        var entryName = runtime == "win-x64" ? "v2rayN.exe" : "v2rayN Learning.app/Contents/MacOS/v2rayN";
        var helperName = runtime == "win-x64" ? "CustomUpdater.exe" : "v2rayN Learning.app/Contents/MacOS/CustomUpdater";
        if (!names.Contains(helperName) || !names.Contains(entryName) || !names.Contains("build-info.json")) throw new InvalidDataException("安装包缺少客户端或构建清单。");
        // The version embedded beside the executable binds the payload to the advertised release.
        var buildName = runtime == "win-x64" ? "custom-build.json" : "v2rayN Learning.app/Contents/Resources/custom-build.json";
        if (!names.Contains(buildName)) throw new InvalidDataException("安装包不支持定制更新协议。");
    }
    public static bool WindowsProgramFile(string relative) => !relative.Contains('/') &&
        (relative.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        || relative is "custom-build.json" or "build-info.json" or "LICENSE" or "版本说明.txt" or "功能说明.md");
}
