using System.Reflection;

namespace UniShare.Core
{
    /// <summary>
    /// 程序版本信息（编译日期版本号，见 src/Directory.Build.props）
    /// </summary>
    public static class AppVersionInfo
    {
        /// <summary>
        /// 当前程序版本，例如 "2026.10.26"
        /// </summary>
        public static string Current { get; } = Resolve();

        private static string Resolve()
        {
            Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(AppVersionInfo).Assembly;
            string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                int plus = informational.IndexOf('+');
                return plus >= 0 ? informational.Substring(0, plus) : informational;
            }
            return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }
}
