using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace Dds120.Util
{
    // key=value settings stored next to the exe (portable). Falls back to %APPDATA% when the
    // program folder is read-only, e.g. on a write-protected stick or under Program Files.
    public static class SettingsFile
    {
        const string FileName = "DDS120Scope.ini";

        public static string PortablePath
        {
            get
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
                return Path.Combine(dir, FileName);
            }
        }

        static string FallbackPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DDS120Scope", FileName);
            }
        }

        public static string ActivePath
        {
            get { return File.Exists(PortablePath) || !File.Exists(FallbackPath) ? PortablePath : FallbackPath; }
        }

        public static Dictionary<string, string> Load()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string path = ActivePath;
            if (!File.Exists(path))
                return d;
            try
            {
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                        continue;
                    int eq = line.IndexOf('=');
                    if (eq > 0)
                        d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            return d;
        }

        public static string Save(IDictionary<string, string> d)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# DDS120 Scope settings (portable). Delete this file to reset to defaults.");
            var keys = new List<string>(d.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (string k in keys)
                sb.Append(k).Append('=').AppendLine(d[k]);
            foreach (string path in new[] { PortablePath, FallbackPath })
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                    return path;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            return null;
        }
    }
}
