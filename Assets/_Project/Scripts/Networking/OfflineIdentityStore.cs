using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DigBlocks.Networking
{
    public static class OfflineIdentityStore
    {
        public static ulong LoadOrCreate(string path)
        {
            path = Path.GetFullPath(path);
            if (File.Exists(path)) return Read(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            byte[] bytes = new byte[8];
            ulong id;
            using (var random = RandomNumberGenerator.Create())
                do { random.GetBytes(bytes); id = BitConverter.ToUInt64(bytes, 0); } while (id == 0);

            //publish a complete file atomically; a competing process may have created the identity first.
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, id.ToString("X16", CultureInfo.InvariantCulture), new UTF8Encoding(false));
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { }
                return Read(path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static ulong Read(string path)
        {
            string value = File.ReadAllText(path).Trim();
            if (value.Length != 16 || !ulong.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong id) || id == 0)
                throw new InvalidDataException($"Invalid offline identity at '{path}'. Restore its backup or explicitly choose a new identity file.");
            return id;
        }
    }
}
