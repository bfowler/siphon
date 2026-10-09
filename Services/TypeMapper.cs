using System;
using System.Data.SqlTypes;

namespace Siphon.Services;

/// <summary>
/// Static class that maps SQL Server data types to SQLite types.
/// </summary>
public static class TypeMapper
{
    /// <summary>
    /// Converts SQL Server type name to SQLite type.
    /// </summary>
    /// <param name="sqlServerType">The SQL Server data type name (e.g., "varchar(50)", "int").</param>
    /// <returns>The corresponding SQLite type: INTEGER, REAL, TEXT, or BLOB.</returns>
    public static string MapToSqlite(string sqlServerType)
    {
        if (string.IsNullOrWhiteSpace(sqlServerType))
            return "TEXT";

        var cleanType = StripParameters(sqlServerType).ToLowerInvariant();

        return cleanType switch
        {
            "bit" => "INTEGER",
            "tinyint" => "INTEGER",
            "smallint" => "INTEGER",
            "int" => "INTEGER",
            "bigint" => "INTEGER",
            "decimal" => "REAL",
            "numeric" => "REAL",
            "money" => "REAL",
            "smallmoney" => "REAL",
            "float" => "REAL",
            "real" => "REAL",
            "char" => "TEXT",
            "varchar" => "TEXT",
            "text" => "TEXT",
            "nchar" => "TEXT",
            "nvarchar" => "TEXT",
            "ntext" => "TEXT",
            "date" => "TEXT",
            "datetime" => "TEXT",
            "datetime2" => "TEXT",
            "smalldatetime" => "TEXT",
            "datetimeoffset" => "TEXT",
            "uniqueidentifier" => "TEXT",
            "varbinary" => "BLOB",
            "binary" => "BLOB",
            "image" => "BLOB",
            "xml" => "TEXT",
            "sql_variant" => "TEXT",
            _ => "TEXT"
        };
    }

    /// <summary>
    /// Returns true if data needs conversion before storing in SQLite.
    /// </summary>
    /// <param name="sqlServerType">The SQL Server data type name.</param>
    /// <returns>True if the type requires value conversion; otherwise, false.</returns>
    public static bool RequiresConversion(string sqlServerType)
    {
        if (string.IsNullOrWhiteSpace(sqlServerType))
            return false;

        var cleanType = StripParameters(sqlServerType).ToLowerInvariant();

        return cleanType switch
        {
            "decimal" => true,
            "numeric" => true,
            "date" => true,
            "datetime" => true,
            "datetime2" => true,
            "smalldatetime" => true,
            "datetimeoffset" => true,
            "uniqueidentifier" => true,
            "sql_variant" => true,
            _ => false
        };
    }

    /// <summary>
    /// Converts a value if needed for SQLite storage.
    /// </summary>
    /// <param name="value">The value to convert.</param>
    /// <param name="sqlServerType">The SQL Server data type name.</param>
    /// <returns>The converted value appropriate for SQLite storage, or null if the input is null.</returns>
    public static object? ConvertValue(object? value, string sqlServerType)
    {
        if (value == null || value == DBNull.Value || value is INullable { IsNull: true })
            return null;

        if (string.IsNullOrWhiteSpace(sqlServerType))
            return value;

        var cleanType = StripParameters(sqlServerType).ToLowerInvariant();

        return cleanType switch
        {
            "decimal" or "numeric" when value is SqlDecimal sqlDecimal => sqlDecimal.ToDouble(),
            "date" or "datetime" or "datetime2" or "smalldatetime" => ConvertDateTime(value),
            "datetimeoffset" => ConvertDateTimeOffset(value),
            "uniqueidentifier" => ConvertGuid(value),
            "sql_variant" => UnwrapSqlVariant(value).ToString(),
            _ => value
        };
    }

    private static object UnwrapSqlVariant(object value) => value switch
    {
        // Preserve the previous CLR ToString behavior for non-decimal variants.
        // Leave SqlDecimal intact so converting to TEXT never narrows its precision.
        SqlBoolean sql => sql.Value,
        SqlByte sql => sql.Value,
        SqlInt16 sql => sql.Value,
        SqlInt32 sql => sql.Value,
        SqlInt64 sql => sql.Value,
        SqlSingle sql => sql.Value,
        SqlDouble sql => sql.Value,
        SqlMoney sql => sql.Value,
        SqlString sql => sql.Value,
        SqlBinary sql => sql.Value,
        SqlGuid sql => sql.Value,
        SqlDateTime sql => sql.Value,
        _ => value
    };

    private static string StripParameters(string sqlServerType)
    {
        var parenIndex = sqlServerType.IndexOf('(');
        if (parenIndex > 0)
            return sqlServerType.Substring(0, parenIndex).Trim();

        var bracketIndex = sqlServerType.IndexOf('[');
        if (bracketIndex > 0)
            return sqlServerType.Substring(0, bracketIndex).Trim();

        return sqlServerType.Trim();
    }

    private static object ConvertDateTime(object value)
    {
        return value switch
        {
            DateTime dt => dt.ToString("o"),
            DateTimeOffset dto => dto.ToString("o"),
            _ => value
        };
    }

    private static object ConvertDateTimeOffset(object value)
    {
        return value switch
        {
            DateTimeOffset dto => dto.ToString("o"),
            DateTime dt => new DateTimeOffset(dt).ToString("o"),
            _ => value
        };
    }

    private static object ConvertGuid(object value)
    {
        return value switch
        {
            Guid g => g.ToString(),
            _ => value
        };
    }
}
