#nullable enable
using System;

namespace VTX.Tests;

public static class Program
{
    public static int Main(string[] args)
    {
        string? filter = args.Length > 0 ? args[0] : null;
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Godot.OS.TestMainThreadId = (ulong)Environment.CurrentManagedThreadId;
        Godot.Dispatcher.TestContext = MainThread.Instance;
        RuntimeTests.Register();
        TaskTests.Register();
        AnalyzerTests.Register();
        T.RunAll(filter);
        Console.WriteLine($"\n{T.Passed} passed, {T.Failed} failed");
        return T.Failed == 0 ? 0 : 1;
    }
}
