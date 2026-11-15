using System.Net.Http;
using System.Text.Json;
using CNCStudio.Core.Remote;

namespace CNCStudio.Core.Tests;

public class RemoteServerTests
{
    private sealed class FakeMachine : IRemoteMachine
    {
        public List<string> Commands { get; } = new();
        public Task<RemoteStatus> StatusAsync() => Task.FromResult(new RemoteStatus(true, "Idle", new[] { 1.0, 2, 3 }, null, true, null, null, null,
            new double[] { 100, 100, 100 }, false, false, new[] { "Fräsbyte" }, "normal", false, ""));
        public Task<string?> CommandAsync(string action, IReadOnlyDictionary<string, string> args)
        {
            Commands.Add(action + (args.TryGetValue("dir", out var d) ? " " + d : ""));
            return Task.FromResult<string?>(action == "home" ? "Maskinen är inte ansluten." : null);
        }
    }

    [Fact]
    public async Task Pin_is_required_and_wrong_pins_lock_for_a_while()
    {
        var m = new FakeMachine();
        var s = new RemoteServer(m, "123456");
        Assert.Equal(200, (await s.Handle(RemoteServer.Parse("GET / HTTP/1.1", "")!)).Status);       // the page itself is open
        Assert.Equal(401, (await s.Handle(RemoteServer.Parse("GET /api/status?pin=000000 HTTP/1.1", "")!)).Status);
        var ok = await s.Handle(RemoteServer.Parse("GET /api/status?pin=123456 HTTP/1.1", "")!);
        Assert.Equal(200, ok.Status);
        Assert.Equal("Idle", JsonDocument.Parse(ok.Body).RootElement.GetProperty("state").GetString());
        for (int i = 0; i < 5; i++) await s.Handle(RemoteServer.Parse("GET /api/status?pin=999999 HTTP/1.1", "")!);
        Assert.Equal(429, (await s.Handle(RemoteServer.Parse("GET /api/status?pin=123456 HTTP/1.1", "")!)).Status);   // locked even with the right PIN
    }

    [Fact]
    public async Task Only_known_commands_reach_the_machine()
    {
        var m = new FakeMachine();
        var s = new RemoteServer(m, "123456");
        var jog = await s.Handle(RemoteServer.Parse("POST /api/cmd HTTP/1.1", "pin=123456&action=jog&dir=X%2B")!);
        Assert.True(JsonDocument.Parse(jog.Body).RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("jog X+", Assert.Single(m.Commands));
        var bad = await s.Handle(RemoteServer.Parse("POST /api/cmd HTTP/1.1", "pin=123456&action=gcode&line=%24RST%3D*")!);
        Assert.Equal(400, bad.Status);
        Assert.Single(m.Commands);
        var refused = await s.Handle(RemoteServer.Parse("POST /api/cmd HTTP/1.1", "pin=123456&action=home")!);
        Assert.Equal("Maskinen är inte ansluten.", JsonDocument.Parse(refused.Body).RootElement.GetProperty("error").GetString());
    }

    [Fact(Timeout = 20000)]
    public async Task Serves_over_the_network()
    {
        var m = new FakeMachine();
        using var s = new RemoteServer(m, "654321");
        s.Start(0);
        using var http = new HttpClient();
        var page = await http.GetStringAsync($"http://127.0.0.1:{s.Port}/");
        Assert.Contains("STOPP", page);
        var r = await http.PostAsync($"http://127.0.0.1:{s.Port}/api/cmd", new FormUrlEncodedContent(new Dictionary<string, string> { ["pin"] = "654321", ["action"] = "stop" }));
        Assert.True(r.IsSuccessStatusCode);
        Assert.Contains("stop", m.Commands);
        Assert.Equal(6, RemoteServer.NewPin().Length);
    }
}
