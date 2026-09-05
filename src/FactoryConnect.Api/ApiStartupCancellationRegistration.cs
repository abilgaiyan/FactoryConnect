using System.Runtime.InteropServices;

namespace FactoryConnect.Api;

internal interface IApiStartupCancellationRegistration : IDisposable
{
    CancellationToken Token { get; }
}

internal sealed class ApiStartupCancellationRegistration :
    IApiStartupCancellationRegistration
{
    private CancellationTokenSource? _source;
    private ConsoleCancelEventHandler? _consoleCancelHandler;
    private PosixSignalRegistration? _sigtermRegistration;

    private ApiStartupCancellationRegistration()
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
            // Console cancellation still provides the pre-activation shutdown path
            // on platforms where POSIX signal registration is unavailable.
        }
    }

    public CancellationToken Token =>
        Volatile.Read(ref _source)?.Token
        ?? throw new ObjectDisposedException(nameof(ApiStartupCancellationRegistration));

    public static IApiStartupCancellationRegistration Create() => new ApiStartupCancellationRegistration();

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
            // Disposal won the race with a process-shutdown callback.
        }
    }
}
