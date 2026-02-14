# Siphon

A simple command-line tool for extracting tables from Microsoft SQL Server and loading them into SQLite databases.

## The Problem

SQL Server is powerful, but sometimes you need your data somewhere more portable:

- **Development & Testing** - Spin up a local test environment without needing a full SQL Server instance
- **Data Analysis** - Share datasets with analysts who use SQLite tools like DB Browser or Datasette
- **Offline Access** - Take your data on the go without network dependencies
- **Cross-Platform Sharing** - SQLite runs everywhere - Windows, macOS, Linux, mobile, even in browsers
- **Archival** - Create portable snapshots of specific tables for long-term storage
- **Migration Prep** - Extract and inspect data before migrating to other systems

Siphon bridges this gap by making SQL Server-to-SQLite extraction simple and repeatable.

## Installation

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later
- Access to a SQL Server instance (local or remote)

### Build from Source

```bash
# Clone the repository
git clone https://github.com/bfowler/siphon.git
cd siphon

# Build the project
dotnet build

# Run the application
siphon --help
```

### Create a Release Build

```bash
# Build a self-contained executable for your platform
dotnet publish -c Release -r osx-arm64 --self-contained

# The executable will be in bin/Release/net10.0/osx-arm64/publish/
```

Available RIDs: `win-x64`, `win-arm64`, `osx-x64`, `osx-arm64`, `linux-x64`, `linux-arm64`

## Usage

### Command Line Options

| Option | Short | Description |
|--------|-------|-------------|
| `--server` | `-s` | SQL Server hostname or instance name |
| `--database` | `-d` | Source database name |
| `--table` | `-t` | Table name(s) to extract (format: `schema.table`, can specify multiple) |
| `--output` | `-o` | Output SQLite file path |
| `--username` | `-u` | SQL authentication username (optional) |
| `--password` | `-p` | SQL authentication password (optional) |
| `--windows-auth` | `-w` | Use Windows integrated authentication |
| `--overwrite` | | Overwrite existing SQLite file |
| `--help` | `-h` | Show help information |

### Interactive Mode

If you omit required arguments, Siphon will prompt you for them:

```bash
siphon
# Prompts for: server, database, table(s), output path
# Prompts for credentials if not using Windows auth
```

### Examples

#### Extract a Single Table (Windows Authentication)

```bash
siphon -s localhost -d MyDatabase -t dbo.Customers -o data.db -w
```

#### Extract Multiple Tables

```bash
siphon -s localhost -d MyDatabase -t dbo.Customers -t dbo.Orders -t dbo.Products -o data.db -w
```

#### Use SQL Server Authentication

```bash
siphon -s myserver.example.com -d ProductionDB -t sales.Orders -o orders.db -u myuser -p mypassword
```

#### Specify Full Instance Name

```bash
siphon -s "MYSERVER\SQLEXPRESS" -d MyDatabase -t dbo.Users -o users.db -w
```

#### Overwrite an Existing File

```bash
siphon -s localhost -d MyDatabase -t dbo.Logs -o logs.db -w --overwrite
```

## How It Works

1. **Connect** - Establishes a connection to your SQL Server instance using Windows or SQL authentication
2. **Extract Schema** - Reads table structure including columns, data types, and primary keys
3. **Type Mapping** - Converts SQL Server data types to appropriate SQLite equivalents
4. **Create Destination** - Creates or updates the SQLite database file
5. **Transfer Data** - Streams data in batches for efficient memory usage, even with large tables

### Type Mapping

| SQL Server Type | SQLite Type |
|-----------------|-------------|
| `bit`, `tinyint`, `smallint`, `int`, `bigint` | `INTEGER` |
| `decimal`, `numeric`, `money`, `float`, `real` | `REAL` |
| `char`, `varchar`, `nchar`, `nvarchar`, `text` | `TEXT` |
| `date`, `datetime`, `datetime2`, `smalldatetime` | `TEXT` (ISO8601) |
| `uniqueidentifier` | `TEXT` |
| `varbinary`, `binary` | `BLOB` |

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
