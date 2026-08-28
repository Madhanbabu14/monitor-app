namespace Monitor.Files.Domain;

/// <summary>Direct translation of <c>RetriggerResult</c> (features/s3/s3.service.ts).</summary>
public sealed record RetriggerResult(string JobId, string PipelineName, string Status);
