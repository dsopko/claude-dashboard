namespace ClaudeDashboard.App.Storage;

/// <summary>
/// What a start of the dashboard writes into the <c>runs</c> table, beside its time (T1.60, issue #78).
/// </summary>
/// <remarks>
/// The version and the port are the dashboard's own, and the data folder is the
/// one path the table holds, because it says which install wrote the rows.
/// </remarks>
/// <param name="Version">The informational version, as the log's first line gives it (<c>StartupVersion</c>).</param>
/// <param name="Port">The port ingress bound, or null when it could not bind.</param>
/// <param name="DataRoot">The data folder.</param>
public sealed record RunStart(string Version, int? Port, string DataRoot);
