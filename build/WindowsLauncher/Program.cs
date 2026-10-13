using Microsoft.NET.HostModel.AppHost;

if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64 || args.Length != 1)
    throw new ArgumentException("Run with a Windows x64 SDK and the package directory as the only argument.");

string package = Path.GetFullPath(args[0]);
string assembly = Path.Combine(package, "app", "NengliangReader.dll");
if (!File.Exists(assembly)) throw new FileNotFoundException("Publish the application first.", assembly);

// The native host resolves the runtime and dependencies beside this relative DLL.
// It keeps the original command line and process identity, including --server.
HostWriter.CreateAppHost(
    Path.Combine(AppContext.BaseDirectory, "apphost-template.exe"),
    Path.Combine(package, "良子小窗.exe"),
    "app/NengliangReader.dll",
    windowsGraphicalUserInterface: true,
    assemblyToCopyResourcesFrom: assembly);
