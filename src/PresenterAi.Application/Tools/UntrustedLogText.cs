using System.Text;

namespace PresenterAi.Application.Tools;

public static class UntrustedLogText
{
    public static string Sanitize(string? value, int maxLength = 256)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var character in value)
        {
            var replacement = char.IsControl(character) ? $"\\u{(int)character:x4}" : character.ToString();
            if (builder.Length + replacement.Length > maxLength)
            {
                if (maxLength >= 3)
                {
                    if (builder.Length > maxLength - 3) builder.Length = maxLength - 3;
                    builder.Append("...");
                }
                break;
            }
            builder.Append(replacement);
        }
        return builder.ToString();
    }
}
