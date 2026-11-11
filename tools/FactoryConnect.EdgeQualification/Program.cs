using FactoryConnect.EdgeQualification;

if (args.Length != 4 || !int.TryParse(args[2], out var seconds) || seconds is < 10 or > 120)
{
    Console.Error.WriteLine("Usage: EdgeQualification <commissioned-json> <Edge-content-root> <seconds:10..120> <new-report-json>");
    return 2;
}

return await QualificationHarness.RunAsync(args[0], args[1], seconds, args[3]);
