namespace Monitor.Identity.Users;

/// <summary>Direct translation of utils/azureAuth.ts's `AzureClaims` interface.</summary>
public sealed record AzureClaims(string Oid, string Email, string Name);
