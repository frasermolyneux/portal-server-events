using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

internal static class ConnectedPlayerClaimRequestId
{
    public static Guid FromEvent(CommandContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context.SourceMessageId);

        // The plugin can restamp published time when retrying, and agent sequences reset on restart.
        var identity = string.Create(CultureInfo.InvariantCulture,
            $"connected-player-claim:v1:{context.ServerId:D}:{context.SequenceId}:{context.EventGeneratedUtc.Ticks}:{context.SourceMessageId}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
