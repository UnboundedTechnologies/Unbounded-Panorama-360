using Xunit;

// Several tests count running ffmpeg processes or time conversions: run them one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
