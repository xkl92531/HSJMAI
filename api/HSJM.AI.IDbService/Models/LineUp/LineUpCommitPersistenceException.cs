namespace HSJM.AI.IDbService;

public sealed class LineUpCommitPersistenceException : Exception
{
    public LineUpCommitPersistenceException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
