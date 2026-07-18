using LegacyLogCorrelation;

CorrelationResult log4Net = await LegacyLogCorrelationDemo.WriteLog4NetAsync();
CorrelationResult nlog = await LegacyLogCorrelationDemo.WriteNLogAsync();

Console.Write(log4Net.Output);
Console.Write(nlog.Output);
