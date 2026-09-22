namespace PresenterAi.Contracts;

public sealed record ListResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);
