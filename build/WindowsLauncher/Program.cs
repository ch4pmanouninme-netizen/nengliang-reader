using Microsoft.NET.HostModel.AppHost;

// 生成 Windows 原生启动器「良子小窗.exe」：用官方 AppHost 模板嵌入 app/NengliangReader.dll，
// 命令行参数和进程身份原样保留，因此 --server / --data 等行为与直接运行 dll 一致。
// 只支持 Windows x64 的 SDK，且必须且只能传入包目录。
if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64 || args.Length != 1)
    throw new ArgumentException("Run with a Windows x64 SDK and the package directory as the only argument.");

string package = Path.GetFullPath(args[0]);
string assembly = Path.Combine(package, "app", "NengliangReader.dll");
// 启动器依赖同目录 app/ 下已发布的程序集，先发布再生成。
if (!File.Exists(assembly)) throw new FileNotFoundException("Publish the application first.", assembly);

// The native host resolves the runtime and dependencies beside this relative DLL.
// It keeps the original command line and process identity, including --server.
// GUI 子系统避免闪出控制台窗口；图标等资源从主程序集复制。
HostWriter.CreateAppHost(
    Path.Combine(AppContext.BaseDirectory, "apphost-template.exe"),
    Path.Combine(package, "良子小窗.exe"),
    "app/NengliangReader.dll",
    windowsGraphicalUserInterface: true,
    assemblyToCopyResourcesFrom: assembly);
