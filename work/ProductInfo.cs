using System.Reflection;
[assembly: AssemblyTitle(ProductInfo.Name)]
[assembly: AssemblyProduct(ProductInfo.Name)]
[assembly: AssemblyCompany(ProductInfo.Owner)]
[assembly: AssemblyDescription("Freeware for personal and commercial use.")]
[assembly: AssemblyCopyright("2026 " + ProductInfo.Owner)]
[assembly: AssemblyVersion(ProductInfo.Version + ".0")]
[assembly: AssemblyFileVersion(ProductInfo.Version + ".0")]
[assembly: AssemblyInformationalVersion(ProductInfo.Version)]

static class ProductInfo {
    internal const string Name = "CapKab";
    internal const string Version = "1.0.0";
    internal const string Owner = "thanes roj.";
}











