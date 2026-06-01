using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace NetworkingTool.Tray;

/// <summary>
/// Loads the application icon that is embedded as a managed resource (see the EmbeddedResource in
/// the .csproj). Loading from the multi-resolution <c>.ico</c> with an explicit size lets WinForms
/// pick the frame closest to the requested size, so the tray (small) and window (large) icons stay
/// crisp instead of being scaled from a single frame. If the resource is somehow missing, it falls
/// back to the system application icon rather than throwing.
/// </summary>
internal static class AppIcon
{
    // Matches the LogicalName given to the EmbeddedResource in NetworkingTool.csproj.
    private const string ResourceName = "NetworkingTool.icon.ico";

    public static Icon Load(Size size)
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        return stream is null
            ? (Icon)SystemIcons.Application.Clone()
            : new Icon(stream, size);
    }
}
