namespace XtremeIdiots.Portal.Server.Events.Processor.App.Tests.Fixtures;

internal static class SettingsFixtureLoader
{
    public static string LoadSettings(string fileName)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Settings", fileName);
        if (!File.Exists(fixturePath))
        {
            throw new FileNotFoundException($"Settings fixture '{fileName}' was not found at '{fixturePath}'.");
        }

        return File.ReadAllText(fixturePath);
    }
}
