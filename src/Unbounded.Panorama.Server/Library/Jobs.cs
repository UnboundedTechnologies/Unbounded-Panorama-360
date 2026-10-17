using System.Collections.Concurrent;
using Unbounded.Panorama.Media;

namespace Unbounded.Panorama.Server.Library;

/// <summary>A long task the web page can follow (an upload being analysed, a YouTube download).</summary>
public sealed class Job : IProgress<ProgressInfo>
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public required string Title { get; init; }
    public string Text { get; private set; } = "Starting…";
    public double? Fraction { get; private set; }

    /// <summary>Size, speed and time left while downloading.</summary>
    public string? Detail { get; private set; }
    public string State { get; private set; } = "running";
    public string? Error { get; private set; }
    public string? ResultId { get; private set; }
    public DateTime Started { get; } = DateTime.UtcNow;
    public DateTime? Finished { get; private set; }

    internal CancellationTokenSource Cancel { get; } = new();

    public void Report(ProgressInfo value)
    {
        Text = value.Text;
        Fraction = value.Fraction;
        Detail = value.Detail;
    }

    internal void Done(string? resultId)
    {
        ResultId = resultId;
        State = "done";
        Text = "Done";
        Fraction = 1;
        Finished = DateTime.UtcNow;
    }

    internal void Failed(Exception ex)
    {
        State = ex is OperationCanceledException ? "cancelled" : "failed";
        Error = ex is OperationCanceledException ? null : ex.Message;
        Finished = DateTime.UtcNow;
    }
}

public sealed class JobList
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new();

    public IEnumerable<Job> All()
    {
        // Finished jobs stay visible for a minute.
        foreach (var (id, job) in _jobs)
            if (job.Finished is { } f && DateTime.UtcNow - f > TimeSpan.FromMinutes(1))
                _jobs.TryRemove(id, out _);
        return _jobs.Values.OrderBy(j => j.Started);
    }

    public Job Start(string title, Func<Job, CancellationToken, Task<string?>> work, ILogger log)
    {
        var job = new Job { Title = title };
        _jobs[job.Id] = job;
        _ = Task.Run(async () =>
        {
            try
            {
                job.Done(await work(job, job.Cancel.Token));
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                    log.LogWarning("{Title}: {Message}", title, ex.Message);
                job.Failed(ex);
            }
        });
        return job;
    }

    public bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var job))
            return false;
        job.Cancel.Cancel();
        return true;
    }
}
