namespace dapps.core.tests;

/// <summary>
/// xUnit test collection used to serialise classes that mutate the static
/// <c>DbInfo.OverridePath</c>. Without this they race when run in parallel
/// and one test's connection ends up pointing at another test's file.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SqliteOverridePathCollection
{
    public const string Name = "sqlite-override-path";
}
