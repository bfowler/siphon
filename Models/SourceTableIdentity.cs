namespace Siphon.Models;

internal sealed record SourceTableIdentity(int ObjectId, string SchemaName, string TableName)
{
    public string DisplayName => $"{SchemaName}.{TableName}";
}
