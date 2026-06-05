using System;
using System.IO;
using System.Reflection;

namespace Emutastic.Services
{
    /// <summary>
    /// Builds the User-Agent header value Emutastic uses when calling
    /// RetroAchievements servers (port of upstream EmutasticUserAgent;
    /// ResolveOs rewritten for Linux).
    ///
    /// RA's hardcore-compliance policy keys two server-side decisions on this
    /// header: whether the unlock request is hardcore-eligible at all (must be
    /// a recognised emulator), and whether to downgrade hardcore unlocks to
    /// softcore (happens when the UA is missing, malformed, or has no
    /// parseable version). Format per their docs:
    ///
    ///   EmulatorName/v1.0.0 (OSName 10.0) core_name/v0.5.0
    ///
    /// A properly-formatted UA is necessary but not sufficient for hardcore —
    /// Emutastic also has to be on RA's approved hardcore-emulator list
    /// (separate one-time application; see docs/achievements-port-plan.md).
    /// </summary>
    public static class EmutasticUserAgent
    {
        private const string ProductName = "Emutastic";

        /// <summary>Canonical UA, e.g. <c>"Emutastic/1.6.0 (Debian 13)"</c>.</summary>
        public static string Build()
        {
            return $"{ProductName}/{ResolveVersion()} ({ResolveOs()})";
        }

        /// <summary>
        /// Canonical UA with the active libretro core's name and version
        /// appended. Falls back to <see cref="Build()"/> if either is blank.
        /// </summary>
        public static string Build(string? coreName, string? coreVersion)
        {
            if (string.IsNullOrWhiteSpace(coreName) || string.IsNullOrWhiteSpace(coreVersion))
                return Build();

            // Sanitize: HTTP UA tokens can't contain spaces, slashes, or
            // control characters ("ParaLLEl N64" → "ParaLLEl-N64").
            string n = Sanitize(coreName);
            string v = Sanitize(coreVersion);
            return $"{ProductName}/{ResolveVersion()} ({ResolveOs()}) {n}/{v}";
        }

        private static string Sanitize(string s)
        {
            var chars = new char[s.Length];
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                chars[i] = c switch
                {
                    ' ' or '\t' or '/' or '\\' or '(' or ')' or '<' or '>' or '@' or
                    ',' or ';' or ':' or '"' or '[' or ']' or '?' or '=' or '{' or '}' => '-',
                    _ when c < 0x20 || c >= 0x7F => '-',
                    _ => c,
                };
            }
            return new string(chars);
        }

        private static string ResolveVersion()
        {
            try
            {
                var asm = Assembly.GetEntryAssembly() ?? typeof(EmutasticUserAgent).Assembly;
                var attr = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                if (attr != null && !string.IsNullOrWhiteSpace(attr.InformationalVersion))
                {
                    // Strip any "+commit" suffix MSBuild appends in SourceLink builds.
                    string v = attr.InformationalVersion;
                    int plus = v.IndexOf('+');
                    if (plus > 0) v = v.Substring(0, plus);
                    return v;
                }
                var name = asm.GetName();
                if (name.Version != null)
                    return $"{name.Version.Major}.{name.Version.Minor}.{name.Version.Build}";
            }
            catch { /* fall through */ }
            return "0.0.0";
        }

        private static string? _os;

        private static string ResolveOs()
        {
            if (_os != null) return _os;
            // RA's UA validator parses the OS bracket; "<Name> <Version>" from
            // os-release gives e.g. "Debian 13" (the Linux analogue of
            // upstream's "Windows 11"). Fall back to a kernel-versioned form.
            try
            {
                string? name = null, version = null;
                foreach (var rawLine in File.ReadLines("/etc/os-release"))
                {
                    var line = rawLine.Trim();
                    if (line.StartsWith("NAME=")) name = line.Substring(5).Trim('"');
                    else if (line.StartsWith("VERSION_ID=")) version = line.Substring(11).Trim('"');
                }
                if (!string.IsNullOrEmpty(name))
                    return _os = string.IsNullOrEmpty(version) ? $"{name}" : $"{name} {version}";
            }
            catch { /* fall through */ }
            try
            {
                var v = Environment.OSVersion.Version;
                return _os = $"Linux {v.Major}.{v.Minor}";
            }
            catch { return _os = "Linux"; }
        }
    }
}
