Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] GVPPro.ImageAcquisitionService v1.0.2 - Mock service running...");
Console.WriteLine("Press Ctrl+C to stop.");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    while (!cts.Token.IsCancellationRequested)
    {
        await Task.Delay(30000, cts.Token);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Heartbeat - Acquisition Service alive");
    }
}
catch (OperationCanceledException) { }

Console.WriteLine("Service shutting down.");
