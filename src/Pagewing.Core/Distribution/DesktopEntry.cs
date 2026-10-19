using System.Buffers;
using System.Text;

namespace Pagewing.Core.Distribution;

/// <summary>The <c>.desktop</c> file of Pagewing on Linux (Desktop Entry Specification).</summary>
public static class DesktopEntry
{
    private static readonly SearchValues<char> ReservedInExec = SearchValues.Create(" \t\n\"'\\><~|&;$*?#()`");

    /// <summary>Renders the file for the executable <paramref name="execPath"/>; the packaging template differs only in <c>Exec</c>.</summary>
    /// <param name="execPath">The program to start, such as the AppImage.</param>
    /// <param name="iconName">The icon name without extension.</param>
    /// <returns>The file content with LF line ends.</returns>
    public static string Render(string execPath, string iconName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(execPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(iconName);
        return new StringBuilder()
            .Append("[Desktop Entry]\n")
            .Append("Type=Application\n")
            .Append("Name=Pagewing\n")
            .Append("GenericName=PDF Viewer\n")
            .Append("Comment=Fast, offline PDF viewer\n")
            .Append("Exec=").Append(EscapeExec(execPath)).Append(" %F\n")
            .Append("Icon=").Append(iconName).Append('\n')
            .Append("Terminal=false\n")
            .Append("Categories=Office;Viewer;\n")
            .Append("MimeType=application/pdf;\n")
            .Append("StartupWMClass=Pagewing\n")
            .ToString();
    }

    /// <summary>
    /// Escapes a program path for the <c>Exec</c> key: <c>%</c> is doubled and a path with a reserved character is put in
    /// double quotes, with <c>"</c>, <c>`</c>, <c>$</c> and <c>\</c> escaped (the backslash twice, as a string value is unescaped first).
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The text for <c>Exec=</c>.</returns>
    public static string EscapeExec(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var text = path.Replace("%", "%%", StringComparison.Ordinal);
        if (text.AsSpan().IndexOfAny(ReservedInExec) < 0)
        {
            return text;
        }

        var quoted = new StringBuilder("\"");
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\':
                    quoted.Append("\\\\\\\\");
                    break;
                case '"' or '`' or '$':
                    quoted.Append('\\').Append(c);
                    break;
                default:
                    quoted.Append(c);
                    break;
            }
        }

        return quoted.Append('"').ToString();
    }
}
