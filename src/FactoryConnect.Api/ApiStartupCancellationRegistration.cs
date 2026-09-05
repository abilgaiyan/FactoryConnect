using System.Runtime.InteropServices;

namespace FactoryConnect.Api;

internal interface IApiStartupCancellationRegistration : IDisposable
{
    CancellationToken Token { get; }
}

internal interface IApiStartupCancellationRegistrationFactory
{
    IApiStartupCancellationRegistration Create();
}

internal sealed class ApiStartupCancellationRegistrationFactory :
    IApiStartupCancellationRegistrationFactory
{
    public IApiStartupCancellationRegistration Create() =>
        new ApiStartupCancellationRegistration();
}

internal sealed class ApiStartupCancellationRegistration :
    IApiStartupCancellationRegistration
{
    private CancellationTokenSource? _source;
    private ConsoleCancelEventHandler? _consoleCancelHandler;
    private PosixSignalRegistration? _sigtermRegistration;

    internal ApiStartupCancellationRegistration()
    {
        _source = new CancellationTokenSource();
        _consoleCancelHandler = OnConsoleCancelKeyPress;
        Console.CancelKeyPress += _consoleCancelHandler;

        try
        {
            _sigtermRegistration = PosixSignalRegistration.Create(
                PosixSignal.SIGTERM,
                OnSigTerm);
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    public CancellationToken Token =>
        Volatile.Read(ref _source)?.Token
        ?? throw new ObjectDisposedException(nameof(ApiStartupCancellationRegistration));

    public void Dispose()
    {
        var consoleCancelHandler = Interlocked.Exchange(
            ref _consoleCancelHandler,
            null);
        if (consoleCancelHandler is not null)
        {
            Console.CancelKeyPress -= consoleCancelHandler;
        }

        Interlocked.Exchange(ref _sigtermRegistration, null)?.Dispose();
        Interlocked.Exchange(ref _source, null)?.Dispose();
    }

    private void OnConsoleCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
    {
        eventArgs.Cancel = true;
        Cancel();
    }

    private void OnSigTerm(PosixSignalContext context)
    {
        context.Cancel = true;
        Cancel();
    }

    private void Cancel()
    {
        var source = Volatile.Read(ref _source);
        if (source is null)
        {
            return;
        }

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
