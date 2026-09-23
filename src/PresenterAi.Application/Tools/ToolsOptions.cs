namespace PresenterAi.Application.Tools;

public sealed class ToolsOptions
{
    public const int DefaultMaxInlineTools = 16;
    public const int MinMaxInlineTools = 0;
    public const int MaxMaxInlineTools = 128;

    public int MaxInlineTools { get; set; } = DefaultMaxInlineTools;
}
