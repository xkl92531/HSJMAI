using System.Collections.Generic;

namespace HsjmTia.Core.Discovery
{
    public interface IRegistryReader
    {
        IReadOnlyList<string> GetSubKeyNames(string path);
        IReadOnlyList<string> GetValueNames(string path);
        string GetValue(string path, string name);
    }
}
