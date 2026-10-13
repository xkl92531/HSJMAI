namespace HSJM.AI.IDbService;

public sealed class LineUpGraphCatalogPersistenceException : Exception
{
    public LineUpGraphCatalogPersistenceException(string code)
        : base(code)
    {
        Code = code;
    }

    public string Code { get; }
}
