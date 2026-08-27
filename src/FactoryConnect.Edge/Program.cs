using FactoryConnect.Edge;

var builder = Host.CreateApplicationBuilder(args);
var machineInventory = MtConnectMachineInventory.FromConfiguration(
    builder.Configuration);
var activityStreams = machineInventory.ActivityStreams;
var machineIds = machineInventory.MachineIds;

builder.Services.AddFactoryConnectEdgePersistence(
    builder.Configuration);
builder.Services.AddFactoryConnectObservationProcessing(
    builder.Configuration,
    activityStreams);
builder.Services.AddFactoryConnectProductionMetricInputs(
    builder.Configuration,
    activityStreams);
builder.Services.AddFactoryConnectMetricAggregation(
    builder.Configuration,
    machineIds);
builder.Services.AddFactoryConnectMtConnectAcquisition(
    builder.Configuration,
    machineInventory);

await builder.Build().RunAsync();
