namespace Monitor.Files.Domain;

/// <summary>Direct translation of the source's <c>RetriggerResult</c> interface (s3.service.ts).</summary>
public sealed record RetriggerResult(string JobId, string PipelineName, string Status);
