using AdaptiveRouting;
using System.Text.Json;

if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) return 2;
try
{
    var plan = JsonSerializer.Deserialize<CustomInstallPlan>(await File.ReadAllTextAsync(args[0]), CustomUpdateManifest.Json);
    if (plan is null) return 2;
    return await CustomUpdateInstaller.RunAsync(plan);
}
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }
