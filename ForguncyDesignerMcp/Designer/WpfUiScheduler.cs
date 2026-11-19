using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Designer;

internal sealed class WpfUiScheduler(Dispatcher dispatcher) : IUiScheduler
{
    public Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        if (dispatcher.CheckAccess()) return action();
        return dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return action();
        }, DispatcherPriority.Normal, cancellationToken).Task.Unwrap();
    }
}
