namespace MiCamera.Net.Abstractions.ConfigSettings;

/// <summary>One-time startup gate for hosts configured through the web UI.</summary>
public sealed class ServerInitialization
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool WebManaged { get; set; }
    public bool Configured => !this.WebManaged || this._ready.Task.IsCompletedSuccessfully;

    public Task WaitAsync(CancellationToken cancellationToken) => this.WebManaged
        ? this._ready.Task.WaitAsync(cancellationToken) : Task.CompletedTask;

    public void Complete() => this._ready.TrySetResult();
}
