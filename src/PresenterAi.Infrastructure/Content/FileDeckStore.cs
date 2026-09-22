using PresenterAi.Application.Content;

namespace PresenterAi.Infrastructure.Content;

public sealed class FileDeckStore(string rootDir) : IDeckStore
{
    public string DeckRoot { get; } = Path.Combine(rootDir, "decks");
}
