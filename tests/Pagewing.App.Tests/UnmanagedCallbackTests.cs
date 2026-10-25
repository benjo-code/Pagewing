using System.Reflection;
using System.Runtime.InteropServices;
using Pagewing.App.ViewModels;
using Pagewing.Core;
using Xunit;

namespace Pagewing.App.Tests;

public sealed class UnmanagedCallbackTests
{
    /// <summary>The native callbacks Pagewing has; each one was checked against the rule below.</summary>
    private static readonly string[] Allowed =
    [
        "Pagewing.Pdfium.PdfiumDocument.ReadBlock",
        "Pagewing.Pdfium.PdfiumDocument.WriteSaveBlock",
        "Pagewing.Pdfium.PdfiumRenderWork.NeedToPauseNow",
    ];

    [Fact]
    public void EveryUnmanagedCallbackIsReviewed()
    {
        // App, Core and Pdfium (the App references both). Reflection is fine here: only the production code is AOT.
        var assemblies = new[]
        {
            typeof(MainViewModel).Assembly,
            typeof(AppInfo).Assembly,
            Assembly.Load("Pagewing.Pdfium"),
        };
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var callbacks = assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(type => type.GetMethods(All).Select(method => (type, method)))
            .Where(entry => entry.method.IsDefined(typeof(UnmanagedCallersOnlyAttribute), inherit: false))
            .Select(entry => $"{entry.type.FullName}.{entry.method.Name}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            callbacks.SequenceEqual(Allowed),
            "The [UnmanagedCallersOnly] methods changed. A callback body only unpacks its arguments and calls a "
            + "[MethodImpl(MethodImplOptions.NoInlining)] managed method; it never reads a clock (Stopwatch.GetTimestamp, "
            + "DateTime.UtcNow, Environment.TickCount64) or calls another SuppressGCTransition P/Invoke inline, because the "
            + "JIT may put that call's GC poll after the reverse P/Invoke exit (Docs/DECISIONS.md, entry \"Absturz "
            + "„UnmanagedCallersOnly aus verwaltetem Code“: Ursache und Behebung (M9)\"). Review the new callback, then "
            + $"update this list. Found: {string.Join(", ", callbacks)}");
    }
}
