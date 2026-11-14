using System.Text.Json;
using WindowsAxon.Agent.Audit;
using WindowsAxon.Agent.Guard;

namespace WindowsAxon.Agent.Tests.Audit;

public class AuditLogTests
{
    private static AuditEntry Entry(string args) => new(new DateTimeOffset(2026, 10, 10, 9, 30, 0, TimeSpan.Zero).ToLocalTime(),
                                                        "call",
                                                        "set wallpaper",
                                                        "wallpaper_set",
                                                        args,
                                                        "Change",
                                                        "approved",
                                                        true);

    [Fact]
    public void Entries_are_appended_as_json_lines_in_a_dated_file()
    {
        var folder = Directory.CreateTempSubdirectory("windowsaxon-audit-").FullName;
        var log = new JsonlAuditLog(folder);

        log.Write(Entry("""{"path":"C:\a.jpg"}"""));
        log.Write(Entry("""{"path":"C:\b.jpg"}"""));

        var file = Path.Combine(folder, $"audit-{Entry("").Time:yyyyMMdd}.jsonl");
        var lines = File.ReadAllLines(file);
        Assert.Equal(2, lines.Length);
        using var json = JsonDocument.Parse(lines[1]);
        Assert.Equal("wallpaper_set", json.RootElement.GetProperty("tool").GetString());
        Assert.Contains("b.jpg", json.RootElement.GetProperty("arguments").GetString());
        Assert.Equal("approved", json.RootElement.GetProperty("decision").GetString());
    }

    [Fact]
    public void Card_numbers_in_arguments_and_request_are_masked()
    {
        var folder = Directory.CreateTempSubdirectory("windowsaxon-audit-").FullName;
        var log = new JsonlAuditLog(folder);

        log.Write(Entry("""{"text":"4111 1111 1111 1111"}""") with
        {
            Request = "type 4111111111111111",
            Result = "copied 4111111111111111",
            Error = "System.Exception: 4111 1111 1111 1111",
        });

        var text = File.ReadAllText(Directory.GetFiles(folder).Single());
        Assert.DoesNotContain("4111", text);
        Assert.Contains(CardNumberMasker.Replacement, text);
    }

    [Fact]
    public void Full_results_and_multiline_errors_remain_in_one_json_line()
    {
        var folder = Directory.CreateTempSubdirectory("windowsaxon-audit-").FullName;
        var result = new string('x', 4000) + "\nlast line";
        var error = "System.InvalidOperationException: failed\n   at Tool.Run()";
        new JsonlAuditLog(folder).Write(Entry("{}") with
        {
            CallId = "call-1",
            Status = "failed",
            DurationMs = 12.5,
            Result = result,
            Error = error,
        });

        var line = Assert.Single(File.ReadAllLines(Directory.GetFiles(folder).Single()));
        using var json = JsonDocument.Parse(line);
        Assert.Equal(result, json.RootElement.GetProperty("result").GetString());
        Assert.Equal(error, json.RootElement.GetProperty("error").GetString());
        Assert.Equal("call-1", json.RootElement.GetProperty("callId").GetString());
        Assert.Equal(12.5, json.RootElement.GetProperty("durationMs").GetDouble());
    }

    [Fact]
    public void Disabled_log_writes_nothing()
    {
        var folder = Directory.CreateTempSubdirectory("windowsaxon-audit-").FullName;

        new JsonlAuditLog(folder, enabled: false).Write(Entry("{}"));

        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public void Ledger_records_decisions_by_call_id()
    {
        var ledger = new ApprovalLedger();

        ledger.Record(new FunctionCallContent("c1", "test"), approved: true);
        ledger.Record(new FunctionCallContent("c2", "test"), approved: false);

        Assert.True(ledger.Consume("c1", "test", new Dictionary<string, object?>()));
        Assert.False(ledger.Consume("c2", "test", new Dictionary<string, object?>()));
        Assert.False(ledger.Consume("c3", "test", new Dictionary<string, object?>()));
    }
}
