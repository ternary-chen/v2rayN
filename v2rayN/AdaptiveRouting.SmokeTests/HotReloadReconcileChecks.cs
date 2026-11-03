using AdaptiveRouting;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

internal static class HotReloadReconcileChecks
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }

    public static async Task RunAsync(string executable)
    {
        Assert(File.Exists(executable), "Pass the installed Xray path; no download required");
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", "hot-reconcile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var apiPort = FreePort(); var socksPort = FreePort();
        while (socksPort == apiPort) socksPort = FreePort();
        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            ["inbounds"] = new JsonArray(new JsonObject
            {
                ["tag"] = "test", ["listen"] = "127.0.0.1", ["port"] = socksPort,
                ["protocol"] = "socks", ["settings"] = new JsonObject { ["auth"] = "noauth" }
            }),
            ["outbounds"] = new JsonArray(
                new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" },
                new JsonObject { ["tag"] = "blocked", ["protocol"] = "blackhole" }),
            ["routing"] = new JsonObject { ["rules"] = new JsonArray(new JsonObject
            {
                ["type"] = "field", ["inboundTag"] = new JsonArray("test"),
                ["outboundTag"] = "direct", ["ruleTag"] = "manual-rule-1"
            }) }
        };
        var original = XrayRoutingHotReload.Prepare(config.ToJsonString(), apiPort);
        var driftNode = JsonNode.Parse(original)!;
        driftNode["routing"]!["rules"]![0]!["outboundTag"] = "blocked";
        var drift = XrayRoutingHotReload.Prepare(driftNode.ToJsonString(), apiPort);
        var desiredNode = JsonNode.Parse(original)!;
        desiredNode["routing"]!["rules"]![0]!["domain"] = new JsonArray("full:example.invalid");
        var desired = XrayRoutingHotReload.Prepare(desiredNode.ToJsonString(), apiPort);
        var file = Path.Combine(root, "core.json");
        await File.WriteAllTextAsync(file, original);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "run", "-c", file }) start.ArgumentList.Add(arg);
        using var core = Process.Start(start)!;
        var stdout = core.StandardOutput.ReadToEndAsync(); var stderr = core.StandardError.ReadToEndAsync();
        var pid = core.Id;
        var echo = new TcpListener(IPAddress.Loopback, 0); echo.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        Task Apply(string before, string after, Func<Task>? persist = null) =>
            XrayRoutingHotReload.ApplyAsync(executable, Path.GetDirectoryName(executable)!, root, before, after,
                () => !core.HasExited && core.Id == pid, persist ?? (() => Task.CompletedTask));
        async Task Matches(string expected)
        {
            var api = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "api", "lsrules", "--server=127.0.0.1:" + apiPort, "--timeout=3" }) api.ArgumentList.Add(arg);
            using var command = Process.Start(api)!;
            var output = command.StandardOutput.ReadToEndAsync(); var error = command.StandardError.ReadToEndAsync();
            await command.WaitForExitAsync(deadline.Token);
            Assert(command.ExitCode == 0, await error);
            var live = JsonNode.Parse(await output)!["rules"]!.AsArray();
            var rules = JsonNode.Parse(expected)!["routing"]!["rules"]!.AsArray();
            Assert(live.Select(x => (x!["tag"]?.ToString(), x["ruleTag"]?.ToString()))
                .SequenceEqual(rules.Select(x => (x!["outboundTag"]?.ToString(), x["ruleTag"]?.ToString()))), "Live revision/order differs");
        }
        try
        {
            for (var i = 0; ; i++)
            {
                try { using var ready = new TcpClient(); await ready.ConnectAsync(IPAddress.Loopback, apiPort, deadline.Token); break; }
                catch (SocketException) when (i < 40 && !core.HasExited) { await Task.Delay(100, deadline.Token); }
            }
            var accept = echo.AcceptTcpClientAsync(deadline.Token).AsTask();
            using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, socksPort, deadline.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, deadline.Token);
            await stream.ReadExactlyAsync(new byte[2], deadline.Token);
            var targetPort = ((IPEndPoint)echo.LocalEndpoint).Port;
            await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, (byte)(targetPort >> 8), (byte)(targetPort & 255) }, deadline.Token);
            var reply = new byte[10]; await stream.ReadExactlyAsync(reply, deadline.Token); Assert(reply[1] == 0, "SOCKS failed");
            using var peer = await accept;
            async Task ExistingConnectionSurvives()
            {
                await stream.WriteAsync(new byte[] { 42 }, deadline.Token);
                var data = new byte[1]; await peer.GetStream().ReadExactlyAsync(data, deadline.Token);
                await peer.GetStream().WriteAsync(data, deadline.Token); await stream.ReadExactlyAsync(data, deadline.Token);
                Assert(data[0] == 42 && !core.HasExited && core.Id == pid, "Existing stream or core lost");
            }
            await ExistingConnectionSurvives();
            await Apply(original, drift); // Change the live core without updating the caller's snapshot.
            var persisted = false;
            await Apply(original, original, () => { persisted = true; return Task.CompletedTask; });
            await Matches(original); await ExistingConnectionSurvives(); Assert(persisted, "Settings not persisted");
            Console.WriteLine("PASS Unchanged current settings overwrite drift; same PID and TCP stream survive");

            await Apply(original, drift);
            await Apply(original, desired);
            await Matches(desired); await ExistingConnectionSurvives();
            Console.WriteLine("PASS Edited current settings overwrite stale runtime including match-content revision");

            await Apply(desired, drift);
            try { await Apply(desired, original, () => throw new IOException("simulated disk failure")); throw new Exception("Expected persistence failure"); }
            catch (InvalidOperationException e) { Assert(e.Message.Contains("上次保存"), "Rollback message misleading"); }
            await Matches(desired); await ExistingConnectionSurvives();
            Console.WriteLine("PASS Persistence failure restores last saved rules and preserves established streams");

            var changedNode = JsonNode.Parse(desired)!; changedNode["inbounds"]![0]!["port"] = FreePort();
            try { await Apply(desired, changedNode.ToJsonString()); throw new Exception("Non-rule change accepted"); }
            catch (InvalidOperationException) { }
            await Matches(desired);
            var stoppedPersisted = false;
            try
            {
                await XrayRoutingHotReload.ApplyAsync(executable, Path.GetDirectoryName(executable)!, root, original, desired,
                    () => false, () => { stoppedPersisted = true; return Task.CompletedTask; });
                throw new Exception("Stopped core accepted");
            }
            catch (InvalidOperationException) { }
            Assert(!stoppedPersisted, "Saved without owning live core");
            Console.WriteLine("PASS Non-rule edits and stale process identity remain rejected");
        }
        finally
        {
            echo.Stop();
            if (!core.HasExited) core.Kill(); // Only the isolated child started by this test.
            await core.WaitForExitAsync();
            var logs = (await stdout) + (await stderr);
            if (logs.Contains("Failed to start")) Console.Error.WriteLine(logs);
        }
    }
}
