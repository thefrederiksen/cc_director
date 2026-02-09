namespace ConsoleDiagnostic;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine();
        Console.WriteLine("=== Windows 11 Console Diagnostic ===");
        Console.WriteLine();
        Console.WriteLine($"Running on: {Environment.OSVersion}");
        Console.WriteLine($"Process ID: {Environment.ProcessId}");
        Console.WriteLine();

        var tests = new DiagnosticTests();

        try
        {
            // Test 1: Registry Check
            tests.TestRegistryCheck();

            // Test 2: Process Launch Test
            tests.TestProcessLaunch();

            // Test 3: Process Tree Walk
            tests.TestProcessTreeWalk();

            // Test 4: Window Enumeration
            tests.TestWindowEnumeration();

            // Test 5: Legacy Conhost Force Test
            tests.TestLegacyConhostForce();

            // Test 6: Unique Title Test
            tests.TestUniqueTitleFindWindow();

            Console.WriteLine("=== Diagnostic Complete ===");
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }

        Console.WriteLine("Press any key to exit...");
        Console.ReadKey(true);
    }
}
