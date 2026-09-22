namespace PresenterAi.Domain.Errors;

public sealed class ScriptParseException : DomainException
{
    public ScriptParseException(string detail)
        : base("presentation.invalid_script", 400, detail)
    {
    }
}
