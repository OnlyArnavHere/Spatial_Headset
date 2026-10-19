using System.IO;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>Writes a file so a crash never leaves it half written: temp file next to it, flushed, then replace/move.</summary>
    public static class AtomicFile
    {
        public static void Write(string path, byte[] data)
        {
            string dir = Path.GetDirectoryName(path);
            Directory.CreateDirectory(dir);
            string tmp = path + "." + System.Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
                {
                    f.Write(data, 0, data.Length);
                    f.Flush(true);
                }
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
                throw;
            }
        }
    }
}
