using System.Collections.Generic;
using Microsoft.Win32;

namespace HsjmTia.Core.Discovery
{
    public class WindowsRegistryReader : IRegistryReader
    {
        public IReadOnlyList<string> GetSubKeyNames(string path)
        {
            using (var key = Open(path))
            {
                return key == null ? new List<string>() : new List<string>(key.GetSubKeyNames());
            }
        }

        public IReadOnlyList<string> GetValueNames(string path)
        {
            using (var key = Open(path))
            {
                return key == null ? new List<string>() : new List<string>(key.GetValueNames());
            }
        }

        public string GetValue(string path, string name)
        {
            using (var key = Open(path))
            {
                return key?.GetValue(name)?.ToString();
            }
        }

        private static RegistryKey Open(string path)
            => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(path);
    }
}
