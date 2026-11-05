// La libreria usa System.Drawing e Windows Forms: e' solo per Windows. Senza questa dichiarazione
// (il progetto ha GenerateAssemblyInfo=false, quindi non viene creata da sola) il compilatore segnala
// centinaia di avvisi "raggiungibile da tutte le piattaforme".
#if NET5_0_OR_GREATER
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
