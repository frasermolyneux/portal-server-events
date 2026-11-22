namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Infrastructure;

public class ApimPolicyTests
{
    [Fact]
    public void IngestPolicy_EnclosesIfBodiesInBlocks()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Configuration", "api_management_ingest.tf");
        var lines = File.ReadAllLines(path);
        var conditions = lines
            .Select((line, index) => (Line: line.Trim(), Index: index))
            .Where(item => item.Line.StartsWith("if (", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(conditions);
        foreach (var condition in conditions)
        {
            Assert.EndsWith(")", condition.Line);
            Assert.Equal("{", lines[condition.Index + 1].Trim());
        }
    }
}
