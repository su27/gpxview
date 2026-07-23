using System.Reflection;

namespace GpxView.App;

internal static class BuildInfo
{
#if GPXVIEW_STORE
    public const string Channel = "Store";
    public const bool SupportsTianditu = false;
#else
    public const string Channel = "GitHub";
    public const bool SupportsTianditu = true;
#endif

    public static string Version { get; } =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0]
        ?? typeof(BuildInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}
