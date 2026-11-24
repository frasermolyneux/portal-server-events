using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.RulesEngine;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;
using XtremeIdiots.Portal.Server.Events.Processor.App.Services;

namespace XtremeIdiots.Portal.Server.Events.Processor.App.Rules;

internal static class ConnectionTriggerNormalizer
{
    public static RuleTriggerEnvelope From(PlayerConnectedEvent source) =>
        Create("player.connected", source, source.ConnectionId, source.PlayerGuid, source.SlotId,
            source.IpAddress, source.ObservationId, source.ConnectionGeneration, source.Runtime) with
        { Username = source.Username };

    public static RuleTriggerEnvelope From(PlayerIpResolvedEvent source) =>
        Create("player.ip-resolved", source, source.ConnectionId, source.PlayerGuid, source.SlotId,
            source.IpAddress, source.ObservationId, source.ConnectionGeneration, source.Runtime);

    private static RuleTriggerEnvelope Create(
        string triggerId,
        ServerEventBase source,
        Guid? connectionId,
        string playerGuid,
        int? slotId,
        string? ipAddress,
        Guid? observationId,
        ulong? connectionGeneration,
        Abstractions.V1.Events.RuleRuntimeDescriptor? runtime)
    {
        if (source.ServerId == Guid.Empty ||
            connectionId is null || connectionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(playerGuid) ||
            slotId is null or < 0 ||
            !Enum.TryParse<GameType>(source.GameType, true, out var gameType) ||
            !Enum.IsDefined(gameType) ||
            gameType == GameType.Unknown ||
            source.EventGeneratedUtc == default ||
            source.EventGeneratedUtc.Kind == DateTimeKind.Local)
        {
            throw new ArgumentException("Rules evaluation requires a server, game, GUID, slot, stable connection identity and UTC observation time.", nameof(source));
        }

        return new RuleTriggerEnvelope
        {
            TriggerId = triggerId,
            GameServerId = source.ServerId,
            GameType = gameType,
            ConnectionId = connectionId.Value,
            PlayerGuid = playerGuid,
            SlotId = slotId.Value,
            PlayerIpAddress = IpAddressGuard.IsPersistable(ipAddress) ? IpAddressGuard.Normalize(ipAddress) : null,
            OccurredAtUtc = new DateTimeOffset(DateTime.SpecifyKind(source.EventGeneratedUtc, DateTimeKind.Utc)),
            ObservationId = observationId.GetValueOrDefault(),
            TriggerOccurrenceId = observationId.GetValueOrDefault(),
            ConnectionGeneration = connectionGeneration,
            Runtime = RuleRuntimeMapping.From(runtime)
        };
    }
}

internal static class RuleRuntimeMapping
{
    public static Repository.Abstractions.Models.V1.RulesEngine.RuleRuntimeDescriptor? From(
        Abstractions.V1.Events.RuleRuntimeDescriptor? runtime) =>
        runtime is null ? null : new()
        {
            Id = runtime.Id,
            Version = runtime.Version,
            ProtocolVersion = runtime.ProtocolVersion,
            IdentityModel = runtime.Id switch
            {
                "server-agent" => RuleRuntimeIdentityModel.AgentObservedConnection,
                "cod4x-plugin" => RuleRuntimeIdentityModel.PluginRuntimeGeneration,
                _ => RuleRuntimeIdentityModel.Unknown
            },
            InstanceId = runtime.InstanceId.GetValueOrDefault(),
            StartedAtUtc = runtime.StartedAtUtc.GetValueOrDefault(),
            Capabilities = runtime.Capabilities
        };
}
