using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Services;

internal static class GameTypeExtensions
{
    public static int ForumIdForObservations(this GameType gameType) => gameType switch
    {
        GameType.CallOfDuty2 => 58,
        GameType.CallOfDuty4 => 59,
        GameType.CallOfDuty5 => 60,
        GameType.Insurgency => 264,
        GameType.Minecraft => 265,
        GameType.Rust => 256,
        GameType.Arma or GameType.Arma2 or GameType.Arma3 => 252,
        _ => 28
    };

    public static int ForumIdForKicks(this GameType gameType) => gameType switch
    {
        GameType.CallOfDuty2 => 58,
        GameType.CallOfDuty4 => 59,
        GameType.CallOfDuty5 => 60,
        GameType.Insurgency => 264,
        GameType.Minecraft => 265,
        GameType.Rust => 256,
        GameType.Arma or GameType.Arma2 or GameType.Arma3 => 252,
        _ => 28
    };

    public static int ForumIdForWarnings(this GameType gameType) => gameType switch
    {
        GameType.CallOfDuty2 => 58,
        GameType.CallOfDuty4 => 59,
        GameType.CallOfDuty5 => 60,
        GameType.Insurgency => 264,
        GameType.Minecraft => 265,
        GameType.Rust => 256,
        GameType.Arma or GameType.Arma2 or GameType.Arma3 => 252,
        _ => 28
    };

    public static int ForumIdForTempBans(this GameType gameType) => gameType switch
    {
        GameType.CallOfDuty2 => 68,
        GameType.CallOfDuty4 => 69,
        GameType.CallOfDuty5 => 70,
        GameType.Insurgency => 169,
        GameType.Minecraft => 144,
        GameType.Rust => 260,
        GameType.Arma or GameType.Arma2 or GameType.Arma3 => 259,
        _ => 28
    };

    public static int ForumIdForBans(this GameType gameType) => gameType switch
    {
        GameType.CallOfDuty2 => 68,
        GameType.CallOfDuty4 => 69,
        GameType.CallOfDuty5 => 70,
        GameType.Insurgency => 169,
        GameType.Minecraft => 144,
        GameType.Rust => 260,
        GameType.Arma or GameType.Arma2 or GameType.Arma3 => 259,
        _ => 28
    };
}
