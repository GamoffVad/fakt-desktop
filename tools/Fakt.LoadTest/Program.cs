using System;
using System.Text;
using System.Threading.Tasks;

namespace Fakt.LoadTest;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        LoadTestOptions options;
        try
        {
            options = LoadTestOptions.Parse(args);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is OverflowException)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(LoadTestOptions.Usage);
            return 2;
        }

        if (options.Help)
        {
            Console.WriteLine(LoadTestOptions.Usage);
            return 0;
        }

        var runner = new LoadTestRunner(options);
        var presses = 0;
        Console.CancelKeyPress += (_, e) =>
        {
            // Первое нажатие — штатная остановка с удалением временной базы; повторное — аварийное завершение.
            if (System.Threading.Interlocked.Increment(ref presses) == 1)
            {
                e.Cancel = true;
                runner.RequestStop();
            }
        };
        return await runner.RunAsync().ConfigureAwait(false);
    }
}
