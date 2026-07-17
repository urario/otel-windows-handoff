using Microsoft.Diagnostics.Tracing;

const string DefaultProviderName = "OtelWindowsHandoff-Handoff";

if (args.Length is not 1 and not 3 || (args.Length == 3 && args[1] != "--provider"))
{
    Console.Error.WriteLine("使用方法: EtlInspector <ETLファイルのパス> [--provider <プロバイダー名>]");
    return 1;
}

string etlPath = Path.GetFullPath(args[0]);
string providerName = args.Length == 3 ? args[2] : DefaultProviderName;
if (!File.Exists(etlPath))
{
    Console.Error.WriteLine($"ETLファイルが見つかりません: {etlPath}");
    return 1;
}

using var source = new ETWTraceEventSource(etlPath);
int startedCount = 0;
int completedCount = 0;

source.Dynamic.All += data =>
{
    if (!string.Equals(data.ProviderName, providerName, StringComparison.Ordinal))
    {
        return;
    }

    switch (data.EventName)
    {
        case "JobStarted":
            startedCount++;
            break;
        case "JobCompleted":
            completedCount++;
            break;
        default:
            return;
    }

    IEnumerable<string> payload = Enumerable.Range(0, data.PayloadNames.Length)
        .Select(index => $"{data.PayloadNames[index]}={data.PayloadValue(index)}");
    Console.WriteLine($"{data.EventName}\t{string.Join(";", payload)}");
};

source.Process();

Console.WriteLine();
Console.WriteLine($"JobStarted={startedCount}");
Console.WriteLine($"JobCompleted={completedCount}");
Console.WriteLine($"Total={startedCount + completedCount}");
return 0;
