namespace XtremeIdiots.Portal.Server.Events.Processor.App.Functions;

/// <summary>
/// <see cref="Abstractions.V1.Events.BanAppliedEvent.Source"/> values that <c>BanAppliedProcessor</c>
/// treats as server-side ban imports (create the player if missing, ensure the <c>RconBanImport</c>
/// admin action, and publish its forum topic).
/// </summary>
internal static class BanImportSources
{
    /// <summary>Ban discovered by the agent's periodic RCON <c>dumpbanlist</c> reconcile.</summary>
    public const string RconDumpBanList = "RconDumpbanlist";

    /// <summary>
    /// Ban applied by the CoD4x plugin's VPN Protection at connect time. The plugin enforces the ban
    /// locally and the evaluate endpoint hands the import to the queue so persistence + forum-topic
    /// creation happen off the plugin's request hot path.
    /// </summary>
    public const string Cod4xVpnProtection = "CoD4xVpnProtection";
}
