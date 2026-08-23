using FactoryConnect.Protocols.MTConnect;

namespace FactoryConnect.Edge;

public sealed class MtConnectAcquisitionSessionFactory(
    MtConnectSampleClient client)
    : IMtConnectAcquisitionSessionFactory
{
    public MtConnectAcquisitionSession Create(
        ulong fromSequence)
    {
        return new MtConnectAcquisitionSession(
            client,
            fromSequence);
    }
}
