namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

public sealed record ChatCommandDescriptor(
    string Name,
    string Prefix,
    string Usage,
    string Description,
    bool IsMutating)
{
    public IReadOnlyList<string>? Aliases { get; init; }
}

public static class ChatCommandDescriptorCatalog
{
    public static ChatCommandDescriptor Commands { get; } = new(
        Name: "commands",
        Prefix: "!commands",
        Usage: "!commands",
        Description: "Lists available chat commands.",
        IsMutating: false)
    {
        Aliases = ["!help"]
    };

    public static ChatCommandDescriptor Register { get; } = new(
        Name: "register",
        Prefix: "!register",
        Usage: "!register CODE",
        Description: "Links your in-game identity to a portal profile using an activation code.",
        IsMutating: true);

    public static ChatCommandDescriptor WhoAmI { get; } = new(
        Name: "whoami",
        Prefix: "!whoami",
        Usage: "!whoami",
        Description: "Shows your current name, IP, location, and role tags in a private response.",
        IsMutating: false);

    public static ChatCommandDescriptor Fu { get; } = new(
        Name: "fu",
        Prefix: "!fu",
        Usage: "!fu <player name>",
        Description: "Sends a playful server-wide message to a resolved player target.",
        IsMutating: false);

    public static ChatCommandDescriptor Like { get; } = new(
        Name: "like",
        Prefix: "!like",
        Usage: "!like",
        Description: "Records a positive vote for the current map.",
        IsMutating: true);

    public static ChatCommandDescriptor Dislike { get; } = new(
        Name: "dislike",
        Prefix: "!dislike",
        Usage: "!dislike",
        Description: "Records a negative vote for the current map.",
        IsMutating: true);

    public static IReadOnlyList<ChatCommandDescriptor> All { get; } =
    [
        Commands,
        Register,
        WhoAmI,
        Like,
        Dislike,
        Fu
    ];
}
