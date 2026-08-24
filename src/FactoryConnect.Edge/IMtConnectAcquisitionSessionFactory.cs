using FactoryConnect.Protocols.MTConnect;

namespace FactoryConnect.Edge;

public interface IMtConnectAcquisitionSessionFactory
{
    MtConnectAcquisitionSession Create(ulong fromSequence);

    MtConnectAcquisitionSession Restore(
        ulong instanceId,
        ulong nextSequence);
}
