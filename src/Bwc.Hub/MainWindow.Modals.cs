using Bwc.Core;

namespace Bwc.Hub;

public sealed partial class MainWindow
{
    private readonly SemaphoreSlim _modalGate = new(1, 1);
    private readonly CancellationTokenSource _modalClosed = new();
    private int _closed;
    private int _modalReservations;
    private int _modalClosedDisposed;
    private int _modalCloseCancelInProgress;
    private UpdateWork? _activeCheckWork;
    private UpdateWork? _activeDownloadWork;

    private bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Reserves the single owner-modal slot, or returns null after the Hub has closed.</summary>
    private async Task<ModalReservation?> ReserveModalAsync()
    {
        if (IsClosed)
        {
            return null;
        }

        Interlocked.Increment(ref _modalReservations);
        if (IsClosed)
        {
            ReleaseModalWaiter();
            return null;
        }

        try
        {
            await _modalGate.WaitAsync(_modalClosed.Token);
        }
        catch (OperationCanceledException)
        {
            ReleaseModalWaiter();
            return null;
        }

        if (IsClosed)
        {
            _modalGate.Release();
            ReleaseModalWaiter();
            return null;
        }

        return new ModalReservation(this);
    }

    private async Task<bool> ShowHubDialogAsync(Func<Task<bool>> show)
    {
        using ModalReservation? reservation = await ReserveModalAsync();
        return reservation is not null && await show();
    }

    private void MarkClosedAndCancelUpdateWork()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _modalCloseCancelInProgress, 1);
        try
        {
            _modalClosed.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Every waiter already left the modal queue.
        }
        finally
        {
            Interlocked.Exchange(ref _modalCloseCancelInProgress, 0);
        }

        _activeCheckWork?.Cancel();
        _activeDownloadWork?.Cancel();
        DisposeModalCancellationIfDone();
    }

    private void ReleaseModalReservation()
    {
        _modalGate.Release();
        ReleaseModalWaiter();
    }

    private void ReleaseModalWaiter()
    {
        if (Interlocked.Decrement(ref _modalReservations) == 0)
        {
            DisposeModalCancellationIfDone();
        }
    }

    private void DisposeModalCancellationIfDone()
    {
        if (IsClosed && Volatile.Read(ref _modalReservations) == 0 &&
            Volatile.Read(ref _modalCloseCancelInProgress) == 0 &&
            Interlocked.Exchange(ref _modalClosedDisposed, 1) == 0)
        {
            _modalClosed.Dispose();
            _modalGate.Dispose();
        }
    }

    private sealed class ModalReservation : IDisposable
    {
        private MainWindow? _owner;

        public ModalReservation(MainWindow owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseModalReservation();
        }
    }

    /// <summary>
    /// A cancellation source shared by the update worker and its UI waiters. The last waiter releases it only after
    /// the worker has completed, so closing the Hub cannot dispose a source still used by a continuation.
    /// </summary>
    private sealed class UpdateWork
    {
        private readonly CancellationTokenSource _cancel = new();
        private int _references = 1;
        private int _cancelCalls;
        private int _disposed;

        public CancellationToken Token => _cancel.Token;

        public bool IsCancellationRequested => _cancel.IsCancellationRequested;

        public void Retain()
        {
            while (true)
            {
                int references = Volatile.Read(ref _references);
                if (references == 0)
                {
                    throw new ObjectDisposedException(nameof(UpdateWork));
                }

                if (Interlocked.CompareExchange(ref _references, references + 1, references) == references)
                {
                    return;
                }
            }
        }

        public void Cancel()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            Interlocked.Increment(ref _cancelCalls);
            try
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _cancel.Cancel();
                }
            }
            catch (ObjectDisposedException)
            {
                // The worker and every waiter have already released the source.
            }
            catch (Exception ex)
            {
                Log.Warn("Hub update cancellation failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Decrement(ref _cancelCalls);
                DisposeIfDone();
            }
        }

        public void Release()
        {
            Interlocked.Decrement(ref _references);
            DisposeIfDone();
        }

        private void DisposeIfDone()
        {
            if (Volatile.Read(ref _references) == 0 && Volatile.Read(ref _cancelCalls) == 0 &&
                Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _cancel.Dispose();
            }
        }
    }
}
