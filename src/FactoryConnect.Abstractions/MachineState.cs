namespace FactoryConnect.Abstractions;

public enum MachineState
{
    Unknown = 0,
    Stopped = 1,
    Idle = 2,
    Running = 3,
    Fault = 4,
    Offline = 5,
}
