namespace PresenterAi.Domain.Errors;

public class DomainException : Exception
{
    public DomainException(
        string code,
        int status,
        string detail,
        IReadOnlyDictionary<string, object?>? extensions = null)
        : base(detail)
    {
        Code = code;
        Status = status;
        Detail = detail;
        Extensions = extensions;
    }

    public string Code { get; }

    public int Status { get; }

    public string Detail { get; }

    public IReadOnlyDictionary<string, object?>? Extensions { get; }
}
