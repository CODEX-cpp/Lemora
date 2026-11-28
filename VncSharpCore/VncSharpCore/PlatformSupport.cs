// The library uses System.Drawing and Windows Forms: it is Windows only. Without this declaration
// (the project has GenerateAssemblyInfo=false, so it is not created automatically) the compiler reports
// hundreds of "reachable on all platforms" warnings.
#if NET5_0_OR_GREATER
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
