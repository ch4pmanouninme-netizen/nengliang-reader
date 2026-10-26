using System.Reflection;

namespace Reader.Core;

/// <summary>
/// 应用版本信息：从程序集信息版本中读取，供标题栏与“关于”界面显示。
/// </summary>
public static class AppInfo
{
    // 与 git 标签同源：Directory.Build.props 的 <Version>，去掉可能附带的 +commit 元数据。
    /// <summary>
    /// 语义化版本号（如 “2.3.8”）。构建时可能附加 “+提交哈希” 元数据，这里截断只保留版本部分。
    /// </summary>
    /// <remarks>静态属性只初始化一次，因此运行期间版本号不会变化。</remarks>
    public static string Version { get; } = typeof(AppInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
}
