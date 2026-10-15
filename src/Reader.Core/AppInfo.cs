using System.Reflection;

namespace Reader.Core;

public static class AppInfo
{
    // 与 git 标签同源：Directory.Build.props 的 <Version>，去掉可能附带的 +commit 元数据。
    public static string Version { get; } = typeof(AppInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
}
