namespace XtremeIdiots.Portal.Server.Events.Processor.App.Commands;

/// <summary>
/// Shared descriptor for a chat command's identity and help metadata.
/// </summary>
/// <param name="Name">Stable command name key (without prefix marker).</param>
/// <param name="Prefix">Command prefix token (for example <c>!register</c>).</param>
/// <param name="Usage">Usage text presented to players.</param>
/// <param name="Description">Human-readable description of command behavior.</param>
/// <param name="IsMutating">Indicates whether the command mutates state.</param>
public sealed record ChatCommandDescriptor(
    string Name,
    string Prefix,
    string Usage,
    string Description,
    bool IsMutating);

/// <summary>
/// Shared catalog of built-in chat command descriptors.
/// </summary>
public static class ChatCommandDescriptorCatalog
{
    /// <summary>
    /// <c>!commands</c> command descriptor.
    /// </summary>
    public static ChatCommandDescriptor Commands { get; } = new(
        Name: "commands",
        Prefix: "!commands",
        Usage: "!commands",
        Description: "Lists available chat commands.",
        IsMutating: false);

    /// <summary>
    /// <c>!register</c> command descriptor.
    /// </summary>
    public static ChatCommandDescriptor Register { get; } = new(
        Name: "register",
        Prefix: "!register",
        Usage: "!register CODE",
        Description: "Links your in-game identity to a portal profile using an activation code.",
        IsMutating: true);

    /// <summary>
    /// <c>!whoami</c> command descriptor.
    /// </summary>
    public static ChatCommandDescriptor WhoAmI { get; } = new(
        Name: "whoami",
        Prefix: "!whoami",
        Usage: "!whoami",
        Description: "Shows your current name, IP, location, and role tags in a private response.",
        IsMutating: false);

    /// <summary>
    /// <c>!fu</c> command descriptor.
    /// </summary>
    public static ChatCommandDescriptor Fu { get; } = new(
        Name: "fu",
        Prefix: "!fu",
        Usage: "!fu <player name>",
        Description: "Sends a playful server-wide message to a resolved player target.",
        IsMutating: false);

    /// <summary>
    /// <c>!like</c> command descriptor.
    /// </summary>
    public static ChatCommandDescriptor Like { get; } = new(
        Name: "like",
        Prefix: "!like",
        Usage: "!like",
        Description: "Records a positive vote for the current map.",
        IsMutating: true);

    /// <summary>
    /// <c>!dislike</c> command descriptor.
    /// </summary>
    public static ChatCommandDescriptor Dislike { get; } = new(
        Name: "dislike",
        Prefix: "!dislike",
        Usage: "!dislike",
        Description: "Records a negative vote for the current map.",
        IsMutating: true);

    /// <summary>
    /// All built-in descriptors in deterministic display order.
    /// </summary>
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
