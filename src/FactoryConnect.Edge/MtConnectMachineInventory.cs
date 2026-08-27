using System.Globalization;
using FactoryConnect.Abstractions;
using FactoryConnect.Protocols.MTConnect;
using Microsoft.Extensions.Configuration;

namespace FactoryConnect.Edge;

public sealed class MtConnectMachineInventory
{
    private readonly IReadOnlyList<MtConnectAcquisitionOptions> _machines;
    private readonly IReadOnlyList<ObservationStreamId> _activityStreams;
    private readonly IReadOnlyList<MachineId> _machineIds;

    public MtConnectMachineInventory(
        IReadOnlyList<MtConnectAcquisitionOptions> machines)
    {
        ArgumentNullException.ThrowIfNull(machines);

        if (machines.Count == 0)
        {
            throw new ArgumentException(
                "At least one MTConnect machine must be configured.",
                nameof(machines));
        }

        var snapshot = machines.ToArray();
        if (snapshot.Select(static item => item.MachineId).Distinct().Count() !=
            snapshot.Length)
        {
            throw new ArgumentException(
                "MTConnect machine identifiers must be unique.",
                nameof(machines));
        }

        _machines = Array.AsReadOnly(snapshot);
        _activityStreams = Array.AsReadOnly(
            snapshot
                .Select(static options => MtConnectObservationStreamId.Create(
                    options.MachineId,
                    options.DeviceKey))
                .ToArray());
        _machineIds = Array.AsReadOnly(
            snapshot.Select(static options => options.MachineId).ToArray());
    }

    public IReadOnlyList<MtConnectAcquisitionOptions> Machines => _machines;

    public IReadOnlyList<ObservationStreamId> ActivityStreams => _activityStreams;

    public IReadOnlyList<MachineId> MachineIds => _machineIds;

    public static MtConnectMachineInventory FromConfiguration(
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetRequiredSection("MTConnect");
        var machineSections = section.GetSection("Machines").GetChildren().ToArray();
        if (machineSections.Length == 0)
        {
            return new MtConnectMachineInventory(
                [CreateOptions(section)]);
        }

        return new MtConnectMachineInventory(
            machineSections.Select(CreateOptions).ToArray());
    }

    private static MtConnectAcquisitionOptions CreateOptions(
        IConfigurationSection section) =>
        new(
            new MtConnectEndpoint(
                new Uri(
                    Required(section, "BaseUri"),
                    UriKind.Absolute)),
            new MachineId(Guid.Parse(Required(section, "MachineId"))),
            Required(section, "DeviceKey"),
            ulong.Parse(
                Required(section, "FromSequence"),
                CultureInfo.InvariantCulture),
            TimeSpan.Parse(
                Required(section, "PollingInterval"),
                CultureInfo.InvariantCulture));

    private static string Required(
        IConfigurationSection section,
        string key) =>
        section[key] ?? throw new InvalidOperationException(
            $"{section.Path}:{key} is required.");
}
