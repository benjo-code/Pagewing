# Nachstellung für dotnet/runtime: GC-Abfrage hinter dem Austritt eines `[UnmanagedCallersOnly]`-Rückrufs

Vorlage für ein Issue an `dotnet/runtime` (Backlog „M9: bekannte Grenzen und Nacharbeiten“). Hintergrund, Disassembly und Zahlen: `Docs/DECISIONS.md`, Eintrag „Absturz „UnmanagedCallersOnly aus verwaltetem Code“: Ursache und Behebung (M9)“. Das Programm stammt aus der Prüfung der Behebung (Windows x64, .NET 10); dort stürzte die Variante `inline` unter CoreCLR in 5 von 5 und unter Native AOT in 3 von 3 Läufen ab, `noinline` in keinem. Ein Issue ist noch nicht angelegt.

**Erwartet:** Beide Varianten laufen 8 s und geben „survived …“ aus. **Beobachtet:** `inline` endet mit `Fatal error. Invalid Program: attempted to call a UnmanagedCallersOnly method from managed code.` (`0xC0000602`), weil die GC-Abfrage nach dem `SuppressGCTransition`-P/Invoke von `Stopwatch.GetTimestamp` hinter `CORINFO_HELP_JIT_REVERSE_PINVOKE_EXIT` liegt.

Ausführen (Release, sonst wird `GetTimestamp` nicht inline erweitert): `dotnet run -c Release -- inline` bzw. `-- noinline`; Native AOT: `dotnet publish -c Release -r win-x64 -p:PublishAot=true`.

`repro.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
```

`Program.cs`:

```csharp
using System;
using System.Threading;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

unsafe
{
    var mode = args.Length > 0 ? args[0] : "inline";
    var stop = false;
    var gc = new Thread(() => { while (!Volatile.Read(ref stop)) { GC.Collect(0, GCCollectionMode.Forced, true); Thread.SpinWait(500); } }) { IsBackground = true };
    gc.Start();
    long deadline = long.MaxValue;
    delegate* unmanaged<nint, long*, int> cb = mode == "inline" ? &Native.CbInline : &Native.CbNoInline;
    var sw = Stopwatch.StartNew();
    long rounds = 0;
    while (sw.ElapsedMilliseconds < 8000)
    {
        Native.EnumWindows(cb, &deadline);
        rounds++;
    }
    Volatile.Write(ref stop, true);
    gc.Join();
    Console.WriteLine($"{mode}: survived {rounds} EnumWindows, gen0={GC.CollectionCount(0)}");
}

static unsafe class Native
{
    [DllImport("user32.dll")]
    public static extern int EnumWindows(delegate* unmanaged<nint, long*, int> cb, long* param);

    // Same shape as the old PdfiumRenderWork.NeedToPauseNow (returns 1 = continue for EnumWindows).
    [UnmanagedCallersOnly]
    public static int CbInline(nint hwnd, long* deadline) => Stopwatch.GetTimestamp() >= *deadline ? 0 : 1;

    [UnmanagedCallersOnly]
    public static int CbNoInline(nint hwnd, long* deadline) => Now() >= *deadline ? 0 : 1;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static long Now() => Stopwatch.GetTimestamp();
}
```
