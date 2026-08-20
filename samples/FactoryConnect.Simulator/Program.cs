using FactoryConnect.Abstractions;
using FactoryConnect.Simulator;

var machine = MachineId.New();
var connector = new SimulatedMachineConnector(machine);

Console.WriteLine($"FactoryConnect Simulator initialized for machine {machine}.");

for (var i = 0; i < 8; i++)
{
    var snapshot = await connector.ReadSignalsAsync();
    var values = string.Join(", ", snapshot.Signals.Select(signal => $"{signal.Key}={signal.Value}"));
    Console.WriteLine($"{snapshot.Timestamp:O} | {values}");
}
