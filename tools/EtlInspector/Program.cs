using Microsoft.Diagnostics.Tracing;

const string ProviderName = "OtelEtwSpike-Handoff";

if (args.Length != 1)
{
    Console.Error.WriteLine("使用方法: EtlInspector <ETLファイルのパス>");
    return 1;
}

string etlPath = Path.GetFullPath(args[0]);
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
    if (!string.Equals(data.ProviderName, ProviderName, StringComparison.Ordinal))
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
